using Eudr.BatchRunner.Exceptions;
using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Models;
using Eudr.BatchRunner.Reconciliation;
using Eudr.BatchRunner.Repository;
using Microsoft.Extensions.Logging;

namespace Eudr.BatchRunner;

public sealed class BatchRunnerService(
    IEudrRepository repo,
    IConnectionFactory connectionFactory,
    Dispatcher dispatcher,
    ReconciliationService reconciliation,
    ILogger<BatchRunnerService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>
    /// Returns false if a run is already in progress (caller should 409).
    /// Otherwise kicks off RunPendingAsync in the background and returns true immediately.
    /// </summary>
    public bool TryStartPending(CancellationToken ct = default)
    {
        if (!_gate.Wait(0)) return false;

        _ = Task.Run(async () =>
        {
            try { await RunPendingAsync(ct); }
            catch (Exception ex) { logger.LogCritical(ex, "Unhandled failure in background batch run"); }
            finally { _gate.Release(); }
        }, ct);

        return true;
    }

    private async Task RunPendingAsync(CancellationToken ct)
    {
        var pendingDates = await repo.GetPendingDatesAsync(ct);

        if (pendingDates.Count == 0)
        {
            logger.LogInformation("No pending business dates found — nothing to do");
            return;
        }

        logger.LogInformation("Found {Count} pending date(s): {Dates}",
            pendingDates.Count, string.Join(", ", pendingDates));

        foreach (var date in pendingDates)
        {
            if (ct.IsCancellationRequested) break;
            await RunAsync(date, ct);
        }
    }

    public async Task<RunReport> RunAsync(DateOnly businessDate, CancellationToken ct = default)
    {
        // ── Step 1: Claim ──────────────────────────────────────────────────────
        var run = await repo.TryClaimRunAsync(businessDate, ct);
        if (run is null)
        {
            logger.LogWarning("Business date {Date} already settled — skipping", businessDate);
            return RunReport.AlreadyDone(businessDate);
        }

        logger.LogInformation("Starting EUDR batch run {RunId} for {Date}", run.RunId, businessDate);

        // ── Step 2: Load + validate event sequence ─────────────────────────────
        var events = await repo.GetDayEventsAsync(businessDate, ct);

        if (!ValidateSequence(events, run.RunId))
        {
            await repo.CompleteRunAsync(run.RunId, RunStatus.DoneWithErrors, ct);
            throw new InvalidOperationException(
                $"Event sequence validation failed for {businessDate} — run {run.RunId} aborted.");
        }

        logger.LogInformation("Loaded {Count} events for {Date}", events.Count, businessDate);

        // ── Step 3: Settle each event ──────────────────────────────────────────
        var items = new List<EudrRunItem>(events.Count);

        foreach (var evt in events)
        {
            if (ct.IsCancellationRequested) break;
            var item = await SettleEventAsync(evt, run.RunId, ct);
            items.Add(item);
        }

        // ── Step 4: Reconcile ──────────────────────────────────────────────────
        var report = await reconciliation.ReconcileAsync(run.RunId, businessDate, items, ct);

        // ── Step 5: Complete ───────────────────────────────────────────────────
        var finalStatus = report.FailedCount == 0 && report.JournalGaps.Count == 0
            ? RunStatus.Done
            : RunStatus.DoneWithErrors;

        await repo.CompleteRunAsync(run.RunId, finalStatus, ct);

        logger.LogInformation("Run {RunId} completed with status {Status}", run.RunId, finalStatus);
        return report;
    }

    // ── Sequence validation (§5) ───────────────────────────────────────────────

    private bool ValidateSequence(IReadOnlyList<EudrEvent> events, int runId)
    {
        if (events.Count == 0) return true;

        var prev = events[0].OccurrenceSequence - 1;
        foreach (var evt in events)
        {
            if (evt.OccurrenceSequence != prev + 1)
            {
                logger.LogError(
                    "Run {RunId}: sequence break — expected {Expected}, got {Actual} (event {EventId})",
                    runId, prev + 1, evt.OccurrenceSequence, evt.EventId);
                return false;
            }
            prev = evt.OccurrenceSequence;
        }
        return true;
    }

    // ── Per-event settlement ───────────────────────────────────────────────────

    private async Task<EudrRunItem> SettleEventAsync(EudrEvent evt, int runId, CancellationToken ct)
    {
        using var conn = connectionFactory.Create();
        using var tx = conn.BeginTransaction();

        try
        {
            var handler = dispatcher.Resolve(evt);
            var result = await handler.HandleAsync(evt, conn, tx, ct);

            var item = new EudrRunItem(runId, evt.EventId, evt.MagDokNagId,
                evt.EudrType, evt.Direction, result.Outcome, result.Notes);
            await repo.RecordRunItemAsync(item, conn, tx, ct);

            tx.Commit();

            logger.LogDebug("Settled event {EventId} ({Type}/{Dir}) → {Outcome}",
                evt.EventId, evt.EudrType, evt.Direction, result.Outcome);

            return item;
        }
        catch (BusinessException bex)
        {
            tx.Rollback();
            logger.LogError(bex,
                "Business exception settling event {EventId} doc {DocId} ({Type}/{Dir})",
                evt.EventId, evt.MagDokNagId, evt.EudrType, evt.Direction);

            return new EudrRunItem(runId, evt.EventId, evt.MagDokNagId,
                evt.EudrType, evt.Direction, RunItemOutcome.BusinessException, bex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            tx.Rollback();
            logger.LogError(ex,
                "Technical failure settling event {EventId} doc {DocId} ({Type}/{Dir})",
                evt.EventId, evt.MagDokNagId, evt.EudrType, evt.Direction);

            return new EudrRunItem(runId, evt.EventId, evt.MagDokNagId,
                evt.EudrType, evt.Direction, RunItemOutcome.TechnicalFailure, ex.Message);
        }
    }
}
