namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class CreditSalesSql
{
    public static readonly string[] Tables = ["debtor_receipts"];

    /// <summary>A receipt taken at a counter belongs to that counter's open shift of the cashier who took it; office receipts have no shift.</summary>
    public const string ReceiptShiftGuard = """
        CREATE FUNCTION sb_debtor_receipt_shift() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE
            s shifts%ROWTYPE;
        BEGIN
            IF NEW.shift_id IS NULL THEN
                RETURN NEW;
            END IF;
            SELECT * INTO s FROM shifts WHERE id = NEW.shift_id;
            IF s.status IS DISTINCT FROM 'OPEN' THEN
                RAISE EXCEPTION 'Shift % is not open.', NEW.shift_id USING ERRCODE = 'restrict_violation';
            END IF;
            IF s.counter_id <> NEW.counter_id OR s.cashier_user_id <> NEW.cashier_user_id THEN
                RAISE EXCEPTION 'The receipt belongs to another counter''s or cashier''s shift.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_debtor_receipts_open_shift BEFORE INSERT ON debtor_receipts FOR EACH ROW EXECUTE FUNCTION sb_debtor_receipt_shift();
        """;

    public const string DropReceiptShiftGuard = """
        DROP TRIGGER IF EXISTS trg_debtor_receipts_open_shift ON debtor_receipts;
        DROP FUNCTION IF EXISTS sb_debtor_receipt_shift();
        """;
}
