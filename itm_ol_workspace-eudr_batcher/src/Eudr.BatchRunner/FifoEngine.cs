using System.Data;
using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Models;
using Microsoft.Extensions.Logging;

namespace Eudr.BatchRunner;

/// <summary>
/// The shared FIFO consume loop used by RZU (§9.5).
/// Walks available RPR/PKS sources in ID-ascending order, takes min(available, remaining),
/// returns the consumed allocations.  Callers insert ledger rows; this engine only
/// computes the allocations and advances the ILOSC_ROZ counters on each source row.
/// </summary>
public sealed class FifoEngine(IEudrRepository repo, ILogger<FifoEngine> logger)
{
    private const decimal ShortageThreshold = 0.001m;

    public record Allocation(
        FifoSource Source,
        decimal TakenQty
    );

    public record ConsumeResult(
        IReadOnlyList<Allocation> Allocations,
        decimal Shortage    // > 0 means BRAK SUROWCA
    );

    /// <summary>
    /// Consume up to <paramref name="required"/> units of <paramref name="zamiennikKod"/>
    /// from available FIFO sources for <paramref name="seriaId"/>.
    /// Advances ILOSC_ROZ on each consumed source row within the provided transaction.
    /// </summary>
    public async Task<ConsumeResult> ConsumeAsync(
        string zamiennikKod,
        int seriaId,
        decimal required,
        IDbConnection conn,
        IDbTransaction tx,
        CancellationToken ct = default)
    {
        var sources = await repo.GetFifoSourcesAsync(zamiennikKod, seriaId, conn, tx, ct);
        var allocations = new List<Allocation>();
        var remaining = required;

        foreach (var source in sources)
        {
            if (remaining <= ShortageThreshold) break;

            var take = Math.Min(source.Available, remaining);
            if (take <= 0) continue;

            await repo.AdvanceIloscRozAsync(source.Id, take, conn, tx, ct);
            allocations.Add(new Allocation(source, take));
            remaining -= take;

            logger.LogDebug(
                "FIFO: consumed {Take} from source {SourceId} ({Kod}, seria {Seria}); remaining={Remaining}",
                take, source.Id, zamiennikKod, seriaId, remaining);
        }

        var shortage = remaining > ShortageThreshold ? remaining : 0m;
        return new ConsumeResult(allocations, shortage);
    }
}
