namespace Eudr.BatchRunner.Models;

/// <summary>
/// An RPR or PKS row available for FIFO consumption by RZU.
/// </summary>
public record FifoSource(
    int Id,
    string RawMaterialZamiennikKod,
    int SeriaId,
    decimal Ilosc,
    decimal IloscRoz,
    string? RawMaterialEtykieta,
    string? ReferenceNumber,
    string? VerificationNumber,
    string? RawMaterialLot,
    string? ZamDostawca,
    string? ZamNumer
)
{
    public decimal Available => Ilosc - IloscRoz;
}
