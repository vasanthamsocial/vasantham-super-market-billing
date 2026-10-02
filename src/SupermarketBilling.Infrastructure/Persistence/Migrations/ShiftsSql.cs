namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class ShiftsSql
{
    public static readonly string[] Tables = ["shifts", "shift_counts", "cash_movements"];

    public static readonly string[] AppendOnly = ["shift_counts", "cash_movements"];

    /// <summary>
    /// A shift is never deleted; its opening never changes; it closes once and its figures are then fixed; a difference
    /// is reviewed once. Bills, returns and cash movements can only be recorded in an open shift of the same counter
    /// and cashier (the application checks this too, under a lock).
    /// </summary>
    public const string Guards = """
        CREATE FUNCTION sb_shift_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Shifts cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.counter_id, NEW.cashier_user_id, NEW.business_date, NEW.opened_at_utc, NEW.opening_float)
               IS DISTINCT FROM
               (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.counter_id, OLD.cashier_user_id, OLD.business_date, OLD.opened_at_utc, OLD.opening_float) THEN
                RAISE EXCEPTION 'A shift''s opening cannot be changed.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.status = 'CLOSED' AND (NEW.status, NEW.closed_at_utc, NEW.closed_by_user_id, NEW.expected_cash, NEW.counted_cash, NEW.difference, NEW.close_note)
               IS DISTINCT FROM (OLD.status, OLD.closed_at_utc, OLD.closed_by_user_id, OLD.expected_cash, OLD.counted_cash, OLD.difference, OLD.close_note) THEN
                RAISE EXCEPTION 'A closed shift''s count cannot be changed.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.reviewed_by_user_id IS NOT NULL AND (NEW.reviewed_by_user_id, NEW.reviewed_at_utc, NEW.review_note)
               IS DISTINCT FROM (OLD.reviewed_by_user_id, OLD.reviewed_at_utc, OLD.review_note) THEN
                RAISE EXCEPTION 'A shift''s review cannot be changed.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF NEW.status = 'OPEN' AND NEW.reviewed_by_user_id IS NOT NULL THEN
                RAISE EXCEPTION 'An open shift cannot be reviewed.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_shifts_guard BEFORE UPDATE OR DELETE ON shifts FOR EACH ROW EXECUTE FUNCTION sb_shift_guard();
        CREATE TRIGGER trg_shifts_no_truncate BEFORE TRUNCATE ON shifts FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();

        CREATE FUNCTION sb_require_open_shift() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE
            s shifts%ROWTYPE;
        BEGIN
            IF NEW.shift_id IS NULL THEN
                IF TG_TABLE_NAME = 'cash_movements' THEN
                    RAISE EXCEPTION 'A cash movement needs a shift.' USING ERRCODE = 'restrict_violation';
                END IF;
                RAISE EXCEPTION 'A % must be recorded in a shift.', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
            END IF;
            SELECT * INTO s FROM shifts WHERE id = NEW.shift_id;
            IF s.status IS DISTINCT FROM 'OPEN' THEN
                RAISE EXCEPTION 'Shift % is not open.', NEW.shift_id USING ERRCODE = 'restrict_violation';
            END IF;
            -- Nested: NEW.counter_id does not exist on cash_movements, and PL/pgSQL would evaluate it inside an AND.
            IF TG_TABLE_NAME <> 'cash_movements' THEN
                IF s.counter_id <> NEW.counter_id OR s.cashier_user_id <> NEW.cashier_user_id THEN
                    RAISE EXCEPTION 'The document belongs to another counter''s or cashier''s shift.' USING ERRCODE = 'restrict_violation';
                END IF;
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_sales_invoices_open_shift BEFORE INSERT ON sales_invoices FOR EACH ROW EXECUTE FUNCTION sb_require_open_shift();
        CREATE TRIGGER trg_sales_returns_open_shift BEFORE INSERT ON sales_returns FOR EACH ROW EXECUTE FUNCTION sb_require_open_shift();
        CREATE TRIGGER trg_cash_movements_open_shift BEFORE INSERT ON cash_movements FOR EACH ROW EXECUTE FUNCTION sb_require_open_shift();
        """;

    public const string DropGuards = """
        DROP TRIGGER IF EXISTS trg_cash_movements_open_shift ON cash_movements;
        DROP TRIGGER IF EXISTS trg_sales_returns_open_shift ON sales_returns;
        DROP TRIGGER IF EXISTS trg_sales_invoices_open_shift ON sales_invoices;
        DROP FUNCTION IF EXISTS sb_require_open_shift();
        DROP TRIGGER IF EXISTS trg_shifts_no_truncate ON shifts;
        DROP TRIGGER IF EXISTS trg_shifts_guard ON shifts;
        DROP FUNCTION IF EXISTS sb_shift_guard();
        """;
}
