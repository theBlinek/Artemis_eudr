using System.Data;
using Eudr.BatchRunner.Models;

namespace Eudr.BatchRunner.Interfaces;

public interface IEudrRepository
{
    // ── Run lifecycle ─────────────────────────────────────────────────────────

    /// <summary>Returns all EUDR_INBOX business dates that have no DONE run yet, oldest first.</summary>
    Task<IReadOnlyList<DateOnly>> GetPendingDatesAsync(CancellationToken ct = default);

    /// <summary>Generates the next FINISHED_PRODUCT_SESSION_ID (GEN_EUDR_SESSION equivalent).</summary>
    Task<int> NextSessionIdAsync(CancellationToken ct = default);

    /// <summary>Returns null if a DONE run already exists for this date (idempotency gate).</summary>
    Task<EudrRun?> TryClaimRunAsync(DateOnly businessDate, CancellationToken ct = default);

    Task CompleteRunAsync(int runId, RunStatus status, CancellationToken ct = default);

    Task RecordRunItemAsync(EudrRunItem item, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default);

    // ── Event sourcing ────────────────────────────────────────────────────────

    Task<IReadOnlyList<EudrEvent>> GetDayEventsAsync(DateOnly businessDate,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the event_ids from EUDR_EVENT_JOURNAL for the day that have no
    /// corresponding settled outcome — the reconciliation gap list.
    /// </summary>
    Task<IReadOnlyList<int>> GetJournalGapsAsync(DateOnly businessDate, int runId,
        CancellationToken ct = default);

    // ── Idempotency ───────────────────────────────────────────────────────────

    Task<bool> IsAlreadySettledAsync(int magDokNagId, EudrEventType type, EudrDirection direction,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    // ── Source reads (Saturn's tables) ───────────────────────────────────────

    Task<IReadOnlyList<FinishedGoodPosition>> GetFinishedGoodPositionsAsync(int magDokNagId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    Task<IReadOnlyList<BomNorm>> GetBomNormsAsync(int magDokNagId, int zamowieniaSpecId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    // ── FIFO ledger reads / writes ────────────────────────────────────────────

    /// <summary>
    /// Fetches RPR/PKS rows available for FIFO consumption, ordered by ID ASC.
    /// Uses WITH LOCK to guard against concurrent writers (defensive; batch is sequential).
    /// </summary>
    Task<IReadOnlyList<FifoSource>> GetFifoSourcesAsync(string zamiennikKod, int seriaId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    Task<int> InsertLedgerRowAsync(LedgerRow row, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default);

    /// <summary>Advances the FIFO counter: ILOSC_ROZ += delta on a single source row.</summary>
    Task AdvanceIloscRozAsync(int sourceId, decimal delta, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default);

    // ── Document position reads ───────────────────────────────────────────────

    /// <summary>EUDR-tagged PZ receipt positions (includes ETYKIETA and ZAM_MAT_ID for RIN).</summary>
    Task<IReadOnlyList<RinPosition>> GetRinPositionsAsync(int magDokNagId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    /// <summary>EUDR-tagged positions for any warehouse doc (WK/ZW/WZ). Includes ROZLICZ_ID.</summary>
    Task<IReadOnlyList<EudrDocPosition>> GetEudrDocPositionsAsync(int magDokNagId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    /// <summary>All ledger rows for a document and transaction type, positive qty only.</summary>
    Task<IReadOnlyList<LedgerRow>> GetLedgerRowsByDocAndTypeAsync(int magDokNagId, string txType,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    // ── RIN-specific ──────────────────────────────────────────────────────────

    Task<(string? ZamNumer, string? ZamDostawca)> GetPurchaseOrderInfoAsync(
        int zamMatId, IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    Task<string?> GetExistingEtykietaAsync(int magDokSpecId, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default);

    // ── RPR-specific ──────────────────────────────────────────────────────────

    Task<LedgerRow?> GetRinSourceBySpecIdAsync(int rozliczId, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default);

    // ── ZWR-specific ──────────────────────────────────────────────────────────

    Task<LedgerRow?> GetLastRprForMaterialAsync(string zamiennikKod, int seriaId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    // ── PKS-specific ──────────────────────────────────────────────────────────

    Task<LedgerRow?> GetLastRprOrPksForMaterialAsync(string zamiennikKod, int seriaId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    Task<(string? ZamiennikKod, string? ZamiennikNazwa)> GetArtykul(int artId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);

    // ── OUT-specific ──────────────────────────────────────────────────────────

    Task<IReadOnlyList<LedgerRow>> GetRzuRowsByNsAsync(int ns,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default);
}
