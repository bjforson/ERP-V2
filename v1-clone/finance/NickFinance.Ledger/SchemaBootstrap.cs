using Microsoft.EntityFrameworkCore;

namespace NickFinance.Ledger;

/// <summary>
/// DB-level invariants that EF migrations can't express cleanly. Run once
/// per DB after the EF schema is in place. Idempotent.
///
/// Enforces:
///   • SUM(debit_minor) = SUM(credit_minor) per event_id, deferred until
///     commit so callers can insert header + lines in any order in one tx
///   • ledger_events rows are insert-only — UPDATE and DELETE rejected at
///     the DB layer. Reversals are new rows.
///   • ledger_event_lines rows are insert-only for the same reason.
/// </summary>
public static class SchemaBootstrap
{
    public static async Task ApplyConstraintsAsync(LedgerDbContext db, CancellationToken ct = default)
    {
        // The constraint trigger re-checks balance per event on every line
        // insert. DEFERRABLE INITIALLY DEFERRED means the check runs at
        // commit, so inserting an event + its lines in one tx is fine even
        // though the intermediate state (header inserted, lines not yet) is
        // unbalanced.
        const string sql = """
-- Balance invariant: DR = CR per event_id, checked at commit time.
CREATE OR REPLACE FUNCTION finance.ledger_events_balanced_fn()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_debits  bigint;
    v_credits bigint;
BEGIN
    SELECT COALESCE(SUM(debit_minor), 0), COALESCE(SUM(credit_minor), 0)
      INTO v_debits, v_credits
      FROM finance.ledger_event_lines
     WHERE event_id = COALESCE(NEW.event_id, OLD.event_id);

    IF v_debits <> v_credits THEN
        RAISE EXCEPTION 'Unbalanced ledger event %: debits=% credits=% diff=%',
            COALESCE(NEW.event_id, OLD.event_id), v_debits, v_credits, v_debits - v_credits;
    END IF;
    RETURN NULL;
END;
$$;

DROP TRIGGER IF EXISTS ledger_events_balanced ON finance.ledger_event_lines;
CREATE CONSTRAINT TRIGGER ledger_events_balanced
    AFTER INSERT OR UPDATE OR DELETE ON finance.ledger_event_lines
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION finance.ledger_events_balanced_fn();

-- Append-only: reject UPDATE and DELETE on events and lines.
CREATE OR REPLACE FUNCTION finance.ledger_reject_mutation_fn()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'ledger tables are append-only; % rejected on %',
        TG_OP, TG_TABLE_NAME;
END;
$$;

DROP TRIGGER IF EXISTS ledger_events_no_update ON finance.ledger_events;
CREATE TRIGGER ledger_events_no_update
    BEFORE UPDATE OR DELETE ON finance.ledger_events
    FOR EACH ROW EXECUTE FUNCTION finance.ledger_reject_mutation_fn();

DROP TRIGGER IF EXISTS ledger_lines_no_update ON finance.ledger_event_lines;
CREATE TRIGGER ledger_lines_no_update
    BEFORE UPDATE OR DELETE ON finance.ledger_event_lines
    FOR EACH ROW EXECUTE FUNCTION finance.ledger_reject_mutation_fn();
""";

        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
