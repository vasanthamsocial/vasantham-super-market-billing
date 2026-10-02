namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class PurchasesSql
{
    public static readonly string[] Tables = ["suppliers", "purchase_settings", "grns", "grn_lines", "grn_expenses", "grn_allocations"];

    public static readonly string[] AppendOnly = ["grn_lines", "grn_expenses", "grn_allocations"];

    /// <summary>Businesses that existed before purchasing get the default thresholds (5% reason, 15% approval).</summary>
    public const string SeedSettings = """
        INSERT INTO purchase_settings (business_id, tenant_id, cost_reason_threshold_percent, cost_approval_threshold_percent, allow_loss_leader)
        SELECT id, tenant_id, 5, 15, false FROM businesses
        ON CONFLICT (business_id) DO NOTHING;
        """;

    /// <summary>A goods receipt's figures never change; it moves once from pending approval to posted or rejected.</summary>
    public const string GrnGuard = """
        CREATE FUNCTION sb_grn_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Goods receipts cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.supplier_id, NEW.number, NEW.sequence_number, NEW.supplier_invoice_number,
                NEW.supplier_invoice_date, NEW.classification, NEW.purchase_order_reference, NEW.is_inter_state, NEW.tax_recoverable, NEW.business_date,
                NEW.notes, NEW.gross_total, NEW.discount_total, NEW.taxable_total, NEW.cgst_total, NEW.sgst_total, NEW.igst_total, NEW.cess_total,
                NEW.round_off, NEW.invoice_total, NEW.expenses_total, NEW.landed_total, NEW.received_by_user_id, NEW.received_at_utc,
                NEW.idempotency_key, NEW.request_hash)
               IS DISTINCT FROM
               (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.supplier_id, OLD.number, OLD.sequence_number, OLD.supplier_invoice_number,
                OLD.supplier_invoice_date, OLD.classification, OLD.purchase_order_reference, OLD.is_inter_state, OLD.tax_recoverable, OLD.business_date,
                OLD.notes, OLD.gross_total, OLD.discount_total, OLD.taxable_total, OLD.cgst_total, OLD.sgst_total, OLD.igst_total, OLD.cess_total,
                OLD.round_off, OLD.invoice_total, OLD.expenses_total, OLD.landed_total, OLD.received_by_user_id, OLD.received_at_utc,
                OLD.idempotency_key, OLD.request_hash) THEN
                RAISE EXCEPTION 'A goods receipt''s figures cannot be changed.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.status <> 'PENDING_APPROVAL' AND (NEW.status, NEW.posted_at_utc, NEW.approval_request_id) IS DISTINCT FROM (OLD.status, OLD.posted_at_utc, OLD.approval_request_id) THEN
                RAISE EXCEPTION 'A % goods receipt cannot change.', lower(OLD.status) USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_grns_guard BEFORE UPDATE OR DELETE ON grns FOR EACH ROW EXECUTE FUNCTION sb_grn_guard();
        CREATE TRIGGER trg_grns_no_truncate BEFORE TRUNCATE ON grns FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
        """;

    public const string DropGrnGuard = """
        DROP TRIGGER IF EXISTS trg_grns_no_truncate ON grns;
        DROP TRIGGER IF EXISTS trg_grns_guard ON grns;
        DROP FUNCTION IF EXISTS sb_grn_guard();
        """;
}
