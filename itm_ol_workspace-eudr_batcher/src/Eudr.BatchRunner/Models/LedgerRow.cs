namespace Eudr.BatchRunner.Models;

/// <summary>
/// Maps 1:1 to a row in EUDR_RAW_MATERIALS_SUMMARY.
/// All inserts are append-only; handlers never update existing rows (except ILOSC_ROZ counter).
/// </summary>
public record LedgerRow
{
    public int Id { get; init; }
    public string TransactionType { get; init; } = null!;

    // Document linkage
    public int MagDokNagId { get; init; }
    public int MagDokSpecId { get; init; }
    public int SeriaId { get; init; }
    public string? SeriaKod { get; init; }

    // Raw material identity
    public string? RawMaterialZamiennikKod { get; init; }
    public int? RawMaterialId { get; init; }
    public string? RawMaterialNazwa { get; init; }

    // Quantities
    public decimal RawMaterialIlosc { get; init; }         // negative for ZWR
    public decimal IloscRoz { get; init; }                 // FIFO consumed counter
    public decimal? RawMaterialEtykietaIlosc { get; init; } // qty on label/pallet

    // Traceability
    public string? RawMaterialEtykieta { get; init; }     // GS1 label
    public string? ReferenceNumber { get; init; }          // DDS ref# or 'BRAK SUROWCA'
    public string? VerificationNumber { get; init; }       // DDS ver#
    public string? RawMaterialLot { get; init; }
    public string? ZamDostawca { get; init; }              // supplier name (KLIENCI_OPS)
    public string? ZamNumer { get; init; }                 // purchase order number

    // RZU / OUT specific
    public int? Ns { get; init; }                          // ZAMOWIENIA_SPEC_ID
    public int? FinishedProductSessionId { get; init; }    // GEN_EUDR_SESSION per WG position
    public int? RawMaterialSessionId { get; init; }        // ID of source RPR/PKS row

    // OUT specific
    public string? FinishedProductEtykieta { get; init; }
    public decimal? FinishedProductIlosc { get; init; }
    public string? FinishedGoodsLot { get; init; }
    public string? FinishedGoodsKod { get; init; }
    public string? FinishedGoodsName { get; init; }
    public int? FinishedProductId { get; init; }

    public int TraceLevel { get; init; }                   // 0=RIN/RPR/ZWR/PKS, 1=RZU, 2=OUT
}
