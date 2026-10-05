-- Packing verification: every bill sent by delivery has its challan with the bill's lines; quantities only go
-- billed >= picked >= checked >= packed >= out; checking is by someone other than the picker; a dispatch carries what
-- was packed, and its value is what was billed for those quantities; delivery reports add up.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.packing_challans') IS NULL THEN
        RAISE NOTICE 'Packing tables not present yet; skipping packing verification.';
        RETURN;
    END IF;

    FOREACH trigger_name IN ARRAY ARRAY['trg_packing_challans_guard', 'trg_packing_challan_lines_guard', 'trg_consignment_lines_guard', 'trg_consignments_guard',
                                        'trg_packing_events_no_update_delete']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 1. Every bill chosen for delivery has an open challan, and every challan carries exactly its bill's lines.
    SELECT string_agg(i.number, ', ') INTO offending
      FROM invoice_fulfilments f JOIN sales_invoices i ON i.id = f.invoice_id
     WHERE f.mode <> 'PICKUP' AND NOT EXISTS (SELECT 1 FROM packing_challans c WHERE c.invoice_id = f.invoice_id AND c.status = 'OPEN');
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('bills for delivery without a challan: %s', offending));
    END IF;
    SELECT string_agg(c.number, ', ') INTO offending
      FROM packing_challans c
     WHERE EXISTS (SELECT l.id, l.quantity, l.unit_code FROM sales_invoice_lines l WHERE l.invoice_id = c.invoice_id
                   EXCEPT SELECT p.invoice_line_id, p.quantity, p.unit_code FROM packing_challan_lines p WHERE p.challan_id = c.id)
        OR EXISTS (SELECT p.invoice_line_id FROM packing_challan_lines p WHERE p.challan_id = c.id
                   EXCEPT SELECT l.id FROM sales_invoice_lines l WHERE l.invoice_id = c.invoice_id)
        OR c.store_id <> (SELECT i.store_id FROM sales_invoices i WHERE i.id = c.invoice_id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('challans that do not match their bill: %s', offending));
    END IF;

    -- 2. Nothing goes out beyond what was packed (less what came back), and nothing came back that was not sent.
    SELECT string_agg(c.number || ' line ' || l.line_number, ', ') INTO offending
      FROM packing_challan_lines l JOIN packing_challans c ON c.id = l.challan_id
     WHERE (SELECT coalesce(sum(cl.quantity - coalesce(cl.returned_quantity, 0)), 0) FROM consignment_lines cl JOIN consignments k ON k.id = cl.consignment_id
             WHERE cl.challan_line_id = l.id AND k.status = 'DISPATCHED') > l.packed_quantity;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('challan lines dispatched beyond what was packed: %s', offending));
    END IF;

    -- 3. A dispatch's lines belong to its bills, and its goods value is what was billed for the quantities carried.
    SELECT string_agg(k.number, ', ') INTO offending
      FROM consignments k
     WHERE (EXISTS (SELECT 1 FROM consignment_lines cl JOIN packing_challan_lines l ON l.id = cl.challan_line_id JOIN packing_challans c ON c.id = l.challan_id
                     WHERE cl.consignment_id = k.id
                       AND NOT EXISTS (SELECT 1 FROM consignment_invoices ci WHERE ci.consignment_id = k.id AND ci.invoice_id = c.invoice_id))
            OR (EXISTS (SELECT 1 FROM consignment_lines cl WHERE cl.consignment_id = k.id)
                AND k.goods_value <> (SELECT sum(round(il.total * cl.quantity / il.quantity, 2))
                                        FROM consignment_lines cl JOIN packing_challan_lines l ON l.id = cl.challan_line_id
                                        JOIN sales_invoice_lines il ON il.id = l.invoice_line_id
                                       WHERE cl.consignment_id = k.id))
            -- A dispatch that stands carries goods; one cancelled before Stage 11b may have none.
            OR (k.status = 'DISPATCHED' AND NOT EXISTS (SELECT 1 FROM consignment_lines cl WHERE cl.consignment_id = k.id)));
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('dispatches whose lines or value do not match their bills: %s', offending));
    END IF;

    -- 4. A delivery report covers every line, and its outcome agrees with the quantities.
    SELECT string_agg(k.number, ', ') INTO offending
      FROM consignments k
     WHERE k.delivery_outcome IS NOT NULL
       AND (EXISTS (SELECT 1 FROM consignment_lines cl WHERE cl.consignment_id = k.id AND cl.delivered_quantity IS NULL)
            OR (k.delivery_outcome = 'DELIVERED') <> NOT EXISTS (SELECT 1 FROM consignment_lines cl WHERE cl.consignment_id = k.id AND cl.delivered_quantity < cl.quantity)
            OR (k.delivery_outcome = 'FAILED') <> NOT EXISTS (SELECT 1 FROM consignment_lines cl WHERE cl.consignment_id = k.id AND cl.delivered_quantity > 0));
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('delivery reports that do not add up: %s', offending));
    END IF;

    -- 5. Packing history starts with the challan.
    SELECT string_agg(c.number, ', ') INTO offending
      FROM packing_challans c
     WHERE (SELECT e.kind FROM packing_events e WHERE e.challan_id = c.id ORDER BY e.at_utc, e.id LIMIT 1) IS DISTINCT FROM 'CREATED'
       AND EXISTS (SELECT 1 FROM packing_events e WHERE e.challan_id = c.id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('challans whose history does not start with their creation: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Packing verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Packing verification passed (% on %).', current_database(), version();
END
$$;
