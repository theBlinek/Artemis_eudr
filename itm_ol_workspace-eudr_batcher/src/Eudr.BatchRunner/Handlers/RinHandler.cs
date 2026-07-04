using System.Data;
using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Eudr.BatchRunner.Handlers;

/// <summary>
/// Handles type 1 FORWARD — raw material receipt (PZ). (§9.1)
/// Reads EUDR-tagged positions, generates GS1 labels where absent,
/// attaches purchase-order info, and inserts one RIN row per position.
/// </summary>
public sealed class RinHandler(
    IEudrRepository repo,
    IOptions<EudrOptions> options,
    ILogger<RinHandler> logger) : IHandler
{
    public EudrEventType EventType => EudrEventType.Rin;
    public EudrDirection Direction => EudrDirection.Forward;

    public async Task<HandlerResult> HandleAsync(EudrEvent evt, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        if (await repo.IsAlreadySettledAsync(evt.MagDokNagId, EventType, Direction, conn, tx, ct))
            return new HandlerResult(RunItemOutcome.Skipped);

        var positions = await repo.GetRinPositionsAsync(evt.MagDokNagId, conn, tx, ct);

        if (positions.Count == 0)
        {
            logger.LogInformation(
                "RIN: no EUDR-tagged positions for MAG_DOK_NAG_ID={Doc} — skipping", evt.MagDokNagId);
            return new HandlerResult(RunItemOutcome.Ok);
        }

        var gs1Prefix = options.Value.Gs1Prefix;

        foreach (var pos in positions)
        {
            var label = string.IsNullOrEmpty(pos.Etykieta)
                ? GenerateGs1Label(gs1Prefix, pos.ArtId)
                : pos.Etykieta;

            string? zamNumer = null, zamDostawca = null;
            if (pos.ZamMatId is > 0)
                (zamNumer, zamDostawca) = await repo.GetPurchaseOrderInfoAsync(
                    pos.ZamMatId.Value, conn, tx, ct);

            await repo.InsertLedgerRowAsync(new LedgerRow
            {
                TransactionType = "RIN",
                MagDokNagId = evt.MagDokNagId,
                MagDokSpecId = pos.MagDokSpecId,
                SeriaId = evt.SeriaId,
                RawMaterialZamiennikKod = pos.ZamiennikKod,
                RawMaterialId = pos.ArtId,
                RawMaterialNazwa = pos.ArtNazwa,
                RawMaterialIlosc = pos.Ilosc,
                RawMaterialEtykieta = label,
                RawMaterialLot = pos.Lot,
                ZamNumer = zamNumer,
                ZamDostawca = zamDostawca,
                TraceLevel = 0,
            }, conn, tx, ct);
        }

        return new HandlerResult(RunItemOutcome.Ok);
    }

    // Pattern: '0' + GS1_PREFIX[..9] + ARTYKUL_ID padded to 9 digits (matches legacy SP)
    private static string GenerateGs1Label(string gs1Prefix, int artId)
    {
        var prefix = gs1Prefix.Length > 9 ? gs1Prefix[..9] : gs1Prefix;
        return "0" + prefix + artId.ToString().PadLeft(9, '0');
    }
}
