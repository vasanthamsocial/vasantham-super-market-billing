namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class AccountsSql
{
    public static readonly string[] Tables = ["debtors", "supplier_ledger", "debtor_ledger", "supplier_settlements", "debtor_settlements", "supplier_payments"];

    public static readonly string[] AppendOnly = ["supplier_ledger", "debtor_ledger", "supplier_settlements", "debtor_settlements", "supplier_payments"];

    /// <summary>
    /// Goods receipts posted before supplier accounts existed become the first entries of their suppliers' ledgers,
    /// in the order they were posted, due on the invoice date (suppliers had no credit period then).
    /// </summary>
    public const string BackfillSupplierLedger = """
        INSERT INTO supplier_ledger (id, tenant_id, business_id, supplier_id, sequence, entry_type, store_id, document_id, document_number, entry_date,
                                     due_date, amount, balance_after, narration, created_by_user_id, created_at_utc)
        SELECT gen_random_uuid(), g.tenant_id, g.business_id, g.supplier_id,
               row_number() OVER w, 'GRN', g.store_id, g.id, g.number, g.business_date, g.supplier_invoice_date, g.invoice_total,
               sum(g.invoice_total) OVER w, 'Goods receipt ' || g.number || ', invoice ' || g.supplier_invoice_number,
               g.received_by_user_id, g.posted_at_utc
          FROM grns g
         WHERE g.status = 'POSTED' AND g.invoice_total > 0
        WINDOW w AS (PARTITION BY g.supplier_id ORDER BY g.posted_at_utc, g.id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
         ORDER BY g.supplier_id, g.posted_at_utc, g.id;
        """;

    /// <summary>Each entry of an account follows the one before: next number, previous balance plus its amount.</summary>
    public static string LedgerChain(string table, string partyColumn) => $$"""
        CREATE FUNCTION sb_{{table}}_chain() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE
            previous_sequence bigint;
            previous_balance numeric;
        BEGIN
            SELECT sequence, balance_after INTO previous_sequence, previous_balance
              FROM {{table}} WHERE {{partyColumn}} = NEW.{{partyColumn}} ORDER BY sequence DESC LIMIT 1;
            IF NEW.sequence <> coalesce(previous_sequence, 0) + 1 OR NEW.balance_after <> coalesce(previous_balance, 0) + NEW.amount THEN
                RAISE EXCEPTION 'Account entry % does not follow the previous entry of the account.', NEW.sequence USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_{{table}}_chain BEFORE INSERT ON {{table}} FOR EACH ROW EXECUTE FUNCTION sb_{{table}}_chain();
        """;

    public static string DropLedgerChain(string table) => $"""
        DROP TRIGGER IF EXISTS trg_{table}_chain ON {table};
        DROP FUNCTION IF EXISTS sb_{table}_chain();
        """;

    /// <summary>
    /// A settlement applies a payment (negative entry) to a charge (positive entry) of the same account, never beyond
    /// what is left of either. Both entries are locked while checking, so concurrent settlements queue.
    /// </summary>
    public static string SettlementGuard(string table, string ledger) => $$"""
        CREATE FUNCTION sb_{{table}}_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE
            charge numeric;
            payment numeric;
        BEGIN
            SELECT amount INTO charge FROM {{ledger}} WHERE id = NEW.charge_entry_id FOR UPDATE;
            SELECT amount INTO payment FROM {{ledger}} WHERE id = NEW.payment_entry_id FOR UPDATE;
            IF charge IS NULL OR charge <= 0 OR payment IS NULL OR payment >= 0 THEN
                RAISE EXCEPTION 'A settlement applies a payment to a charge.' USING ERRCODE = 'check_violation';
            END IF;
            IF (SELECT coalesce(sum(amount), 0) FROM {{table}} WHERE charge_entry_id = NEW.charge_entry_id) + NEW.amount > charge
               OR (SELECT coalesce(sum(amount), 0) FROM {{table}} WHERE payment_entry_id = NEW.payment_entry_id) + NEW.amount > -payment THEN
                RAISE EXCEPTION 'A settlement cannot exceed what is left of the charge or the payment.' USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_{{table}}_guard BEFORE INSERT ON {{table}} FOR EACH ROW EXECUTE FUNCTION sb_{{table}}_guard();
        """;

    public static string DropSettlementGuard(string table) => $"""
        DROP TRIGGER IF EXISTS trg_{table}_guard ON {table};
        DROP FUNCTION IF EXISTS sb_{table}_guard();
        """;

    /// <summary>A debtor's account is closed only when nothing is owed either way; debtors are never deleted.</summary>
    public const string DebtorGuard = """
        CREATE FUNCTION sb_debtor_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Debtors cannot be deleted; close the account instead.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF NEW.status = 'CLOSED' AND OLD.status <> 'CLOSED'
               AND (SELECT coalesce(sum(amount), 0) FROM debtor_ledger WHERE debtor_id = NEW.id) <> 0 THEN
                RAISE EXCEPTION 'A debtor account can be closed only at a zero balance.' USING ERRCODE = 'check_violation';
            END IF;
            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.code) IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.code) THEN
                RAISE EXCEPTION 'A debtor''s code cannot change.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_debtors_guard BEFORE UPDATE OR DELETE ON debtors FOR EACH ROW EXECUTE FUNCTION sb_debtor_guard();
        """;

    public const string DropDebtorGuard = """
        DROP TRIGGER IF EXISTS trg_debtors_guard ON debtors;
        DROP FUNCTION IF EXISTS sb_debtor_guard();
        """;
}
