namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class SalesSql
{
    public static readonly string[] Tables =
    [
        "counters", "counter_devices", "supervisor_approvals", "sales_invoices", "sales_invoice_lines", "sales_invoice_payments",
    ];

    /// <summary>Issued invoices, their lines and payments are never edited or deleted; returns are credit notes.</summary>
    public static readonly string[] AppendOnly = ["sales_invoices", "sales_invoice_lines", "sales_invoice_payments"];

    /// <summary>
    /// A supervisor approval can only be used once, and nothing else about it may change.
    /// A counter's code is part of every invoice number it issued, so it can never change.
    /// </summary>
    public const string Guards = """
        CREATE FUNCTION sb_supervisor_approval_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Supervisor approvals cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.used_at_utc IS NOT NULL
               OR (NEW.id, NEW.tenant_id, NEW.business_id, NEW.counter_id, NEW.kind, NEW.variant_unit_id, NEW.approved_price, NEW.max_discount,
                   NEW.reason, NEW.approved_by_user_id, NEW.requested_by_user_id, NEW.token_hash, NEW.created_at_utc, NEW.expires_at_utc)
                  IS DISTINCT FROM
                  (OLD.id, OLD.tenant_id, OLD.business_id, OLD.counter_id, OLD.kind, OLD.variant_unit_id, OLD.approved_price, OLD.max_discount,
                   OLD.reason, OLD.approved_by_user_id, OLD.requested_by_user_id, OLD.token_hash, OLD.created_at_utc, OLD.expires_at_utc) THEN
                RAISE EXCEPTION 'A supervisor approval can only be marked used, once.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_supervisor_approvals_guard BEFORE UPDATE OR DELETE ON supervisor_approvals
            FOR EACH ROW EXECUTE FUNCTION sb_supervisor_approval_guard();

        CREATE FUNCTION sb_counter_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Counters cannot be deleted; switch them off.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.code, NEW.store_id, NEW.business_id, NEW.tenant_id) IS DISTINCT FROM (OLD.code, OLD.store_id, OLD.business_id, OLD.tenant_id) THEN
                RAISE EXCEPTION 'A counter''s code and store cannot change (they are part of its invoice numbers).' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_counters_guard BEFORE UPDATE OR DELETE ON counters
            FOR EACH ROW EXECUTE FUNCTION sb_counter_guard();
        """;

    public const string DropGuards = """
        DROP TRIGGER IF EXISTS trg_counters_guard ON counters;
        DROP FUNCTION IF EXISTS sb_counter_guard();
        DROP TRIGGER IF EXISTS trg_supervisor_approvals_guard ON supervisor_approvals;
        DROP FUNCTION IF EXISTS sb_supervisor_approval_guard();
        """;
}
