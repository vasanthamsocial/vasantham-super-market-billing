-- Purchase order and attachment verification: receipts never exceed their orders, files are intact, and receipts that
-- updated selling prices produced those prices.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.purchase_orders') IS NULL THEN
        RAISE NOTICE 'Purchase order tables not present yet; skipping purchase order verification.';
        RETURN;
    END IF;

    FOREACH trigger_name IN ARRAY ARRAY[
        'trg_purchase_orders_guard', 'trg_purchase_orders_no_truncate', 'trg_purchase_order_lines_no_update_delete', 'trg_attachments_no_update_delete']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    IF NOT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'sb_grn_guard' AND prosrc LIKE '%purchase_order_id%') THEN
        failures := array_append(failures, 'the goods-receipt guard does not protect purchase_order_id');
    END IF;

    -- 1. Receipts against an order are for its store and supplier.
    SELECT string_agg(g.number, ', ') INTO offending
      FROM grns g JOIN purchase_orders o ON o.id = g.purchase_order_id
     WHERE (o.store_id, o.supplier_id, o.business_id) IS DISTINCT FROM (g.store_id, g.supplier_id, g.business_id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('receipts against another store''s or supplier''s order: %s', offending));
    END IF;

    -- 2. Receipts that were not rejected bring in only what was ordered, and no more (paid quantity).
    SELECT string_agg(format('%s (%s)', o.number, r.variant_unit_id), ', ') INTO offending
      FROM (SELECT g.purchase_order_id, l.variant_unit_id, sum(l.quantity) q
              FROM grns g JOIN grn_lines l ON l.grn_id = g.id
             WHERE g.purchase_order_id IS NOT NULL AND g.status <> 'REJECTED'
             GROUP BY g.purchase_order_id, l.variant_unit_id) r
      JOIN purchase_orders o ON o.id = r.purchase_order_id
      LEFT JOIN purchase_order_lines pl ON pl.purchase_order_id = r.purchase_order_id AND pl.variant_unit_id = r.variant_unit_id
     WHERE pl.id IS NULL OR r.q > pl.quantity;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('orders received beyond what was ordered: %s', offending));
    END IF;

    -- 3. A cancelled order has no receipts; every order has lines.
    SELECT string_agg(o.number, ', ') INTO offending
      FROM purchase_orders o
     WHERE (o.status = 'CANCELLED' AND EXISTS (SELECT 1 FROM grns g WHERE g.purchase_order_id = o.id AND g.status <> 'REJECTED'))
        OR NOT EXISTS (SELECT 1 FROM purchase_order_lines l WHERE l.purchase_order_id = o.id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('orders cancelled after receipt, or without lines: %s', offending));
    END IF;

    -- 4. Order numbering per store is gapless.
    SELECT string_agg(format('store %s: issued %s, orders %s', q.store_id, q.next_number - 1, coalesce(o.n, 0)), '; ') INTO offending
      FROM document_sequences q
      LEFT JOIN (SELECT store_id, count(*) n FROM purchase_orders GROUP BY store_id) o ON o.store_id = q.store_id
     WHERE q.series = 'PO' AND q.next_number - 1 <> coalesce(o.n, 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('purchase order numbering has gaps: %s', offending));
    END IF;

    -- 5. Attached files are intact (hash and size), of the type their content shows, and belong to a receipt of the same business.
    SELECT string_agg(a.id::text, ', ') INTO offending
      FROM attachments a
      LEFT JOIN grns g ON g.id = a.owner_id AND g.business_id = a.business_id
     WHERE a.sha256 <> encode(sha256(a.content), 'hex')
        OR a.size <> octet_length(a.content)
        OR g.id IS NULL
        OR NOT CASE a.content_type
               WHEN 'application/pdf' THEN substring(a.content FROM 1 FOR 5) = '\x255044462d'::bytea
               WHEN 'image/jpeg' THEN substring(a.content FROM 1 FOR 3) = '\xffd8ff'::bytea
               WHEN 'image/png' THEN substring(a.content FROM 1 FOR 8) = '\x89504e470d0a1a0a'::bytea
               ELSE false END;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('attachments damaged, mistyped or without their receipt: %s', offending));
    END IF;

    -- 6. A posted receipt that updated a selling price produced that retail price.
    SELECT string_agg(DISTINCT g.number, ', ') INTO offending
      FROM grns g JOIN grn_lines l ON l.grn_id = g.id
     WHERE g.status = 'POSTED' AND l.update_selling_price
       AND NOT EXISTS (SELECT 1 FROM price_rules r
                        WHERE r.variant_unit_id = l.variant_unit_id AND r.price = l.selling_price AND r.rate_type = 'STANDARD'
                          AND r.note = 'From goods receipt ' || g.number);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('receipts whose selling-price update is missing: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Purchase order verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Purchase order verification passed (% on %).', current_database(), version();
END
$$;
