-- Purchase verification: goods receipts add up, expenses are allocated exactly, and only posted receipts moved stock.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.grns') IS NULL THEN
        RAISE NOTICE 'Purchase tables not present yet; skipping purchase verification.';
        RETURN;
    END IF;

    FOREACH trigger_name IN ARRAY ARRAY[
        'trg_grns_guard', 'trg_grns_no_truncate', 'trg_grn_lines_no_update_delete', 'trg_grn_expenses_no_update_delete', 'trg_grn_allocations_no_update_delete']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 1. Every business has purchase settings.
    SELECT string_agg(b.code, ', ') INTO offending FROM businesses b LEFT JOIN purchase_settings s ON s.business_id = b.id WHERE s.business_id IS NULL;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('businesses without purchase settings: %s', offending));
    END IF;

    -- 2. Each receipt equals its lines, and its expenses total.
    SELECT string_agg(g.number, ', ') INTO offending
      FROM grns g
      LEFT JOIN (SELECT grn_id, sum(gross) gr, sum(discount) d, sum(taxable) t, sum(cgst) c, sum(sgst) s, sum(igst) i, sum(cess) ce, sum(landed_total) l
                   FROM grn_lines GROUP BY grn_id) x ON x.grn_id = g.id
      LEFT JOIN (SELECT grn_id, sum(amount) a FROM grn_expenses GROUP BY grn_id) e ON e.grn_id = g.id
     WHERE x.grn_id IS NULL
        OR (g.gross_total, g.discount_total, g.taxable_total, g.cgst_total, g.sgst_total, g.igst_total, g.cess_total, g.landed_total)
           IS DISTINCT FROM (x.gr, x.d, x.t, x.c, x.s, x.i, x.ce, x.l)
        OR g.expenses_total <> coalesce(e.a, 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('goods receipts that do not add up: %s', offending));
    END IF;

    -- 3. Every expense is allocated exactly, and each line carries exactly its allocations.
    SELECT string_agg(e.id::text, ', ') INTO offending
      FROM grn_expenses e LEFT JOIN (SELECT expense_id, sum(amount) a FROM grn_allocations GROUP BY expense_id) a ON a.expense_id = e.id
     WHERE coalesce(a.a, 0) <> e.amount;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('expenses not allocated exactly: %s', offending));
    END IF;

    SELECT string_agg(l.id::text, ', ') INTO offending
      FROM grn_lines l LEFT JOIN (SELECT line_id, sum(amount) a FROM grn_allocations GROUP BY line_id) a ON a.line_id = l.id
     WHERE coalesce(a.a, 0) <> l.expense_share;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('lines whose expense share differs from their allocations: %s', offending));
    END IF;

    -- 4. Posted receipts put exactly their quantities into stock at their landed cost; others moved nothing.
    SELECT string_agg(DISTINCT g.number, ', ') INTO offending
      FROM grns g
      JOIN (SELECT grn_id, variant_id, sum(base_quantity) q FROM grn_lines GROUP BY grn_id, variant_id) l ON l.grn_id = g.id
      LEFT JOIN (SELECT document_id, variant_id, sum(quantity) q FROM stock_ledger WHERE document_type = 'GRN' GROUP BY document_id, variant_id) s
        ON s.document_id = g.id AND s.variant_id = l.variant_id
     WHERE (g.status = 'POSTED' AND s.q IS DISTINCT FROM l.q) OR (g.status <> 'POSTED' AND s.q IS NOT NULL);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('goods receipts whose stock movements do not match their status and lines: %s', offending));
    END IF;

    SELECT string_agg(DISTINCT g.number, ', ') INTO offending
      FROM grns g JOIN grn_lines l ON l.grn_id = g.id
      JOIN stock_ledger s ON s.document_id = g.id AND s.variant_id = l.variant_id AND s.document_type = 'GRN'
     WHERE s.unit_cost <> l.landed_unit_cost AND NOT EXISTS (
           SELECT 1 FROM grn_lines o WHERE o.grn_id = g.id AND o.variant_id = l.variant_id AND o.id <> l.id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('goods receipts received at a cost other than their landed cost: %s', offending));
    END IF;

    -- 5. Numbering per store is gapless.
    SELECT string_agg(format('store %s: issued %s, receipts %s', q.store_id, q.next_number - 1, coalesce(g.n, 0)), '; ') INTO offending
      FROM document_sequences q
      LEFT JOIN (SELECT store_id, count(*) n FROM grns GROUP BY store_id) g ON g.store_id = q.store_id
     WHERE q.series = 'GRN' AND q.next_number - 1 <> coalesce(g.n, 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('goods receipt numbering has gaps: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Purchase verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Purchase verification passed (% on %).', current_database(), version();
END
$$;
