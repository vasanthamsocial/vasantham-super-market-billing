-- Purchase return verification: returns never exceed their receipts, add up, took their stock out, and are deducted
-- from what is owed to the supplier exactly once.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.purchase_returns') IS NULL THEN
        RAISE NOTICE 'Purchase return tables not present yet; skipping purchase return verification.';
        RETURN;
    END IF;

    FOREACH trigger_name IN ARRAY ARRAY['trg_purchase_return_lines_guard', 'trg_purchase_returns_no_update_delete', 'trg_purchase_return_lines_no_update_delete']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 1. Returns are against posted receipts, and never more than a receipt line received (quantity or any amount).
    SELECT string_agg(DISTINCT g.number, ', ') INTO offending
      FROM grn_lines gl JOIN grns g ON g.id = gl.grn_id
      JOIN (SELECT l.grn_line_id, sum(l.quantity) q, sum(l.taxable) t, sum(l.cgst) c, sum(l.sgst) s, sum(l.igst) i, sum(l.cess) ce
              FROM purchase_return_lines l GROUP BY l.grn_line_id) r ON r.grn_line_id = gl.id
     WHERE g.status <> 'POSTED' OR r.q > gl.quantity + gl.free_quantity
        OR r.t > gl.taxable OR r.c > gl.cgst OR r.s > gl.sgst OR r.i > gl.igst OR r.ce > gl.cess;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('receipts returned beyond what they received: %s', offending));
    END IF;

    -- 2. Each return equals its lines (plus round-off), and its lines are of its receipt.
    SELECT string_agg(r.number, ', ') INTO offending
      FROM purchase_returns r
      LEFT JOIN (SELECT purchase_return_id, sum(taxable) t, sum(cgst) c, sum(sgst) s, sum(igst) i, sum(cess) ce, sum(total) tot, sum(stock_value) v
                   FROM purchase_return_lines GROUP BY purchase_return_id) l ON l.purchase_return_id = r.id
     WHERE l.purchase_return_id IS NULL
        OR (r.taxable, r.cgst, r.sgst, r.igst, r.cess, r.stock_value) IS DISTINCT FROM (l.t, l.c, l.s, l.i, l.ce, l.v)
        OR r.total <> l.tot + r.round_off
        OR EXISTS (SELECT 1 FROM purchase_return_lines x JOIN grn_lines gl ON gl.id = x.grn_line_id
                    WHERE x.purchase_return_id = r.id AND gl.grn_id <> r.grn_id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('purchase returns that do not add up: %s', offending));
    END IF;

    -- 3. Each return took exactly its quantities out of stock.
    SELECT string_agg(DISTINCT r.number, ', ') INTO offending
      FROM purchase_returns r
      JOIN (SELECT purchase_return_id, variant_id, sum(base_quantity) q FROM purchase_return_lines GROUP BY purchase_return_id, variant_id) l
        ON l.purchase_return_id = r.id
      LEFT JOIN (SELECT document_id, variant_id, sum(quantity) q FROM stock_ledger
                  WHERE document_type = 'PURCHASE_RETURN' AND movement_type = 'PURCHASE_RETURN' GROUP BY document_id, variant_id) s
        ON s.document_id = r.id AND s.variant_id = l.variant_id
     WHERE s.q IS DISTINCT FROM -l.q;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('purchase returns whose stock movements do not match: %s', offending));
    END IF;

    -- 4. Each return is deducted from what is owed to its supplier exactly once.
    SELECT string_agg(r.number, ', ') INTO offending
      FROM purchase_returns r
      LEFT JOIN (SELECT document_id, count(*) n, sum(amount) a, min(supplier_id::text) s FROM supplier_ledger WHERE entry_type = 'DEBIT_NOTE' GROUP BY document_id) e
        ON e.document_id = r.id
     WHERE (r.total > 0 AND (e.n IS DISTINCT FROM 1 OR e.a <> -r.total OR e.s <> r.supplier_id::text)) OR (r.total = 0 AND e.n IS NOT NULL);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('purchase returns not in the supplier ledger exactly once: %s', offending));
    END IF;

    -- 5. Debit note numbering per store is gapless.
    SELECT string_agg(format('store %s: issued %s, returns %s', q.store_id, q.next_number - 1, coalesce(r.n, 0)), '; ') INTO offending
      FROM document_sequences q
      LEFT JOIN (SELECT store_id, count(*) n FROM purchase_returns GROUP BY store_id) r ON r.store_id = q.store_id
     WHERE q.series = 'DN' AND q.next_number - 1 <> coalesce(r.n, 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('debit note numbering has gaps: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Purchase return verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Purchase return verification passed (% on %).', current_database(), version();
END
$$;
