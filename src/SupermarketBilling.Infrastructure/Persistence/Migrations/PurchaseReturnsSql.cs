namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class PurchaseReturnsSql
{
    public static readonly string[] Tables = ["purchase_returns", "purchase_return_lines"];

    /// <summary>
    /// A return line belongs to a line of the receipt its return is against, and all returns of a receipt line never
    /// exceed what it received (free goods included). The receipt line is locked while checking.
    /// </summary>
    public const string ReturnLineGuard = """
        CREATE FUNCTION sb_purchase_return_line_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE
            received numeric;
            line_grn uuid;
        BEGIN
            SELECT quantity + free_quantity, grn_id INTO received, line_grn FROM grn_lines WHERE id = NEW.grn_line_id FOR UPDATE;
            IF line_grn IS DISTINCT FROM (SELECT grn_id FROM purchase_returns WHERE id = NEW.purchase_return_id) THEN
                RAISE EXCEPTION 'A return line must be against a line of the receipt the return is for.' USING ERRCODE = 'check_violation';
            END IF;
            IF (SELECT coalesce(sum(quantity), 0) FROM purchase_return_lines WHERE grn_line_id = NEW.grn_line_id) + NEW.quantity > received THEN
                RAISE EXCEPTION 'More would be returned than the receipt line received.' USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_purchase_return_lines_guard BEFORE INSERT ON purchase_return_lines FOR EACH ROW EXECUTE FUNCTION sb_purchase_return_line_guard();
        """;

    public const string DropReturnLineGuard = """
        DROP TRIGGER IF EXISTS trg_purchase_return_lines_guard ON purchase_return_lines;
        DROP FUNCTION IF EXISTS sb_purchase_return_line_guard();
        """;
}
