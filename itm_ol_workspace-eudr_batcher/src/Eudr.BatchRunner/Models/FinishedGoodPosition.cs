namespace Eudr.BatchRunner.Models;

/// <summary>
/// One PW or WZ document position (finished-product receipt or shipment), source for RZU and OUT.
/// </summary>
public record FinishedGoodPosition(
    int MagDokSpecId,
    int ZamowieniaSpecId,   // NS
    decimal Ilosc,
    int ArtId,
    string? Etykieta,
    string? Lot,
    string? ZamiennikKod,
    string? ArtNazwa
);
