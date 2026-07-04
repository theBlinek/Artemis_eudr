using System.Data;
using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Models;
using Microsoft.Extensions.Logging;

namespace Eudr.BatchRunner.Handlers;

/// <summary>
/// Handles type 110 FORWARD — finished-product shipment (WZ). (§9.6)
/// Report-layer only: copies each RZU row for the shipment's NS into an OUT row at TRACE_LEVEL=2.
/// No FIFO counters change. Missing DDS on OUT = compliance gap (§13b).
/// </summary>
public sealed class OutHandler(IEudrRepository repo, ILogger<OutHandler> logger) : IHandler
{
    public EudrEventType EventType => EudrEventType.Out;
    public EudrDirection Direction => EudrDirection.Forward;

    public async Task<HandlerResult> HandleAsync(EudrEvent evt, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        if (await repo.IsAlreadySettledAsync(evt.MagDokNagId, EventType, Direction, conn, tx, ct))
            return new HandlerResult(RunItemOutcome.Skipped);

        var positions = await repo.GetFinishedGoodPositionsAsync(evt.MagDokNagId, conn, tx, ct);

        if (positions.Count == 0)
        {
            logger.LogInformation(
                "OUT: no positions for MAG_DOK_NAG_ID={Doc} — skipping", evt.MagDokNagId);
            return new HandlerResult(RunItemOutcome.Ok);
        }

        var hasComplianceGap = false;

        foreach (var wz in positions)
        {
            var rzuRows = await repo.GetRzuRowsByNsAsync(wz.ZamowieniaSpecId, conn, tx, ct);

            if (rzuRows.Count == 0)
            {
                logger.LogWarning(
                    "OUT: no RZU rows for NS={Ns} (doc {Doc}) — compliance gap", wz.ZamowieniaSpecId, evt.MagDokNagId);
                hasComplianceGap = true;
                continue;
            }

            foreach (var rzu in rzuRows)
            {
                if (string.IsNullOrEmpty(rzu.ReferenceNumber) || rzu.ReferenceNumber == "BRAK SUROWCA")
                {
                    logger.LogWarning(
                        "OUT compliance gap: RZU ID={Id} NS={Ns} has no valid DDS reference", rzu.Id, wz.ZamowieniaSpecId);
                    hasComplianceGap = true;
                }

                await repo.InsertLedgerRowAsync(new LedgerRow
                {
                    TransactionType = "OUT",
                    MagDokNagId = evt.MagDokNagId,
                    MagDokSpecId = wz.MagDokSpecId,
                    SeriaId = evt.SeriaId,
                    RawMaterialZamiennikKod = rzu.RawMaterialZamiennikKod,
                    RawMaterialId = rzu.RawMaterialId,
                    RawMaterialNazwa = rzu.RawMaterialNazwa,
                    RawMaterialIlosc = rzu.RawMaterialIlosc,
                    RawMaterialEtykieta = rzu.RawMaterialEtykieta,
                    ReferenceNumber = rzu.ReferenceNumber,
                    VerificationNumber = rzu.VerificationNumber,
                    RawMaterialLot = rzu.RawMaterialLot,
                    ZamDostawca = rzu.ZamDostawca,
                    ZamNumer = rzu.ZamNumer,
                    Ns = wz.ZamowieniaSpecId,
                    FinishedProductSessionId = rzu.FinishedProductSessionId,
                    RawMaterialSessionId = rzu.RawMaterialSessionId,
                    FinishedProductEtykieta = wz.Etykieta,
                    FinishedProductIlosc = wz.Ilosc,
                    FinishedProductId = wz.ArtId,
                    FinishedGoodsLot = wz.Lot,
                    FinishedGoodsKod = wz.ZamiennikKod,
                    FinishedGoodsName = wz.ArtNazwa,
                    TraceLevel = 2,
                }, conn, tx, ct);
            }
        }

        return hasComplianceGap
            ? new HandlerResult(RunItemOutcome.Shortage, "One or more shipment positions lack a valid DDS reference")
            : new HandlerResult(RunItemOutcome.Ok);
    }
}
