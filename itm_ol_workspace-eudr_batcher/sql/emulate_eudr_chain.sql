-- ============================================================
-- EUDR Full-Chain Emulation Script
-- Firebird 2.5, Dialect 1
-- ============================================================
--
-- Creates complete test fixture data and populates EUDR_INBOX +
-- EUDR_EVENT_JOURNAL with a 12-event sequence covering every
-- handler and every direction.
--
-- Document chain:
--   1.  RIN   FORWARD — PZ receipt  (100 kg Mat-A, 50 kg Mat-B)
--   2.  RPR   FORWARD — WK1 issue   (80 kg Mat-A, partial)
--   3.  ZWR   FORWARD — ZW return   (30 kg Mat-A back from production)
--   4.  RPR   FORWARD — WK2 issue   (20 kg Mat-A + 50 kg Mat-B)
--   5.  RZU   FORWARD — PW receipt  (8 FG units; BOM 10 kg Mat-A + 5 kg Mat-B/unit)
--   6.  OUT   FORWARD — WZ shipment (8 FG units)
--   7.  OUT   REVERSE
--   8.  RZU   REVERSE
--   9.  ZWR   REVERSE
--  10.  RPR   REVERSE — WK2
--  11.  RPR   REVERSE — WK1  (ILOSC_ROZ on source RIN-A → 0)
--  12.  RIN   REVERSE — BR4 passes, compensating rows inserted
--
-- FIFO accounting (Mat-A):
--   RPR1 row: ILOSC=80, ILOSC_ROZ starts 0
--   ZWR fwd credits RPR1: ILOSC_ROZ → -30 → Available = 110
--   RZU consumes 80 from RPR1: ILOSC_ROZ → 50
--   RPR2-A row: ILOSC=20, ILOSC_ROZ=0, Available=20 (RZU does not touch it)
--
-- Prerequisite: Run against a Firebird 2.5 sandbox DB that already
-- has the EUDR schema (eudr_schema.sql) applied.
-- TAGI_ID=3 (KOLEJNOSC=1000, WEIGHT_CONF=2) must exist — confirmed
-- from the live DB; the script references it directly.
--
-- The script selects a real SERIA_ID from the SERIA table.
-- If SERIA has no rows yet, insert one manually first.
-- ============================================================

EXECUTE BLOCK AS
  -- ── Reused constants ──────────────────────────────────────
  DECLARE VARIABLE V_TAGI_ID     INTEGER;
  DECLARE VARIABLE V_SERIA_ID    INTEGER;
  DECLARE VARIABLE V_BIZ_DATE    DATE;

  -- ── Article IDs ───────────────────────────────────────────
  DECLARE VARIABLE V_ART_A_ID    INTEGER;   -- EUDR raw material A
  DECLARE VARIABLE V_ART_B_ID    INTEGER;   -- EUDR raw material B
  DECLARE VARIABLE V_ART_FG_ID   INTEGER;   -- finished good (not EUDR-tagged)

  -- ── Support table IDs ─────────────────────────────────────
  DECLARE VARIABLE V_KLIENCI_ID  INTEGER;
  DECLARE VARIABLE V_ZAM_MAT_ID  INTEGER;
  DECLARE VARIABLE V_NS          INTEGER;   -- ZAMOWIENIA_SPEC_ID (production order position)

  -- ── Warehouse document header IDs ────────────────────────
  DECLARE VARIABLE V_DOC_RIN     INTEGER;
  DECLARE VARIABLE V_DOC_RPR1    INTEGER;
  DECLARE VARIABLE V_DOC_ZWR     INTEGER;
  DECLARE VARIABLE V_DOC_RPR2    INTEGER;
  DECLARE VARIABLE V_DOC_RZU     INTEGER;
  DECLARE VARIABLE V_DOC_OUT     INTEGER;

  -- ── Warehouse document position IDs ──────────────────────
  DECLARE VARIABLE V_SPEC_A_RIN  INTEGER;   -- RIN spec for Mat-A (used as ROZLICZ_ID in RPR)
  DECLARE VARIABLE V_SPEC_B_RIN  INTEGER;   -- RIN spec for Mat-B
  DECLARE VARIABLE V_SPEC_RPR1   INTEGER;
  DECLARE VARIABLE V_SPEC_ZWR    INTEGER;
  DECLARE VARIABLE V_SPEC_RPR2A  INTEGER;
  DECLARE VARIABLE V_SPEC_RPR2B  INTEGER;
  DECLARE VARIABLE V_SPEC_RZU    INTEGER;
  DECLARE VARIABLE V_SPEC_OUT    INTEGER;

  -- ── Sequence helpers ──────────────────────────────────────
  DECLARE VARIABLE V_SEQ         INTEGER;
  DECLARE VARIABLE V_EVT_ID      INTEGER;

BEGIN
  V_TAGI_ID  = 3;                  -- existing EUDR tag (KOLEJNOSC=1000, WEIGHT_CONF=2)
  V_BIZ_DATE = '2026-06-29';
  V_SEQ      = 0;

  -- Pick a real SERIA_ID from the DB (FK-safe)
  SELECT FIRST 1 SERIA_ID FROM SERIA ORDER BY SERIA_ID INTO :V_SERIA_ID;

  -- ══════════════════════════════════════════════════════════
  --  FIXTURE: articles, supplier, BOM norms
  -- ══════════════════════════════════════════════════════════

  -- Raw material A (EUDR-tagged)
  V_ART_A_ID = GEN_ID(SET_ARTYKUL_ID, 1);
  INSERT INTO ARTYKUL (ARTYKUL_ID, ARTYKUL_KOD, ZAMIENNIK_KOD, NAZWA1)
  VALUES (:V_ART_A_ID, 'EUDR-RM-A', 'EUDR-RM-A', 'EUDR Test Raw Material A');

  INSERT INTO ARTYKUL_TAGI (ARTYKUL_TAGI_ID, ARTYKUL_ID, TAGI_ID)
  VALUES (GEN_ID(GEN_ARTYKUL_TAGI_ID, 1), :V_ART_A_ID, :V_TAGI_ID);

  -- Raw material B (EUDR-tagged)
  V_ART_B_ID = GEN_ID(SET_ARTYKUL_ID, 1);
  INSERT INTO ARTYKUL (ARTYKUL_ID, ARTYKUL_KOD, ZAMIENNIK_KOD, NAZWA1)
  VALUES (:V_ART_B_ID, 'EUDR-RM-B', 'EUDR-RM-B', 'EUDR Test Raw Material B');

  INSERT INTO ARTYKUL_TAGI (ARTYKUL_TAGI_ID, ARTYKUL_ID, TAGI_ID)
  VALUES (GEN_ID(GEN_ARTYKUL_TAGI_ID, 1), :V_ART_B_ID, :V_TAGI_ID);

  -- Finished goods article (not EUDR-tagged — no ARTYKUL_TAGI row)
  V_ART_FG_ID = GEN_ID(SET_ARTYKUL_ID, 1);
  INSERT INTO ARTYKUL (ARTYKUL_ID, ARTYKUL_KOD, ZAMIENNIK_KOD, NAZWA1)
  VALUES (:V_ART_FG_ID, 'EUDR-FG-001', 'EUDR-FG-001', 'EUDR Test Finished Good');

  V_KLIENCI_ID = GEN_ID(SET_KLIENCI_ID, 1);
  INSERT INTO KLIENCI (KLIENCI_ID, KLIENCI_OPIS) VALUES (:V_KLIENCI_ID, 'EUDR Test Supplier SA');

  -- Purchase order (referenced by RIN positions via ZAM_MAT_ID)
  V_ZAM_MAT_ID = GEN_ID(SET_ZAM_MAT_ID, 1);
  INSERT INTO ZAM_MAT (ZAM_MAT_ID, ZAM_NUMER, KLIENCI_ID)
  VALUES (:V_ZAM_MAT_ID, 'PO-EUDR-TEST-001', :V_KLIENCI_ID);

  -- BOM norms for the production order position (NS):
  --   10 kg Mat-A + 5 kg Mat-B per finished-good unit
  -- Select an existing ZAMOWIENIA_SPEC_ID (FK-safe, same pattern as SERIA_ID).
  -- Our freshly-created ARTYKUL_IDs won't collide with any existing norms.
  SELECT FIRST 1 ZAMOWIENIA_SPEC_ID FROM ZAMOWIENIA_SPEC
  ORDER BY ZAMOWIENIA_SPEC_ID INTO :V_NS;

  INSERT INTO MAG_DOK_SPEC_PRO (MAG_DOK_SPEC_PRO_ID, ZAMOWIENIA_SPEC_ID, ARTYKUL_ID, ILOSC)
  VALUES (GEN_ID(SET_MAG_DOK_SPEC_PRO_ID, 1), :V_NS, :V_ART_A_ID, 10.0);

  INSERT INTO MAG_DOK_SPEC_PRO (MAG_DOK_SPEC_PRO_ID, ZAMOWIENIA_SPEC_ID, ARTYKUL_ID, ILOSC)
  VALUES (GEN_ID(SET_MAG_DOK_SPEC_PRO_ID, 1), :V_NS, :V_ART_B_ID,  5.0);

  -- ══════════════════════════════════════════════════════════
  --  EVENT 1: RIN FORWARD — PZ receipt
  --  100 kg Mat-A (with PO ref + lot) + 50 kg Mat-B
  --  MAG_DOK_SPEC_IDs become ROZLICZ_IDs in downstream WK docs
  -- ══════════════════════════════════════════════════════════
  V_DOC_RIN = GEN_ID(SET_MAG_DOK_NAG_ID, 1);
  -- ZAM_MAT_ID lives on the document header, not on spec lines
  INSERT INTO MAG_DOK_NAG (MAG_DOK_NAG_ID, ZAM_MAT_ID) VALUES (:V_DOC_RIN, :V_ZAM_MAT_ID);

  V_SPEC_A_RIN = GEN_ID(SET_MAG_DOK_SPEC_ID, 1);
  INSERT INTO MAG_DOK_SPEC (MAG_DOK_SPEC_ID, MAG_DOK_NAG_ID, ARTYKUL_ID, ILOSC, NUMER_PARTII_LOT)
  VALUES (:V_SPEC_A_RIN, :V_DOC_RIN, :V_ART_A_ID, 100.0, 'LOT-A-2026');

  V_SPEC_B_RIN = GEN_ID(SET_MAG_DOK_SPEC_ID, 1);
  INSERT INTO MAG_DOK_SPEC (MAG_DOK_SPEC_ID, MAG_DOK_NAG_ID, ARTYKUL_ID, ILOSC, NUMER_PARTII_LOT)
  VALUES (:V_SPEC_B_RIN, :V_DOC_RIN, :V_ART_B_ID,  50.0, 'LOT-B-2026');

  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_RIN, 1, 'FORWARD', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

  -- ══════════════════════════════════════════════════════════
  --  EVENT 2: RPR FORWARD — WK1 (partial issue)
  --  80 kg Mat-A only; ROZLICZ_ID → RIN spec A (V_SPEC_A_RIN)
  --  After RPR handler: RIN-A ledger ILOSC_ROZ = 80
  --                     RPR1 ledger  ILOSC_ROZ = 0
  -- ══════════════════════════════════════════════════════════
  V_DOC_RPR1 = GEN_ID(SET_MAG_DOK_NAG_ID, 1);
  INSERT INTO MAG_DOK_NAG (MAG_DOK_NAG_ID) VALUES (:V_DOC_RPR1);

  V_SPEC_RPR1 = GEN_ID(SET_MAG_DOK_SPEC_ID, 1);
  INSERT INTO MAG_DOK_SPEC (MAG_DOK_SPEC_ID, MAG_DOK_NAG_ID, ARTYKUL_ID, ILOSC, ROZLICZ_ID)
  VALUES (:V_SPEC_RPR1, :V_DOC_RPR1, :V_ART_A_ID, 80.0, :V_SPEC_A_RIN);

  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_RPR1, 2, 'FORWARD', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

  -- ══════════════════════════════════════════════════════════
  --  EVENT 3: ZWR FORWARD — ZW return 30 kg Mat-A
  --  Handler credits the LAST RPR for Mat-A in this series
  --  (= RPR1) by AdvanceIloscRozAsync(RPR1, -30)
  --  → RPR1 ledger ILOSC_ROZ = -30, Available = 110
  -- ══════════════════════════════════════════════════════════
  V_DOC_ZWR = GEN_ID(SET_MAG_DOK_NAG_ID, 1);
  INSERT INTO MAG_DOK_NAG (MAG_DOK_NAG_ID) VALUES (:V_DOC_ZWR);

  V_SPEC_ZWR = GEN_ID(SET_MAG_DOK_SPEC_ID, 1);
  INSERT INTO MAG_DOK_SPEC (MAG_DOK_SPEC_ID, MAG_DOK_NAG_ID, ARTYKUL_ID, ILOSC)
  VALUES (:V_SPEC_ZWR, :V_DOC_ZWR, :V_ART_A_ID, 30.0);

  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_ZWR, 200, 'FORWARD', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

  -- ══════════════════════════════════════════════════════════
  --  EVENT 4: RPR FORWARD — WK2 (two positions)
  --  20 kg Mat-A (remainder from RIN-A) + 50 kg Mat-B (full RIN-B)
  --  After handler: RIN-A ILOSC_ROZ = 100, RIN-B ILOSC_ROZ = 50
  -- ══════════════════════════════════════════════════════════
  V_DOC_RPR2 = GEN_ID(SET_MAG_DOK_NAG_ID, 1);
  INSERT INTO MAG_DOK_NAG (MAG_DOK_NAG_ID) VALUES (:V_DOC_RPR2);

  V_SPEC_RPR2A = GEN_ID(SET_MAG_DOK_SPEC_ID, 1);
  INSERT INTO MAG_DOK_SPEC (MAG_DOK_SPEC_ID, MAG_DOK_NAG_ID, ARTYKUL_ID, ILOSC, ROZLICZ_ID)
  VALUES (:V_SPEC_RPR2A, :V_DOC_RPR2, :V_ART_A_ID, 20.0, :V_SPEC_A_RIN);

  V_SPEC_RPR2B = GEN_ID(SET_MAG_DOK_SPEC_ID, 1);
  INSERT INTO MAG_DOK_SPEC (MAG_DOK_SPEC_ID, MAG_DOK_NAG_ID, ARTYKUL_ID, ILOSC, ROZLICZ_ID)
  VALUES (:V_SPEC_RPR2B, :V_DOC_RPR2, :V_ART_B_ID, 50.0, :V_SPEC_B_RIN);

  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_RPR2, 2, 'FORWARD', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

  -- ══════════════════════════════════════════════════════════
  --  EVENT 5: RZU FORWARD — PW finished-product receipt
  --  8 FG units; NS = V_NS
  --  FifoEngine allocations (no shortage):
  --    Mat-A: RPR1 Available=110 → takes 80; RPR2-A not touched
  --    Mat-B: RPR2-B Available=50 → takes 40
  --  ZAMOWIENIA_SPEC_ID links to MAG_DOK_SPEC_PRO norms
  -- ══════════════════════════════════════════════════════════
  V_DOC_RZU = GEN_ID(SET_MAG_DOK_NAG_ID, 1);
  INSERT INTO MAG_DOK_NAG (MAG_DOK_NAG_ID) VALUES (:V_DOC_RZU);

  V_SPEC_RZU = GEN_ID(SET_MAG_DOK_SPEC_ID, 1);
  INSERT INTO MAG_DOK_SPEC (MAG_DOK_SPEC_ID, MAG_DOK_NAG_ID, ARTYKUL_ID, ILOSC, ZAMOWIENIA_SPEC_ID, NUMER_PARTII_LOT)
  VALUES (:V_SPEC_RZU, :V_DOC_RZU, :V_ART_FG_ID, 8.0, :V_NS, 'FG-LOT-2026');

  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_RZU, 100, 'FORWARD', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

  -- ══════════════════════════════════════════════════════════
  --  EVENT 6: OUT FORWARD — WZ shipment
  --  8 FG units; same NS so OUT handler finds RZU rows by NS.
  --  ETYKIETA simulates a finished-goods GS1 label.
  -- ══════════════════════════════════════════════════════════
  V_DOC_OUT = GEN_ID(SET_MAG_DOK_NAG_ID, 1);
  INSERT INTO MAG_DOK_NAG (MAG_DOK_NAG_ID) VALUES (:V_DOC_OUT);

  V_SPEC_OUT = GEN_ID(SET_MAG_DOK_SPEC_ID, 1);
  INSERT INTO MAG_DOK_SPEC (MAG_DOK_SPEC_ID, MAG_DOK_NAG_ID, ARTYKUL_ID, ILOSC, ZAMOWIENIA_SPEC_ID, ETYKIETA, NUMER_PARTII_LOT)
  VALUES (:V_SPEC_OUT, :V_DOC_OUT, :V_ART_FG_ID, 8.0, :V_NS, '00123456789000000001', 'FG-LOT-2026');

  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_OUT, 110, 'FORWARD', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

  -- ══════════════════════════════════════════════════════════
  --  REVERSALS — same MAG_DOK_NAG_IDs, DIRECTION = 'REVERSE'
  --  Order: newest document first (OUT → RZU → ZWR → RPR2 → RPR1 → RIN)
  --  This order satisfies FIFO/BR4 constraints so all reverses succeed.
  -- ══════════════════════════════════════════════════════════

  -- EVENT 7: OUT REVERSE
  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_OUT, 110, 'REVERSE', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

  -- EVENT 8: RZU REVERSE
  -- Handler advances RawMaterialSessionId (= source RPR row ID) by -qty,
  -- restoring ILOSC_ROZ on RPR rows so they can be fully reversed next.
  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_RZU, 100, 'REVERSE', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

  -- EVENT 9: ZWR REVERSE
  -- Handler finds last RPR for Mat-A (= RPR2 at this point in the sequence)
  -- and advances its ILOSC_ROZ += 30. RPR1 remains at -30 from the forward pass.
  -- This is expected behaviour — see handler comments for asymmetry note.
  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_ZWR, 200, 'REVERSE', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

  -- EVENT 10: RPR REVERSE — WK2 (2 positions: Mat-A 20 kg + Mat-B 50 kg)
  -- Credits RIN-A ILOSC_ROZ -= 20 and RIN-B ILOSC_ROZ -= 50
  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_RPR2, 2, 'REVERSE', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

  -- EVENT 11: RPR REVERSE — WK1 (Mat-A 80 kg)
  -- Credits RIN-A ILOSC_ROZ -= 80 → RIN-A ILOSC_ROZ = 100-20-80 = 0
  -- RIN-B ILOSC_ROZ = 50-50 = 0 (already 0 after WK2 reverse)
  -- BR4 will now pass for the RIN reverse that follows.
  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_RPR1, 2, 'REVERSE', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

  -- EVENT 12: RIN REVERSE — PZ reversal
  -- BR4 backstop: ILOSC_ROZ = 0 on both RIN rows → reversal allowed.
  -- Inserts compensating rows with negated ILOSC for both Mat-A and Mat-B.
  V_SEQ = V_SEQ + 1;
  V_EVT_ID = GEN_ID(GEN_EUDR_INBOX_ID, 1);
  INSERT INTO EUDR_INBOX
    (EVENT_ID, MAG_DOK_NAG_ID, EUDR_TYPE, DIRECTION, SERIA_ID, OCCURRENCE_SEQ, EVENT_TIMESTAMP, BUSINESS_DATE)
  VALUES
    (:V_EVT_ID, :V_DOC_RIN, 1, 'REVERSE', :V_SERIA_ID, :V_SEQ, CURRENT_TIMESTAMP, :V_BIZ_DATE);
  INSERT INTO EUDR_EVENT_JOURNAL (ID, EVENT_ID, BUSINESS_DATE, OCCURRENCE_SEQ)
  VALUES (GEN_ID(GEN_EUDR_JOURNAL_ID, 1), :V_EVT_ID, :V_BIZ_DATE, :V_SEQ);

END

-- ============================================================
--  Verification: run after the EXECUTE BLOCK commits to confirm
--  the inbox is populated correctly.
-- ============================================================
/*
SELECT i.EVENT_ID, i.EUDR_TYPE, i.DIRECTION, i.OCCURRENCE_SEQ,
       i.MAG_DOK_NAG_ID, i.SERIA_ID
FROM EUDR_INBOX i
WHERE i.BUSINESS_DATE = '2026-06-29'
ORDER BY i.OCCURRENCE_SEQ;
*/
