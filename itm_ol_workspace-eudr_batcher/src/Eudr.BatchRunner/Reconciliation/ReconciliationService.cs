using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Models;
using Microsoft.Extensions.Logging;

namespace Eudr.BatchRunner.Reconciliation;

/// <summary>
/// End-of-run reconciliation: every EUDR_EVENT_JOURNAL row for the day must have
/// a settled outcome in EUDR_RUN_ITEM.  Reports gaps, shortages, and failures. (§8 step 4, §13b)
/// </summary>
public sealed class ReconciliationService(IEudrRepository repo, ILogger<ReconciliationService> logger)
{
    public async Task<RunReport> ReconcileAsync(
        int runId,
        DateOnly businessDate,
        IReadOnlyList<EudrRunItem> items,
        CancellationToken ct = default)
    {
        var gaps = await repo.GetJournalGapsAsync(businessDate, runId, ct);

        if (gaps.Count > 0)
            logger.LogError(
                "RECONCILIATION: {GapCount} journal events for {Date} have no settled outcome: {Gaps}",
                gaps.Count, businessDate, string.Join(", ", gaps));

        var ok = items.Count(i => i.Outcome == RunItemOutcome.Ok);
        var shortages = items.Count(i => i.Outcome == RunItemOutcome.Shortage);
        var skipped = items.Count(i => i.Outcome == RunItemOutcome.Skipped);
        var failed = items.Where(i => i.Outcome is RunItemOutcome.BusinessException
                                                 or RunItemOutcome.TechnicalFailure).ToList();

        logger.LogInformation(
            "Run {RunId} [{Date}] — OK={Ok} SHORTAGE={Shortage} SKIPPED={Skipped} FAILED={Failed} GAPS={Gaps}",
            runId, businessDate, ok, shortages, skipped, failed.Count, gaps.Count);

        foreach (var failure in failed)
            logger.LogError(
                "FAILED event {EventId} doc {DocId} ({Type}/{Dir}): {Notes}",
                failure.EventId, failure.MagDokNagId, failure.EventType, failure.Direction, failure.Notes);

        return new RunReport(runId, businessDate, ok, shortages, skipped, failed.Count, failed, gaps);
    }
}
