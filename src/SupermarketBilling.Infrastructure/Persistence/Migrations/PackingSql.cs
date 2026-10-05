namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class PackingSql
{
    public static readonly string[] Tables = ["packing_challans", "packing_challan_lines", "packing_events", "consignment_lines"];

    /// <summary>
    /// Bills chosen for delivery before challans existed get one (nothing picked yet), numbered on from each store's
    /// series. A bill already dispatched without a challan cannot be given one honestly (who picked and checked it is
    /// unknown), so the migration stops; none exist before release.
    /// </summary>
    public const string Backfill = """
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM consignments WHERE status = 'DISPATCHED') THEN
                RAISE EXCEPTION 'Dispatches recorded before packing challans exist; cancel them (they are re-recorded after packing) before upgrading.';
            END IF;
        END
        $$;

        CREATE TEMP TABLE sb_new_challans ON COMMIT DROP AS
        SELECT gen_random_uuid() AS id, f.tenant_id, f.business_id, f.store_id, f.invoice_id, s.code AS store_code,
               coalesce(d.trade_name, d.legal_name, i.buyer_name, 'Walk-in customer') AS party_name,
               row_number() OVER (PARTITION BY f.store_id ORDER BY f.created_at_utc, f.invoice_id)
                 + coalesce((SELECT q.next_number - 1 FROM document_sequences q WHERE q.store_id = f.store_id AND q.series = 'PCH'), 0) AS seq
          FROM invoice_fulfilments f
          JOIN sales_invoices i ON i.id = f.invoice_id
          JOIN stores s ON s.id = f.store_id
          LEFT JOIN debtors d ON d.id = i.debtor_id
         WHERE f.mode <> 'PICKUP';

        INSERT INTO packing_challans (id, tenant_id, business_id, store_id, invoice_id, number, party_name, status, package_count, created_at_utc)
        SELECT id, tenant_id, business_id, store_id, invoice_id, store_code || '/PCH/' || lpad(seq::text, 6, '0'), party_name, 'OPEN', 0, now()
          FROM sb_new_challans;

        INSERT INTO packing_challan_lines (id, tenant_id, business_id, challan_id, invoice_line_id, line_number, item_name, variant_name, unit_code, quantity,
                                           free_quantity, packed_quantity)
        SELECT gen_random_uuid(), c.tenant_id, c.business_id, c.id, l.id, l.line_number, coalesce(p.name, l.description),
               CASE WHEN p.name IS NULL OR p.name = l.description THEN NULL ELSE l.description END, l.unit_code, l.quantity, 0, 0
          FROM sb_new_challans c
          JOIN sales_invoice_lines l ON l.invoice_id = c.invoice_id
          LEFT JOIN products p ON p.id = l.product_id;

        INSERT INTO document_sequences (id, tenant_id, business_id, store_id, series, next_number)
        SELECT gen_random_uuid(), tenant_id, business_id, store_id, 'PCH', max(seq) + 1 FROM sb_new_challans GROUP BY tenant_id, business_id, store_id
        ON CONFLICT (store_id, series) DO UPDATE SET next_number = EXCLUDED.next_number;
        """;

    /// <summary>
    /// What was picked, checked and packed is set once and only grows (packing); identities never change; nothing is
    /// deleted. A dispatch line carries no more than is packed and not already out (also enforced by the service under a
    /// lock on the challans). A dispatch changes only by being cancelled (before delivery is reported), by its delivery
    /// report, and by the goods coming back, each once.
    /// </summary>
    public const string Guards = """
        CREATE FUNCTION sb_packing_challan_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Packing challans cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.invoice_id, NEW.number, NEW.party_name, NEW.created_at_utc)
                 IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.invoice_id, OLD.number, OLD.party_name, OLD.created_at_utc)
               OR (OLD.picked_by_user_id IS NOT NULL AND (NEW.picked_by_user_id, NEW.picked_at_utc) IS DISTINCT FROM (OLD.picked_by_user_id, OLD.picked_at_utc))
               OR (OLD.checked_by_user_id IS NOT NULL AND (NEW.checked_by_user_id, NEW.checked_at_utc) IS DISTINCT FROM (OLD.checked_by_user_id, OLD.checked_at_utc))
               OR (OLD.packed_by_user_id IS NOT NULL AND NEW.packed_by_user_id IS DISTINCT FROM OLD.packed_by_user_id)
               OR NEW.package_count < OLD.package_count
               OR (OLD.status = 'CANCELLED' AND NEW IS DISTINCT FROM OLD) THEN
                RAISE EXCEPTION 'A packing challan keeps what was recorded.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.status = 'OPEN' AND NEW.status = 'CANCELLED' AND EXISTS (
                SELECT 1 FROM consignment_lines cl JOIN consignments c ON c.id = cl.consignment_id
                  JOIN packing_challan_lines l ON l.id = cl.challan_line_id
                 WHERE l.challan_id = OLD.id AND c.status = 'DISPATCHED') THEN
                RAISE EXCEPTION 'Goods on this challan have been dispatched.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_packing_challans_guard BEFORE UPDATE OR DELETE ON packing_challans FOR EACH ROW EXECUTE FUNCTION sb_packing_challan_guard();

        CREATE FUNCTION sb_packing_challan_line_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Challan lines cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.challan_id, NEW.invoice_line_id, NEW.line_number, NEW.item_name, NEW.variant_name, NEW.unit_code,
                NEW.quantity, NEW.free_quantity)
                 IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.challan_id, OLD.invoice_line_id, OLD.line_number, OLD.item_name, OLD.variant_name,
                OLD.unit_code, OLD.quantity, OLD.free_quantity)
               OR (OLD.picked_quantity IS NOT NULL AND NEW.picked_quantity IS DISTINCT FROM OLD.picked_quantity)
               OR (OLD.checked_quantity IS NOT NULL AND NEW.checked_quantity IS DISTINCT FROM OLD.checked_quantity)
               OR NEW.packed_quantity < OLD.packed_quantity THEN
                RAISE EXCEPTION 'A challan line keeps what was picked, checked and packed.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_packing_challan_lines_guard BEFORE UPDATE OR DELETE ON packing_challan_lines FOR EACH ROW EXECUTE FUNCTION sb_packing_challan_line_guard();

        CREATE FUNCTION sb_consignment_line_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE
            v_out numeric;
            v_packed numeric;
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Dispatch lines cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF TG_OP = 'UPDATE' THEN
                IF (NEW.consignment_id, NEW.challan_line_id, NEW.tenant_id, NEW.business_id, NEW.quantity)
                     IS DISTINCT FROM (OLD.consignment_id, OLD.challan_line_id, OLD.tenant_id, OLD.business_id, OLD.quantity)
                   OR (OLD.delivered_quantity IS NOT NULL AND NEW.delivered_quantity IS DISTINCT FROM OLD.delivered_quantity)
                   OR (OLD.returned_quantity IS NOT NULL AND NEW.returned_quantity IS DISTINCT FROM OLD.returned_quantity) THEN
                    RAISE EXCEPTION 'A dispatch line keeps what was sent, delivered and returned.' USING ERRCODE = 'restrict_violation';
                END IF;
                RETURN NEW;
            END IF;
            IF NEW.delivered_quantity IS NOT NULL OR NEW.returned_quantity IS NOT NULL THEN
                RAISE EXCEPTION 'Delivery is reported after dispatch.' USING ERRCODE = 'check_violation';
            END IF;
            IF NOT EXISTS (SELECT 1 FROM consignments c JOIN packing_challan_lines l ON l.id = NEW.challan_line_id
                             JOIN packing_challans ch ON ch.id = l.challan_id
                            WHERE c.id = NEW.consignment_id AND c.status = 'DISPATCHED' AND ch.status = 'OPEN' AND ch.store_id = c.store_id) THEN
                RAISE EXCEPTION 'A dispatch carries goods of open challans of its own store.' USING ERRCODE = 'check_violation';
            END IF;
            SELECT coalesce(sum(cl.quantity - coalesce(cl.returned_quantity, 0)), 0) INTO v_out
              FROM consignment_lines cl JOIN consignments c ON c.id = cl.consignment_id
             WHERE cl.challan_line_id = NEW.challan_line_id AND c.status = 'DISPATCHED';
            SELECT packed_quantity INTO v_packed FROM packing_challan_lines WHERE id = NEW.challan_line_id;
            IF v_out + NEW.quantity > v_packed THEN
                RAISE EXCEPTION 'More would be dispatched than was packed.' USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_consignment_lines_guard BEFORE INSERT OR UPDATE OR DELETE ON consignment_lines FOR EACH ROW EXECUTE FUNCTION sb_consignment_line_guard();

        DROP TRIGGER trg_consignments_guard ON consignments;
        DROP FUNCTION sb_consignment_guard();
        CREATE FUNCTION sb_consignment_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE
            v_cancel text[] := ARRAY['status', 'cancel_reason', 'cancelled_by_user_id', 'cancelled_at_utc'];
            v_delivery text[] := ARRAY['delivery_outcome', 'delivered_on', 'delivery_note', 'delivery_reported_by_user_id', 'delivery_reported_at_utc'];
            v_return text[] := ARRAY['return_recorded_by_user_id', 'return_recorded_at_utc'];
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Dispatches cannot be deleted; cancel them instead.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (to_jsonb(NEW) - v_cancel - v_delivery - v_return) IS DISTINCT FROM (to_jsonb(OLD) - v_cancel - v_delivery - v_return)
               OR (OLD.status = 'CANCELLED' AND NEW IS DISTINCT FROM OLD)
               OR (NEW.status = 'CANCELLED' AND (OLD.delivery_outcome IS NOT NULL OR (to_jsonb(NEW) - v_cancel) IS DISTINCT FROM (to_jsonb(OLD) - v_cancel)))
               OR (OLD.delivery_outcome IS NOT NULL AND (NEW.delivery_outcome, NEW.delivered_on, NEW.delivery_note, NEW.delivery_reported_by_user_id,
                     NEW.delivery_reported_at_utc) IS DISTINCT FROM (OLD.delivery_outcome, OLD.delivered_on, OLD.delivery_note, OLD.delivery_reported_by_user_id,
                     OLD.delivery_reported_at_utc))
               OR (OLD.return_recorded_at_utc IS NOT NULL AND (NEW.return_recorded_at_utc, NEW.return_recorded_by_user_id)
                     IS DISTINCT FROM (OLD.return_recorded_at_utc, OLD.return_recorded_by_user_id))
               OR (NEW.return_recorded_at_utc IS NOT NULL AND NEW.delivery_outcome IS NULL) THEN
                RAISE EXCEPTION 'A dispatch changes only by being cancelled, by its delivery report and by goods coming back, once each.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_consignments_guard BEFORE UPDATE OR DELETE ON consignments FOR EACH ROW EXECUTE FUNCTION sb_consignment_guard();

        -- A bill can now go in several dispatches (in parts); what each carries is checked on its lines.
        CREATE OR REPLACE FUNCTION sb_consignment_invoice_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM consignments c WHERE c.id = NEW.consignment_id AND c.status = 'DISPATCHED') THEN
                RAISE EXCEPTION 'Bills are added to a dispatch only when it is recorded.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF NOT EXISTS (SELECT 1 FROM consignments c JOIN invoice_fulfilments f ON f.invoice_id = NEW.invoice_id
                            WHERE c.id = NEW.consignment_id AND f.store_id = c.store_id AND f.mode = c.mode) THEN
                RAISE EXCEPTION 'A dispatch carries bills of its own store, delivered its way.' USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        """;

    public const string DropGuards = """
        DROP TRIGGER IF EXISTS trg_consignment_lines_guard ON consignment_lines;
        DROP FUNCTION IF EXISTS sb_consignment_line_guard();
        DROP TRIGGER IF EXISTS trg_packing_challan_lines_guard ON packing_challan_lines;
        DROP FUNCTION IF EXISTS sb_packing_challan_line_guard();
        DROP TRIGGER IF EXISTS trg_packing_challans_guard ON packing_challans;
        DROP FUNCTION IF EXISTS sb_packing_challan_guard();
        DROP TRIGGER IF EXISTS trg_consignments_guard ON consignments;
        DROP FUNCTION IF EXISTS sb_consignment_guard();
        """;
}
