using System.Data;
using Dapper;
using Eudr.BatchRunner.Interfaces;
using Eudr.BatchRunner.Models;
using Microsoft.Extensions.Logging;

namespace Eudr.BatchRunner.Repository;

public sealed class FirebirdEudrRepository(IConnectionFactory connectionFactory,
    ILogger<FirebirdEudrRepository> logger) : IEudrRepository
{
    // ── Run lifecycle ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DateOnly>> GetPendingDatesAsync(CancellationToken ct = default)
    {
        using var conn = connectionFactory.Create();

        var dates = await conn.QueryAsync<DateTime>(
            """
            SELECT DISTINCT i.BUSINESS_DATE
            FROM EUDR_INBOX i
            WHERE NOT EXISTS (
                SELECT 1 FROM EUDR_RUN r
                WHERE r.BUSINESS_DATE = i.BUSINESS_DATE
                  AND r.STATUS = 'DONE'
            )
            ORDER BY i.BUSINESS_DATE ASC
            """);

        return dates.Select(DateOnly.FromDateTime).ToList();
    }

    public async Task<int> NextSessionIdAsync(CancellationToken ct = default)
    {
        using var conn = connectionFactory.Create();
        return await conn.ExecuteScalarAsync<int>("SELECT GEN_ID(GEN_EUDR_SESSION, 1) FROM RDB$DATABASE");
    }

    public async Task<EudrRun?> TryClaimRunAsync(DateOnly businessDate, CancellationToken ct = default)
    {
        using var conn = connectionFactory.Create();

        var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT RUN_ID, STATUS FROM EUDR_RUN WHERE BUSINESS_DATE = @Date AND STATUS = 'DONE'",
            new { Date = businessDate.ToDateTime(TimeOnly.MinValue) });

        if (existing is not null)
        {
            logger.LogWarning("Run for {Date} is already DONE — aborting (idempotency gate)", businessDate);
            return null;
        }

        var runId = await conn.ExecuteScalarAsync<int>(
            """
            INSERT INTO EUDR_RUN (BUSINESS_DATE, STATUS, STARTED_AT)
            VALUES (@Date, 'RUNNING', CURRENT_TIMESTAMP)
            RETURNING RUN_ID
            """,
            new { Date = businessDate.ToDateTime(TimeOnly.MinValue) });

        return new EudrRun(runId, businessDate, RunStatus.Running, DateTime.UtcNow);
    }

    public async Task CompleteRunAsync(int runId, RunStatus status, CancellationToken ct = default)
    {
        using var conn = connectionFactory.Create();
        await conn.ExecuteAsync(
            "UPDATE EUDR_RUN SET STATUS = @Status, FINISHED_AT = CURRENT_TIMESTAMP WHERE RUN_ID = @RunId",
            new { Status = status.ToDbString(), RunId = runId });
    }

    public async Task RecordRunItemAsync(EudrRunItem item, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO EUDR_RUN_ITEM
              (RUN_ID, EVENT_ID, MAG_DOK_NAG_ID, EVENT_TYPE, DIRECTION, OUTCOME, NOTES)
            VALUES
              (@RunId, @EventId, @MagDokNagId, @EventType, @Direction, @Outcome, @Notes)
            """,
            new
            {
                item.RunId,
                item.EventId,
                item.MagDokNagId,
                EventType = (int)item.EventType,
                Direction = item.Direction.ToString().ToUpperInvariant(),
                Outcome = item.Outcome.ToDbString(),
                item.Notes,
            }, tx);
    }

    // ── Event sourcing ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<EudrEvent>> GetDayEventsAsync(DateOnly businessDate,
        CancellationToken ct = default)
    {
        using var conn = connectionFactory.Create();

        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION,
                   SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP
            FROM EUDR_INBOX
            WHERE BUSINESS_DATE = @Date
            ORDER BY OCCURRENCE_SEQ ASC
            """,
            new { Date = businessDate.ToDateTime(TimeOnly.MinValue) });

        return rows.Select(r => new EudrEvent(
            EventId: (int)r.EVENT_ID,
            MagDokNagId: (int)r.MAG_DOK_NAG_ID,
            EudrType: (EudrEventType)(int)r.EUDR_TYPE,
            Direction: ParseDirection((string)r.DIRECTION),
            SeriaId: (int)r.SERIA_ID,
            OccurrenceSequence: (int)r.OCCURRENCE_SEQ,
            Timestamp: (DateTime)r.EVENT_TIMESTAMP
        )).ToList();
    }

    public async Task<IReadOnlyList<int>> GetJournalGapsAsync(DateOnly businessDate, int runId,
        CancellationToken ct = default)
    {
        using var conn = connectionFactory.Create();

        var gaps = await conn.QueryAsync<int>(
            """
            SELECT j.EVENT_ID
            FROM EUDR_EVENT_JOURNAL j
            WHERE j.BUSINESS_DATE = @Date
              AND NOT EXISTS (
                SELECT 1 FROM EUDR_RUN_ITEM ri
                WHERE ri.RUN_ID = @RunId AND ri.EVENT_ID = j.EVENT_ID
              )
            ORDER BY j.OCCURRENCE_SEQ
            """,
            new { Date = businessDate.ToDateTime(TimeOnly.MinValue), RunId = runId });

        return gaps.ToList();
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    public async Task<bool> IsAlreadySettledAsync(int magDokNagId, EudrEventType type,
        EudrDirection direction, IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        // Forward: any row existing = already settled (no sign filter).
        // Reverse: a compensating row (opposite sign) already exists.
        //   ZWR forward = negative ILOSC → its compensation = positive
        //   All others forward = positive ILOSC → their compensation = negative
        var signClause = direction == EudrDirection.Reverse
            ? (type == EudrEventType.Zwr
                ? " AND RAW_MATERIAL_ILOSC > 0"
                : " AND RAW_MATERIAL_ILOSC < 0")
            : "";

        var count = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(1) FROM EUDR_RAW_MATERIALS_SUMMARY WHERE MAG_DOK_NAG_ID = @MagDokNagId AND TRANSACTION_TYPE = @TxType{signClause}",
            new
            {
                MagDokNagId = magDokNagId,
                TxType = type.ToTransactionTypeString(),
            }, tx);

        return count > 0;
    }

    // ── Source reads ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FinishedGoodPosition>> GetFinishedGoodPositionsAsync(
        int magDokNagId, IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT s.MAG_DOK_SPEC_ID, s.ZAMOWIENIA_SPEC_ID, s.ILOSC, s.ARTYKUL_ID,
                   s.ETYKIETA, s.NUMER_PARTII_LOT, a.ZAMIENNIK_KOD, a.NAZWA1
            FROM MAG_DOK_SPEC s
            JOIN ARTYKUL a ON a.ARTYKUL_ID = s.ARTYKUL_ID
            WHERE s.MAG_DOK_NAG_ID = @MagDokNagId
            """,
            new { MagDokNagId = magDokNagId }, tx);

        return rows.Select(r => new FinishedGoodPosition(
            MagDokSpecId: (int)r.MAG_DOK_SPEC_ID,
            ZamowieniaSpecId: (int)r.ZAMOWIENIA_SPEC_ID,
            Ilosc: (decimal)r.ILOSC,
            ArtId: (int)r.ARTYKUL_ID,
            Etykieta: (string?)r.ETYKIETA,
            Lot: (string?)r.NUMER_PARTII_LOT,
            ZamiennikKod: (string?)r.ZAMIENNIK_KOD,
            ArtNazwa: (string?)r.NAZWA1
        )).ToList();
    }

    public async Task<IReadOnlyList<RinPosition>> GetRinPositionsAsync(int magDokNagId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT s.MAG_DOK_SPEC_ID, s.ARTYKUL_ID, a.ZAMIENNIK_KOD, a.NAZWA1,
                   s.ILOSC, s.ETYKIETA, s.NUMER_PARTII_LOT, n.ZAM_MAT_ID
            FROM MAG_DOK_SPEC s
            JOIN ARTYKUL a ON a.ARTYKUL_ID = s.ARTYKUL_ID
            JOIN MAG_DOK_NAG n ON n.MAG_DOK_NAG_ID = s.MAG_DOK_NAG_ID
            WHERE s.MAG_DOK_NAG_ID = @MagDokNagId
              AND EXISTS (
                  SELECT 1 FROM ARTYKUL_TAGI at2
                  JOIN TAGI t ON t.TAGI_ID = at2.TAGI_ID
                  WHERE at2.ARTYKUL_ID = a.ARTYKUL_ID
                    AND t.KOLEJNOSC = 1000
                    AND t.WEIGHT_CONF = 2
              )
            """,
            new { MagDokNagId = magDokNagId }, tx);

        return rows.Select(r => new RinPosition(
            MagDokSpecId: (int)r.MAG_DOK_SPEC_ID,
            ArtId: (int)r.ARTYKUL_ID,
            ZamiennikKod: (string)r.ZAMIENNIK_KOD,
            ArtNazwa: (string?)r.NAZWA1,
            Ilosc: (decimal)r.ILOSC,
            Etykieta: (string?)r.ETYKIETA,
            Lot: (string?)r.NUMER_PARTII_LOT,
            ZamMatId: (int?)r.ZAM_MAT_ID
        )).ToList();
    }

    public async Task<IReadOnlyList<EudrDocPosition>> GetEudrDocPositionsAsync(int magDokNagId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT s.MAG_DOK_SPEC_ID, s.ARTYKUL_ID, a.ZAMIENNIK_KOD, a.NAZWA1,
                   s.ILOSC, s.NUMER_PARTII_LOT, s.ROZLICZ_ID
            FROM MAG_DOK_SPEC s
            JOIN ARTYKUL a ON a.ARTYKUL_ID = s.ARTYKUL_ID
            WHERE s.MAG_DOK_NAG_ID = @MagDokNagId
              AND EXISTS (
                  SELECT 1 FROM ARTYKUL_TAGI at2
                  JOIN TAGI t ON t.TAGI_ID = at2.TAGI_ID
                  WHERE at2.ARTYKUL_ID = a.ARTYKUL_ID
                    AND t.KOLEJNOSC = 1000
                    AND t.WEIGHT_CONF = 2
              )
            """,
            new { MagDokNagId = magDokNagId }, tx);

        return rows.Select(r => new EudrDocPosition(
            MagDokSpecId: (int)r.MAG_DOK_SPEC_ID,
            ArtId: (int)r.ARTYKUL_ID,
            ZamiennikKod: (string)r.ZAMIENNIK_KOD,
            ArtNazwa: (string?)r.NAZWA1,
            Ilosc: (decimal)r.ILOSC,
            Lot: (string?)r.NUMER_PARTII_LOT,
            RozliczId: (int?)r.ROZLICZ_ID
        )).ToList();
    }

    public async Task<IReadOnlyList<LedgerRow>> GetLedgerRowsByDocAndTypeAsync(int magDokNagId,
        string txType, IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT ID, TRANSACTION_TYPE, MAG_DOK_NAG_ID, MAG_DOK_SPEC_ID, SERIA_ID,
                   RAW_MATERIAL_ZAMIENNIK_KOD, RAW_MATERIAL_ID, RAW_MATERIAL_NAZWA,
                   RAW_MATERIAL_ILOSC, COALESCE(RAW_MATERIAL_ETYKIETA_ILOSC_ROZ, 0) AS ILOSC_ROZ,
                   RAW_MATERIAL_ETYKIETA, REFERENCE_NUMBER, VERIFICATION_NUMBER,
                   RAW_MATERIAL_LOT, ZAM_DOSTAWCA, ZAM_NUMER,
                   NS, FINISHED_PRODUCT_SESSION_ID, RAW_MATERIAL_SESSION_ID,
                   FINISHED_PRODUCT_ETYKIETA, FINISHED_PRODUCT_ILOSC, FINISHED_PRODUCT_ID,
                   FINISHED_GOODS_LOT, FINISHED_GOODS_KOD, FINISHED_GOODS_NAME,
                   TRACE_LEVEL
            FROM EUDR_RAW_MATERIALS_SUMMARY
            WHERE MAG_DOK_NAG_ID = @MagDokNagId
              AND TRANSACTION_TYPE = @TxType
              AND RAW_MATERIAL_ILOSC > 0
            ORDER BY ID ASC
            """,
            new { MagDokNagId = magDokNagId, TxType = txType }, tx);

        return rows.Select(MapToFullLedgerRow).ToList();
    }

    public async Task<IReadOnlyList<BomNorm>> GetBomNormsAsync(int magDokNagId, int zamowieniaSpecId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        // MAG_DOK_SPEC_PRO joined to EUDR-tagged ARTYKUL (TAGI.KOLEJNOSC=1000, WEIGHT_CONF=2)
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT a.ZAMIENNIK_KOD, a.ARTYKUL_ID, a.NAZWA1, p.ILOSC AS NORM_QUANTITY
            FROM MAG_DOK_SPEC_PRO p
            JOIN ARTYKUL a ON a.ARTYKUL_ID = p.ARTYKUL_ID
            JOIN ARTYKUL_TAGI at2 ON at2.ARTYKUL_ID = a.ARTYKUL_ID
            JOIN TAGI t ON t.TAGI_ID = at2.TAGI_ID
            WHERE p.ZAMOWIENIA_SPEC_ID = @Ns
              AND t.KOLEJNOSC = 1000
              AND t.WEIGHT_CONF = 2
            """,
            new { Ns = zamowieniaSpecId }, tx);

        return rows.Select(r => new BomNorm(
            RawMaterialZamiennikKod: (string)r.ZAMIENNIK_KOD,
            RawMaterialId: (int)r.ARTYKUL_ID,
            RawMaterialNazwa: (string?)r.NAZWA1,
            NormQuantity: (decimal)r.NORM_QUANTITY
        )).ToList();
    }

    // ── FIFO ledger ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FifoSource>> GetFifoSourcesAsync(string zamiennikKod, int seriaId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        // WITH LOCK is defensive in batch (sequential); protects against any stray concurrent writer.
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT ID, RAW_MATERIAL_ZAMIENNIK_KOD, SERIA_ID, RAW_MATERIAL_ILOSC AS ILOSC,
                   COALESCE(RAW_MATERIAL_ETYKIETA_ILOSC_ROZ, 0) AS ILOSC_ROZ,
                   RAW_MATERIAL_ETYKIETA, REFERENCE_NUMBER, VERIFICATION_NUMBER,
                   RAW_MATERIAL_LOT, ZAM_DOSTAWCA, ZAM_NUMER
            FROM EUDR_RAW_MATERIALS_SUMMARY
            WHERE RAW_MATERIAL_ZAMIENNIK_KOD = @Kod
              AND SERIA_ID = @SeriaId
              AND TRANSACTION_TYPE IN ('RPR', 'PKS')
              AND (RAW_MATERIAL_ILOSC - COALESCE(RAW_MATERIAL_ETYKIETA_ILOSC_ROZ, 0)) > 0
            ORDER BY ID ASC
            WITH LOCK
            """,
            new { Kod = zamiennikKod, SeriaId = seriaId }, tx);

        return rows.Select(r => new FifoSource(
            Id: (int)r.ID,
            RawMaterialZamiennikKod: (string)r.RAW_MATERIAL_ZAMIENNIK_KOD,
            SeriaId: (int)r.SERIA_ID,
            Ilosc: (decimal)r.ILOSC,
            IloscRoz: (decimal)r.ILOSC_ROZ,
            RawMaterialEtykieta: (string?)r.RAW_MATERIAL_ETYKIETA,
            ReferenceNumber: (string?)r.REFERENCE_NUMBER,
            VerificationNumber: (string?)r.VERIFICATION_NUMBER,
            RawMaterialLot: (string?)r.RAW_MATERIAL_LOT,
            ZamDostawca: (string?)r.ZAM_DOSTAWCA,
            ZamNumer: (string?)r.ZAM_NUMER
        )).ToList();
    }

    public async Task<int> InsertLedgerRowAsync(LedgerRow row, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        var id = await conn.ExecuteScalarAsync<int>(
            """
            INSERT INTO EUDR_RAW_MATERIALS_SUMMARY (
                TRANSACTION_TYPE,
                MAG_DOK_NAG_ID, MAG_DOK_SPEC_ID, SERIA_ID, SERIA_KOD,
                RAW_MATERIAL_ZAMIENNIK_KOD, RAW_MATERIAL_ID, RAW_MATERIAL_NAZWA,
                RAW_MATERIAL_ILOSC, RAW_MATERIAL_ETYKIETA_ILOSC,
                RAW_MATERIAL_ETYKIETA, REFERENCE_NUMBER, VERIFICATION_NUMBER,
                RAW_MATERIAL_LOT, ZAM_DOSTAWCA, ZAM_NUMER,
                NS, FINISHED_PRODUCT_SESSION_ID, RAW_MATERIAL_SESSION_ID,
                FINISHED_PRODUCT_ETYKIETA, FINISHED_PRODUCT_ILOSC, FINISHED_PRODUCT_ID,
                FINISHED_GOODS_LOT, FINISHED_GOODS_KOD, FINISHED_GOODS_NAME,
                TRACE_LEVEL, RAW_MATERIALS_ORY_ILOSC
            ) VALUES (
                @TransactionType,
                @MagDokNagId, @MagDokSpecId, @SeriaId, @SeriaKod,
                @RawMaterialZamiennikKod, @RawMaterialId, @RawMaterialNazwa,
                @RawMaterialIlosc, @RawMaterialEtykietaIlosc,
                @RawMaterialEtykieta, @ReferenceNumber, @VerificationNumber,
                @RawMaterialLot, @ZamDostawca, @ZamNumer,
                @Ns, @FinishedProductSessionId, @RawMaterialSessionId,
                @FinishedProductEtykieta, @FinishedProductIlosc, @FinishedProductId,
                @FinishedGoodsLot, @FinishedGoodsKod, @FinishedGoodsName,
                @TraceLevel, @RawMaterialIlosc
            )
            RETURNING ID
            """,
            new
            {
                row.TransactionType,
                row.MagDokNagId,
                row.MagDokSpecId,
                row.SeriaId,
                row.SeriaKod,
                row.RawMaterialZamiennikKod,
                row.RawMaterialId,
                row.RawMaterialNazwa,
                row.RawMaterialIlosc,
                RawMaterialEtykietaIlosc = row.RawMaterialEtykietaIlosc ?? (object)DBNull.Value,
                row.RawMaterialEtykieta,
                row.ReferenceNumber,
                row.VerificationNumber,
                row.RawMaterialLot,
                row.ZamDostawca,
                row.ZamNumer,
                row.Ns,
                row.FinishedProductSessionId,
                row.RawMaterialSessionId,
                row.FinishedProductEtykieta,
                row.FinishedProductIlosc,
                row.FinishedProductId,
                row.FinishedGoodsLot,
                row.FinishedGoodsKod,
                row.FinishedGoodsName,
                row.TraceLevel,
            }, tx);

        return id;
    }

    public async Task AdvanceIloscRozAsync(int sourceId, decimal delta, IDbConnection conn,
        IDbTransaction tx, CancellationToken ct = default)
    {
        await conn.ExecuteAsync(
            """
            UPDATE EUDR_RAW_MATERIALS_SUMMARY
            SET RAW_MATERIAL_ETYKIETA_ILOSC_ROZ = COALESCE(RAW_MATERIAL_ETYKIETA_ILOSC_ROZ, 0) + @Delta
            WHERE ID = @Id
            """,
            new { Id = sourceId, Delta = delta }, tx);
    }

    // ── RIN helpers ───────────────────────────────────────────────────────────

    public async Task<(string? ZamNumer, string? ZamDostawca)>
        GetPurchaseOrderInfoAsync(int zamMatId, IDbConnection conn, IDbTransaction tx,
        CancellationToken ct = default)
    {
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT FIRST 1 zm.ZAM_NUMER, kl.KLIENCI_OPIS
            FROM ZAM_MAT zm
            JOIN KLIENCI kl ON kl.KLIENCI_ID = zm.KLIENCI_ID
            WHERE zm.ZAM_MAT_ID = @ZamMatId
            """,
            new { ZamMatId = zamMatId }, tx);

        return row is null
            ? (null, null)
            : ((string?)row.ZAM_NUMER, (string?)row.KLIENCI_OPIS);
    }

    public async Task<string?> GetExistingEtykietaAsync(int magDokSpecId, IDbConnection conn,
        IDbTransaction tx, CancellationToken ct = default)
    {
        return await conn.ExecuteScalarAsync<string?>(
            """
            SELECT RAW_MATERIAL_ETYKIETA FROM EUDR_RAW_MATERIALS_SUMMARY
            WHERE MAG_DOK_SPEC_ID = @SpecId AND TRANSACTION_TYPE = 'RIN'
            """,
            new { SpecId = magDokSpecId }, tx);
    }

    // ── RPR helpers ───────────────────────────────────────────────────────────

    public async Task<LedgerRow?> GetRinSourceBySpecIdAsync(int rozliczId, IDbConnection conn,
        IDbTransaction tx, CancellationToken ct = default)
    {
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT ID, RAW_MATERIAL_ZAMIENNIK_KOD, SERIA_ID, RAW_MATERIAL_ILOSC,
                   COALESCE(RAW_MATERIAL_ETYKIETA_ILOSC_ROZ,0) AS ILOSC_ROZ,
                   RAW_MATERIAL_ETYKIETA, REFERENCE_NUMBER, VERIFICATION_NUMBER,
                   RAW_MATERIAL_LOT, ZAM_DOSTAWCA, ZAM_NUMER
            FROM EUDR_RAW_MATERIALS_SUMMARY
            WHERE MAG_DOK_SPEC_ID = @RozliczId AND TRANSACTION_TYPE = 'RIN'
            WITH LOCK
            """,
            new { RozliczId = rozliczId }, tx);

        return row is null ? null : MapToLedgerRow(row);
    }

    // ── ZWR helpers ───────────────────────────────────────────────────────────

    public async Task<LedgerRow?> GetLastRprForMaterialAsync(string zamiennikKod, int seriaId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT FIRST 1 ID, RAW_MATERIAL_ZAMIENNIK_KOD, SERIA_ID, RAW_MATERIAL_ILOSC,
                   COALESCE(RAW_MATERIAL_ETYKIETA_ILOSC_ROZ,0) AS ILOSC_ROZ,
                   RAW_MATERIAL_ETYKIETA, REFERENCE_NUMBER, VERIFICATION_NUMBER,
                   RAW_MATERIAL_LOT, ZAM_DOSTAWCA, ZAM_NUMER
            FROM EUDR_RAW_MATERIALS_SUMMARY
            WHERE RAW_MATERIAL_ZAMIENNIK_KOD = @Kod AND SERIA_ID = @SeriaId
              AND TRANSACTION_TYPE = 'RPR'
            ORDER BY ID DESC
            WITH LOCK
            """,
            new { Kod = zamiennikKod, SeriaId = seriaId }, tx);

        return row is null ? null : MapToLedgerRow(row);
    }

    // ── PKS helpers ───────────────────────────────────────────────────────────

    public async Task<LedgerRow?> GetLastRprOrPksForMaterialAsync(string zamiennikKod, int seriaId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT FIRST 1 ID, RAW_MATERIAL_ZAMIENNIK_KOD, SERIA_ID, RAW_MATERIAL_ILOSC,
                   COALESCE(RAW_MATERIAL_ETYKIETA_ILOSC_ROZ,0) AS ILOSC_ROZ,
                   RAW_MATERIAL_ETYKIETA, REFERENCE_NUMBER, VERIFICATION_NUMBER,
                   RAW_MATERIAL_LOT, ZAM_DOSTAWCA, ZAM_NUMER
            FROM EUDR_RAW_MATERIALS_SUMMARY
            WHERE RAW_MATERIAL_ZAMIENNIK_KOD = @Kod AND SERIA_ID = @SeriaId
              AND TRANSACTION_TYPE IN ('RPR', 'PKS')
            ORDER BY ID DESC
            WITH LOCK
            """,
            new { Kod = zamiennikKod, SeriaId = seriaId }, tx);

        return row is null ? null : MapToLedgerRow(row);
    }

    public async Task<(string? ZamiennikKod, string? ZamiennikNazwa)> GetArtykul(int artId,
        IDbConnection conn, IDbTransaction tx, CancellationToken ct = default)
    {
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT ZAMIENNIK_KOD, NAZWA1 FROM ARTYKUL WHERE ARTYKUL_ID = @ArtId",
            new { ArtId = artId }, tx);

        return row is null ? (null, null) : ((string?)row.ZAMIENNIK_KOD, (string?)row.NAZWA1);
    }

    // ── OUT helpers ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<LedgerRow>> GetRzuRowsByNsAsync(int ns, IDbConnection conn,
        IDbTransaction tx, CancellationToken ct = default)
    {
        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT ID, TRANSACTION_TYPE, MAG_DOK_NAG_ID, MAG_DOK_SPEC_ID, SERIA_ID,
                   RAW_MATERIAL_ZAMIENNIK_KOD, RAW_MATERIAL_ID, RAW_MATERIAL_NAZWA,
                   RAW_MATERIAL_ILOSC, COALESCE(RAW_MATERIAL_ETYKIETA_ILOSC_ROZ, 0) AS ILOSC_ROZ,
                   RAW_MATERIAL_ETYKIETA, REFERENCE_NUMBER, VERIFICATION_NUMBER,
                   RAW_MATERIAL_LOT, ZAM_DOSTAWCA, ZAM_NUMER,
                   NS, FINISHED_PRODUCT_SESSION_ID, RAW_MATERIAL_SESSION_ID,
                   FINISHED_PRODUCT_ETYKIETA, FINISHED_PRODUCT_ILOSC, FINISHED_PRODUCT_ID,
                   FINISHED_GOODS_LOT, FINISHED_GOODS_KOD, FINISHED_GOODS_NAME,
                   TRACE_LEVEL
            FROM EUDR_RAW_MATERIALS_SUMMARY
            WHERE NS = @Ns AND TRANSACTION_TYPE = 'RZU'
            ORDER BY ID ASC
            """,
            new { Ns = ns }, tx);

        return rows.Select(MapToFullLedgerRow).ToList();
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    // Minimal mapper used by FIFO helpers (GetFifoSourcesAsync, GetRinSourceBySpecIdAsync, etc.)
    private static LedgerRow MapToLedgerRow(dynamic r) => new()
    {
        Id = (int)r.ID,
        RawMaterialZamiennikKod = (string?)r.RAW_MATERIAL_ZAMIENNIK_KOD,
        SeriaId = (int)r.SERIA_ID,
        RawMaterialIlosc = (decimal)r.RAW_MATERIAL_ILOSC,
        IloscRoz = (decimal)r.ILOSC_ROZ,
        RawMaterialEtykieta = (string?)r.RAW_MATERIAL_ETYKIETA,
        ReferenceNumber = (string?)r.REFERENCE_NUMBER,
        VerificationNumber = (string?)r.VERIFICATION_NUMBER,
        RawMaterialLot = (string?)r.RAW_MATERIAL_LOT,
        ZamDostawca = (string?)r.ZAM_DOSTAWCA,
        ZamNumer = (string?)r.ZAM_NUMER,
    };

    // Full mapper for queries that select all columns (GetLedgerRowsByDocAndTypeAsync, GetRzuRowsByNsAsync full)
    private static LedgerRow MapToFullLedgerRow(dynamic r) => new()
    {
        Id = (int)r.ID,
        TransactionType = (string)r.TRANSACTION_TYPE,
        MagDokNagId = (int)r.MAG_DOK_NAG_ID,
        MagDokSpecId = (int)r.MAG_DOK_SPEC_ID,
        SeriaId = (int)r.SERIA_ID,
        RawMaterialZamiennikKod = (string?)r.RAW_MATERIAL_ZAMIENNIK_KOD,
        RawMaterialId = (int?)r.RAW_MATERIAL_ID,
        RawMaterialNazwa = (string?)r.RAW_MATERIAL_NAZWA,
        RawMaterialIlosc = (decimal)r.RAW_MATERIAL_ILOSC,
        IloscRoz = (decimal)r.ILOSC_ROZ,
        RawMaterialEtykieta = (string?)r.RAW_MATERIAL_ETYKIETA,
        ReferenceNumber = (string?)r.REFERENCE_NUMBER,
        VerificationNumber = (string?)r.VERIFICATION_NUMBER,
        RawMaterialLot = (string?)r.RAW_MATERIAL_LOT,
        ZamDostawca = (string?)r.ZAM_DOSTAWCA,
        ZamNumer = (string?)r.ZAM_NUMER,
        Ns = (int?)r.NS,
        FinishedProductSessionId = (int?)r.FINISHED_PRODUCT_SESSION_ID,
        RawMaterialSessionId = (int?)r.RAW_MATERIAL_SESSION_ID,
        FinishedProductEtykieta = (string?)r.FINISHED_PRODUCT_ETYKIETA,
        FinishedProductIlosc = (decimal?)r.FINISHED_PRODUCT_ILOSC,
        FinishedProductId = (int?)r.FINISHED_PRODUCT_ID,
        FinishedGoodsLot = (string?)r.FINISHED_GOODS_LOT,
        FinishedGoodsKod = (string?)r.FINISHED_GOODS_KOD,
        FinishedGoodsName = (string?)r.FINISHED_GOODS_NAME,
        TraceLevel = (int)r.TRACE_LEVEL,
    };

    private static EudrDirection ParseDirection(string s) =>
        s.Equals("REVERSE", StringComparison.OrdinalIgnoreCase)
            ? EudrDirection.Reverse
            : EudrDirection.Forward;
}
