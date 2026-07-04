using System.Data;
using Eudr.BatchRunner.Exceptions;
using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Models;
using Microsoft.Extensions.Logging;

namespace Eudr.BatchRunner.Handlers;

/// <summary>
/// Handles type 100 FORWARD — finished-product receipt (PW). (§9.5)
/// </summary>
public sealed class RzuHandler(IEudrRepository repo, FifoEngine fifo, ILogger<RzuHandler> logger)
    : IHandler
{
    public EudrEventType EventType => EudrEventType.Rzu;
    public EudrDirection Direction => EudrDirection.Forward;

    public async Task<HandlerResult> HandleAsync(EudrEvent evt, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        if (await repo.IsAlreadySettledAsync(evt.MagDokNagId, EventType, Direction, conn, tx, ct))
            return new HandlerResult(RunItemOutcome.Skipped);

        var positions = await repo.GetFinishedGoodPositionsAsync(evt.MagDokNagId, conn, tx, ct);

        if (positions.Count == 0)
            throw new BusinessException(
                $"RZU: no finished-good positions found for MAG_DOK_NAG_ID={evt.MagDokNagId}");

        var hasShortage = false;

        foreach (var position in positions)
        {
            // One INTEGER session ID per WG position (matches GEN_EUDR_SESSION in legacy)
            var sessionId = await repo.NextSessionIdAsync(ct);

            var boms = await repo.GetBomNormsAsync(evt.MagDokNagId, position.ZamowieniaSpecId, conn, tx, ct);

            if (boms.Count == 0)
            {
                logger.LogWarning(
                    "RZU: no EUDR-tagged BOM norms for MAG_DOK_NAG_ID={Doc}, NS={Ns} — skipping position",
                    evt.MagDokNagId, position.ZamowieniaSpecId);
                continue;
            }

            foreach (var bom in boms)
            {
                var required = bom.NormQuantity * position.Ilosc;

                var result = await fifo.ConsumeAsync(
                    bom.RawMaterialZamiennikKod, evt.SeriaId, required, conn, tx, ct);

                foreach (var alloc in result.Allocations)
                {
                    await repo.InsertLedgerRowAsync(new LedgerRow
                    {
                        TransactionType = "RZU",
                        MagDokNagId = evt.MagDokNagId,
                        MagDokSpecId = position.MagDokSpecId,
                        SeriaId = evt.SeriaId,
                        RawMaterialZamiennikKod = bom.RawMaterialZamiennikKod,
                        RawMaterialId = bom.RawMaterialId,
                        RawMaterialNazwa = bom.RawMaterialNazwa,
                        RawMaterialIlosc = alloc.TakenQty,
                        RawMaterialEtykieta = alloc.Source.RawMaterialEtykieta,
                        ReferenceNumber = alloc.Source.ReferenceNumber,
                        VerificationNumber = alloc.Source.VerificationNumber,
                        RawMaterialLot = alloc.Source.RawMaterialLot,
                        ZamDostawca = alloc.Source.ZamDostawca,
                        ZamNumer = alloc.Source.ZamNumer,
                        Ns = position.ZamowieniaSpecId,
                        FinishedProductSessionId = sessionId,
                        RawMaterialSessionId = alloc.Source.Id,
                        TraceLevel = 1,
                    }, conn, tx, ct);
                }

                if (result.Shortage > 0)
                {
                    hasShortage = true;

                    logger.LogWarning(
                        "SHORTAGE: {Shortage} units of {Kod} missing for doc {Doc}, NS={Ns}",
                        result.Shortage, bom.RawMaterialZamiennikKod, evt.MagDokNagId, position.ZamowieniaSpecId);

                    await repo.InsertLedgerRowAsync(new LedgerRow
                    {
                        TransactionType = "RZU",
                        MagDokNagId = evt.MagDokNagId,
                        MagDokSpecId = position.MagDokSpecId,
                        SeriaId = evt.SeriaId,
                        RawMaterialZamiennikKod = bom.RawMaterialZamiennikKod,
                        RawMaterialId = bom.RawMaterialId,
                        RawMaterialNazwa = bom.RawMaterialNazwa,
                        RawMaterialIlosc = result.Shortage,
                        Ns = position.ZamowieniaSpecId,
                        FinishedProductSessionId = sessionId,
                        TraceLevel = 1,
                        ReferenceNumber = "BRAK SUROWCA",
                    }, conn, tx, ct);
                }
            }
        }

        return hasShortage
            ? new HandlerResult(RunItemOutcome.Shortage, "One or more BOM components had insufficient FIFO stock")
            : new HandlerResult(RunItemOutcome.Ok);
    }
}
