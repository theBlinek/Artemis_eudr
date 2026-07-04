namespace Eudr.BatchRunner.Models;

/// <summary>
/// An EUDR-tagged position from a PZ (purchase receipt) document, used by RinHandler.
/// </summary>
public record RinPosition(
    int MagDokSpecId,
    int ArtId,
    string ZamiennikKod,
    string? ArtNazwa,
    decimal Ilosc,
    string? Etykieta,
    string? Lot,
    int? ZamMatId
);
