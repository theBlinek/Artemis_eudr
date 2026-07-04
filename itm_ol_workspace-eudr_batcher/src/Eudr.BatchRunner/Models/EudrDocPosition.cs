namespace Eudr.BatchRunner.Models;

/// <summary>
/// An EUDR-tagged position from any warehouse document (WK, ZW, WZ).
/// RozliczId is populated for WK (issue to production) and is mandatory for RPR handler.
/// </summary>
public record EudrDocPosition(
    int MagDokSpecId,
    int ArtId,
    string ZamiennikKod,
    string? ArtNazwa,
    decimal Ilosc,
    string? Lot,
    int? RozliczId
);
