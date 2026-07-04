using System.Data;
using Eudr.BatchRunner.Exceptions;
using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Models;
using Microsoft.Extensions.Logging;

namespace Eudr.BatchRunner.Handlers;

/// <summary>
/// Reverse type 1 — reversal of a PZ receipt (§10).
/// BR4 backstop: raises ComplianceViolationException if any RPR has already consumed from this receipt.
/// Otherwise inserts a compensating RIN row with negated quantity.
/// </summary>
public sealed class RinReverseHandler(IEudrRepository repo, ILogger<RinReverseHandler> logger) : IHandler
{
    public EudrEventType EventType => EudrEventType.Rin;
    public EudrDirection Direction => EudrDirection.Reverse;

    public async Task<HandlerResult> HandleAsync(EudrEvent evt, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        if (await repo.IsAlreadySettledAsync(evt.MagDokNagId, EventType, Direction, conn, tx, ct))
            return new HandlerResult(RunItemOutcome.Skipped);

        var rinRows = await repo.GetLedgerRowsByDocAndTypeAsync(evt.MagDokNagId, "RIN", conn, tx, ct);

        if (rinRows.Count == 0)
        {
            logger.LogWarning("RIN-reverse: no forward RIN rows found for doc {Doc} — nothing to compensate", evt.MagDokNagId);
            return new HandlerResult(RunItemOutcome.Ok);
        }

        foreach (var rin in rinRows)
        {
            // BR4: refuse reversal if any quantity was already issued (ILOSC_ROZ > 0)
            if (rin.IloscRoz > 0)
                throw new ComplianceViolationException(
                    $"RIN-reverse blocked (BR4): doc {evt.MagDokNagId} spec {rin.MagDokSpecId} " +
                    $"has ILOSC_ROZ={rin.IloscRoz} — downstream RPR exists");

            await repo.InsertLedgerRowAsync(rin with
            {
                Id = 0,
                RawMaterialIlosc = -rin.RawMaterialIlosc,
                IloscRoz = 0,
            }, conn, tx, ct);
        }

        return new HandlerResult(RunItemOutcome.Ok);
    }
}

/// <summary>Reverse type 2 — reversal of a WK issue (§10).</summary>
public sealed class RprReverseHandler(IEudrRepository repo, ILogger<RprReverseHandler> logger) : IHandler
{
    public EudrEventType EventType => EudrEventType.Rpr;
    public EudrDirection Direction => EudrDirection.Reverse;

    public async Task<HandlerResult> HandleAsync(EudrEvent evt, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        if (await repo.IsAlreadySettledAsync(evt.MagDokNagId, EventType, Direction, conn, tx, ct))
            return new HandlerResult(RunItemOutcome.Skipped);

        // RPR rows store ROZLICZ_ID in MAG_DOK_SPEC_ID (back-pointer to the source RIN spec)
        var rprRows = await repo.GetLedgerRowsByDocAndTypeAsync(evt.MagDokNagId, "RPR", conn, tx, ct);

        foreach (var rpr in rprRows)
        {
            // Credit back the source RIN's ILOSC_ROZ (MagDokSpecId = ROZLICZ_ID = source RIN spec)
            var sourceRin = await repo.GetRinSourceBySpecIdAsync(rpr.MagDokSpecId, conn, tx, ct);

            if (sourceRin is not null)
                await repo.AdvanceIloscRozAsync(sourceRin.Id, -rpr.RawMaterialIlosc, conn, tx, ct);
            else
                logger.LogWarning(
                    "RPR-reverse: no source RIN found for ROZLICZ_ID={RozliczId} (doc {Doc})",
                    rpr.MagDokSpecId, evt.MagDokNagId);

            await repo.InsertLedgerRowAsync(rpr with
            {
                Id = 0,
                RawMaterialIlosc = -rpr.RawMaterialIlosc,
                IloscRoz = 0,
            }, conn, tx, ct);
        }

        return new HandlerResult(RunItemOutcome.Ok);
    }
}

/// <summary>Reverse type 200 — reversal of a ZW production return (§10).</summary>
public sealed class ZwrReverseHandler(IEudrRepository repo, ILogger<ZwrReverseHandler> logger) : IHandler
{
    public EudrEventType EventType => EudrEventType.Zwr;
    public EudrDirection Direction => EudrDirection.Reverse;

    public async Task<HandlerResult> HandleAsync(EudrEvent evt, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        if (await repo.IsAlreadySettledAsync(evt.MagDokNagId, EventType, Direction, conn, tx, ct))
            return new HandlerResult(RunItemOutcome.Skipped);

        var positions = await repo.GetEudrDocPositionsAsync(evt.MagDokNagId, conn, tx, ct);

        foreach (var pos in positions)
        {
            // Reversing ZWR re-consumes the returned qty: RPR ILOSC_ROZ goes back up
            var sourceRpr = await repo.GetLastRprForMaterialAsync(pos.ZamiennikKod, evt.SeriaId, conn, tx, ct);

            if (sourceRpr is not null)
                await repo.AdvanceIloscRozAsync(sourceRpr.Id, pos.Ilosc, conn, tx, ct);

            // Compensating entry: positive ILOSC negates the original negative ZWR
            await repo.InsertLedgerRowAsync(new LedgerRow
            {
                TransactionType = "ZWR",
                MagDokNagId = evt.MagDokNagId,
                MagDokSpecId = pos.MagDokSpecId,
                SeriaId = evt.SeriaId,
                RawMaterialZamiennikKod = pos.ZamiennikKod,
                RawMaterialId = pos.ArtId,
                RawMaterialNazwa = pos.ArtNazwa,
                RawMaterialIlosc = pos.Ilosc,   // positive = compensates the negative forward ZWR
                RawMaterialEtykieta = sourceRpr?.RawMaterialEtykieta,
                ReferenceNumber = sourceRpr?.ReferenceNumber,
                VerificationNumber = sourceRpr?.VerificationNumber,
                RawMaterialLot = sourceRpr?.RawMaterialLot ?? pos.Lot,
                ZamDostawca = sourceRpr?.ZamDostawca,
                ZamNumer = sourceRpr?.ZamNumer,
                TraceLevel = 0,
            }, conn, tx, ct);
        }

        return new HandlerResult(RunItemOutcome.Ok);
    }
}

/// <summary>Reverse type 100 — reversal of a PW finished-product receipt (§10).</summary>
public sealed class RzuReverseHandler(IEudrRepository repo, ILogger<RzuReverseHandler> logger) : IHandler
{
    public EudrEventType EventType => EudrEventType.Rzu;
    public EudrDirection Direction => EudrDirection.Reverse;

    public async Task<HandlerResult> HandleAsync(EudrEvent evt, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        if (await repo.IsAlreadySettledAsync(evt.MagDokNagId, EventType, Direction, conn, tx, ct))
            return new HandlerResult(RunItemOutcome.Skipped);

        var rzuRows = await repo.GetLedgerRowsByDocAndTypeAsync(evt.MagDokNagId, "RZU", conn, tx, ct);

        foreach (var rzu in rzuRows)
        {
            // Skip BRAK SUROWCA entries — no source row to credit back
            if (rzu.ReferenceNumber == "BRAK SUROWCA")
                continue;

            if (rzu.RawMaterialSessionId is not null)
                await repo.AdvanceIloscRozAsync(rzu.RawMaterialSessionId.Value, -rzu.RawMaterialIlosc, conn, tx, ct);

            await repo.InsertLedgerRowAsync(rzu with
            {
                Id = 0,
                RawMaterialIlosc = -rzu.RawMaterialIlosc,
                IloscRoz = 0,
            }, conn, tx, ct);
        }

        return new HandlerResult(RunItemOutcome.Ok);
    }
}

/// <summary>Reverse type 110 — reversal of a WZ shipment (§10). Report-layer only; no counter changes.</summary>
public sealed class OutReverseHandler(IEudrRepository repo, ILogger<OutReverseHandler> logger) : IHandler
{
    public EudrEventType EventType => EudrEventType.Out;
    public EudrDirection Direction => EudrDirection.Reverse;

    public async Task<HandlerResult> HandleAsync(EudrEvent evt, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        if (await repo.IsAlreadySettledAsync(evt.MagDokNagId, EventType, Direction, conn, tx, ct))
            return new HandlerResult(RunItemOutcome.Skipped);

        var outRows = await repo.GetLedgerRowsByDocAndTypeAsync(evt.MagDokNagId, "OUT", conn, tx, ct);

        foreach (var outRow in outRows)
        {
            await repo.InsertLedgerRowAsync(outRow with
            {
                Id = 0,
                RawMaterialIlosc = -outRow.RawMaterialIlosc,
                IloscRoz = 0,
            }, conn, tx, ct);
        }

        return new HandlerResult(RunItemOutcome.Ok);
    }
}
