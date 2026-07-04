using System.Data;
using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Models;
using Microsoft.Extensions.Logging;

namespace Eudr.BatchRunner.Handlers;

/// <summary>
/// Handles type 300 FORWARD — DDS re-book between series (PKS). (§9.4)
/// No document; inputs come from the PKS event payload.
/// Cross-series: correctness depends on event ordering (journal sequence §5), not partitioning.
/// </summary>
public sealed class PksHandler(IEudrRepository repo, ILogger<PksHandler> logger) : IHandler
{
    public EudrEventType EventType => EudrEventType.Pks;
    public EudrDirection Direction => EudrDirection.Forward;

    public async Task<HandlerResult> HandleAsync(EudrEvent evt, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        if (await repo.IsAlreadySettledAsync(evt.MagDokNagId, EventType, Direction, conn, tx, ct))
            return new HandlerResult(RunItemOutcome.Skipped);

        // TODO: read ZAMIENNIK_KOD/name from ARTYKUL (artykul_id from event payload)
        // TODO: find last RPR/PKS for (kod, seria_src) WITH LOCK; require available >= ilosc
        // TODO: advance source ILOSC_ROZ += ilosc
        // TODO: insert PKS in seria_dst: RAW_MATERIAL_ILOSC=ilosc, ILOSC_ROZ=0, DDS copied
        throw new NotImplementedException("PKS handler — implementation pending");
    }
}
