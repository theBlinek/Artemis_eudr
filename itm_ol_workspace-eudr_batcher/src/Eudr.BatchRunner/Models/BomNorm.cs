namespace Eudr.BatchRunner.Models;

public record BomNorm(
    string RawMaterialZamiennikKod,
    int RawMaterialId,
    string? RawMaterialNazwa,
    decimal NormQuantity
);
