namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class DispatchSql
{
    public static readonly string[] Tables =
        ["transporters", "transporter_branches", "transporter_routes", "invoice_fulfilments", "consignments", "consignment_invoices", "delivery_preferences"];

    /// <summary>
    /// A dispatch is never deleted or edited; the only change is being cancelled, once, with who, when and why. A bill
    /// is in at most one dispatch that stands (also checked by the service under a lock on the bill's delivery record).
    /// A bill's delivery cannot change once its goods are dispatched, and is never deleted.
    /// </summary>
    public const string Guards = """
        CREATE FUNCTION sb_consignment_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Dispatches cannot be deleted; cancel them instead.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.status <> 'DISPATCHED' OR NEW.status <> 'CANCELLED'
               OR (to_jsonb(NEW) - ARRAY['status', 'cancel_reason', 'cancelled_by_user_id', 'cancelled_at_utc'])
                  IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['status', 'cancel_reason', 'cancelled_by_user_id', 'cancelled_at_utc']) THEN
                RAISE EXCEPTION 'A dispatch can only be cancelled, once.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_consignments_guard BEFORE UPDATE OR DELETE ON consignments FOR EACH ROW EXECUTE FUNCTION sb_consignment_guard();

        CREATE FUNCTION sb_consignment_invoice_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM consignments c WHERE c.id = NEW.consignment_id AND c.status = 'DISPATCHED') THEN
                RAISE EXCEPTION 'Bills are added to a dispatch only when it is recorded.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF EXISTS (SELECT 1 FROM consignment_invoices ci JOIN consignments c ON c.id = ci.consignment_id
                        WHERE ci.invoice_id = NEW.invoice_id AND ci.consignment_id <> NEW.consignment_id AND c.status = 'DISPATCHED') THEN
                RAISE EXCEPTION 'This bill is already in a dispatch.' USING ERRCODE = 'unique_violation';
            END IF;
            IF NOT EXISTS (SELECT 1 FROM consignments c JOIN invoice_fulfilments f ON f.invoice_id = NEW.invoice_id
                            WHERE c.id = NEW.consignment_id AND f.store_id = c.store_id AND f.mode = c.mode) THEN
                RAISE EXCEPTION 'A dispatch carries bills of its own store, delivered its way.' USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_consignment_invoices_guard BEFORE INSERT ON consignment_invoices FOR EACH ROW EXECUTE FUNCTION sb_consignment_invoice_guard();

        CREATE FUNCTION sb_invoice_fulfilment_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'A bill''s delivery is never deleted (set it to pickup instead).' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.invoice_id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.created_at_utc)
               IS DISTINCT FROM (OLD.invoice_id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.created_at_utc) THEN
                RAISE EXCEPTION 'A bill''s delivery stays with its bill.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF EXISTS (SELECT 1 FROM consignment_invoices ci JOIN consignments c ON c.id = ci.consignment_id
                        WHERE ci.invoice_id = OLD.invoice_id AND c.status = 'DISPATCHED') THEN
                RAISE EXCEPTION 'The goods on this bill have been dispatched; cancel the dispatch first.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_invoice_fulfilments_guard BEFORE UPDATE OR DELETE ON invoice_fulfilments FOR EACH ROW EXECUTE FUNCTION sb_invoice_fulfilment_guard();
        """;

    public const string DropGuards = """
        DROP TRIGGER IF EXISTS trg_invoice_fulfilments_guard ON invoice_fulfilments;
        DROP FUNCTION IF EXISTS sb_invoice_fulfilment_guard();
        DROP TRIGGER IF EXISTS trg_consignment_invoices_guard ON consignment_invoices;
        DROP FUNCTION IF EXISTS sb_consignment_invoice_guard();
        DROP TRIGGER IF EXISTS trg_consignments_guard ON consignments;
        DROP FUNCTION IF EXISTS sb_consignment_guard();
        """;
}
