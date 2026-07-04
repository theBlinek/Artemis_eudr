using System.Data;
using Eudr.BatchRunner.Exceptions;
using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Models;
using Microsoft.Extensions.Logging;

namespace Eudr.BatchRunner.Handlers;

/// <summary>
/// Handles type 2 FORWARD — issue to production (WK). (§9.2)
/// Sources DDS / label from the originating RIN via ROZLICZ_ID back-pointer.
/// Advances source RIN's ILOSC_ROZ so FIFO can track remaining available.
/// </summary>
public sealed class RprHandler(IEudrRepository repo, ILogger<RprHandler> logger) : IHandler
{
    public EudrEventType EventType => EudrEventType.Rpr;
    public EudrDirection Direction => EudrDirection.Forward;

    public async Task<HandlerResult> HandleAsync(EudrEvent evt, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        if (await repo.IsAlreadySettledAsync(evt.MagDokNagId, EventType, Direction, conn, tx, ct))
            return new HandlerResult(RunItemOutcome.Skipped);

        var positions = await repo.GetEudrDocPositionsAsync(evt.MagDokNagId, conn, tx, ct);

        if (positions.Count == 0)
        {
            logger.LogInformation(
                "RPR: no EUDR-tagged positions for MAG_DOK_NAG_ID={Doc} — skipping", evt.MagDokNagId);
            return new HandlerResult(RunItemOutcome.Ok);
        }

        foreach (var pos in positions)
        {
            if (pos.RozliczId is null or 0)
                throw new BusinessException(
                    $"RPR: ROZLICZ_ID missing on MAG_DOK_SPEC_ID={pos.MagDokSpecId} " +
                    $"(doc {evt.MagDokNagId}) — cannot source RIN");

            var sourceRin = await repo.GetRinSourceBySpecIdAsync(pos.RozliczId.Value, conn, tx, ct);

            if (sourceRin is null)
                throw new BusinessException(
                    $"RPR: no RIN row found for ROZLICZ_ID={pos.RozliczId} " +
                    $"(doc {evt.MagDokNagId})");

            await repo.InsertLedgerRowAsync(new LedgerRow
            {
                TransactionType = "RPR",
                MagDokNagId = evt.MagDokNagId,
                MagDokSpecId = pos.RozliczId!.Value,  // back-pointer: MAG_DOK_SPEC_ID = ROZLICZ_ID of source RIN
                SeriaId = evt.SeriaId,
                RawMaterialZamiennikKod = pos.ZamiennikKod,
                RawMaterialId = pos.ArtId,
                RawMaterialNazwa = pos.ArtNazwa,
                RawMaterialIlosc = pos.Ilosc,
                RawMaterialEtykieta = sourceRin.RawMaterialEtykieta,
                ReferenceNumber = sourceRin.ReferenceNumber,
                VerificationNumber = sourceRin.VerificationNumber,
                RawMaterialLot = sourceRin.RawMaterialLot ?? pos.Lot,
                ZamDostawca = sourceRin.ZamDostawca,
                ZamNumer = sourceRin.ZamNumer,
                TraceLevel = 0,
            }, conn, tx, ct);

            await repo.AdvanceIloscRozAsync(sourceRin.Id, pos.Ilosc, conn, tx, ct);
        }

        return new HandlerResult(RunItemOutcome.Ok);
    }
}
