-- Returns verification: credit notes against their invoices, refunds, store credit and stock. Run through
-- scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply), so it checks every tenant.
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.sales_returns') IS NULL THEN
        RAISE NOTICE 'Return tables not present yet; skipping returns verification.';
        RETURN;
    END IF;

    -- 1. Credit notes, their lines, refunds and uses of store credit are append-only.
    FOREACH trigger_name IN ARRAY ARRAY[
        'trg_sales_returns_no_update_delete', 'trg_sales_return_lines_no_update_delete',
        'trg_sales_return_refunds_no_update_delete', 'trg_credit_note_redemptions_no_update_delete']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 2. Each credit note equals the sum of its lines, and its refunds add up to it.
    SELECT string_agg(r.number, ', ') INTO offending
      FROM sales_returns r
      LEFT JOIN (SELECT return_id, sum(taxable) t, sum(cgst) c, sum(sgst) s, sum(igst) i, sum(cess) ce, count(*) n FROM sales_return_lines GROUP BY return_id) l
        ON l.return_id = r.id
      LEFT JOIN (SELECT return_id, sum(amount) total, sum(amount) FILTER (WHERE method = 'STORE_CREDIT') credit FROM sales_return_refunds GROUP BY return_id) f
        ON f.return_id = r.id
     WHERE l.n IS NULL
        OR (r.taxable_total, r.cgst_total, r.sgst_total, r.igst_total, r.cess_total) IS DISTINCT FROM (l.t, l.c, l.s, l.i, l.ce)
        OR f.total IS DISTINCT FROM r.grand_total
        OR coalesce(f.credit, 0) <> r.store_credit;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('credit notes that do not add up (lines or refunds): %s', offending));
    END IF;

    -- 3. Nothing is returned twice: per invoice line, returned quantity never exceeds what was sold, and a fully
    --    returned line has reversed exactly its original amounts.
    SELECT string_agg(DISTINCT i.number, ', ') INTO offending
      FROM sales_invoice_lines il
      JOIN sales_invoices i ON i.id = il.invoice_id
      JOIN (SELECT original_line_id, sum(quantity) q, sum(taxable) t, sum(cgst) c, sum(igst) ig, sum(cess) ce, sum(total) tot
              FROM sales_return_lines GROUP BY original_line_id) rl ON rl.original_line_id = il.id
     WHERE rl.q > il.quantity
        OR (rl.q = il.quantity AND (rl.t, rl.c, rl.ig, rl.ce, rl.tot) IS DISTINCT FROM (il.taxable, il.cgst, il.igst, il.cess, il.total));
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('invoices returned beyond what was sold (or not exactly reversed): %s', offending));
    END IF;

    SELECT string_agg(r.number, ', ') INTO offending
      FROM sales_returns r
      JOIN sales_invoices i ON i.id = r.original_invoice_id
      JOIN sales_return_lines l ON l.return_id = r.id
      JOIN sales_invoice_lines il ON il.id = l.original_line_id
     WHERE il.invoice_id <> r.original_invoice_id OR i.store_id <> r.store_id OR r.original_invoice_number <> i.number;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('credit notes whose lines or store do not match their invoice: %s', offending));
    END IF;

    -- 4. Credit note numbers are gapless per series.
    SELECT string_agg(format('series %s: issued %s, credit notes %s, highest %s', x.series, x.issued, x.n, x.m), '; ') INTO offending
      FROM (
        SELECT coalesce(q.series, 'CN-' || r.number_prefix) AS series, coalesce(q.next_number, 1) - 1 AS issued, coalesce(r.n, 0) AS n, coalesce(r.m, 0) AS m
          FROM (SELECT * FROM document_sequences WHERE series LIKE 'CN-%') q
          FULL JOIN (SELECT store_id, number_prefix, count(*) n, max(sequence_number) m FROM sales_returns GROUP BY store_id, number_prefix) r
            ON r.store_id = q.store_id AND q.series = 'CN-' || r.number_prefix
      ) x
     WHERE x.issued <> x.n OR x.n <> x.m;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('credit note numbering has gaps: %s', offending));
    END IF;

    -- 5. Restocked goods came back into stock exactly (quantity and cost); damaged goods did not.
    SELECT string_agg(DISTINCT r.number, ', ') INTO offending
      FROM sales_returns r
      JOIN (SELECT return_id, variant_id, sum(base_quantity) FILTER (WHERE restocked) q, sum(cost_returned) c FROM sales_return_lines GROUP BY return_id, variant_id) l
        ON l.return_id = r.id
      LEFT JOIN (SELECT document_id, variant_id, sum(quantity) q, sum(value) c FROM stock_ledger WHERE document_type = 'SALES_RETURN' GROUP BY document_id, variant_id) s
        ON s.document_id = r.id AND s.variant_id = l.variant_id
     WHERE coalesce(s.q, 0) <> coalesce(l.q, 0) OR coalesce(s.c, 0) <> coalesce(l.c, 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('credit notes whose stock movements differ from their lines: %s', offending));
    END IF;

    -- 6. Store credit is never spent beyond what the credit note kept, and every credit-note payment was redeemed.
    SELECT string_agg(r.number, ', ') INTO offending
      FROM sales_returns r JOIN (SELECT return_id, sum(amount) used FROM credit_note_redemptions GROUP BY return_id) u ON u.return_id = r.id
     WHERE u.used > r.store_credit;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('credit notes overspent: %s', offending));
    END IF;

    SELECT string_agg(i.number, ', ') INTO offending
      FROM sales_invoices i
      LEFT JOIN (SELECT invoice_id, sum(amount) a FROM sales_invoice_payments WHERE method = 'CREDIT_NOTE' GROUP BY invoice_id) p ON p.invoice_id = i.id
      LEFT JOIN (SELECT invoice_id, sum(amount) a FROM credit_note_redemptions GROUP BY invoice_id) x ON x.invoice_id = i.id
     WHERE coalesce(p.a, 0) <> coalesce(x.a, 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('credit-note payments without matching redemptions: %s', offending));
    END IF;

    -- 7. Every used return approval belongs to a credit note of its own counter and cashier.
    SELECT string_agg(a.id::text, ', ') INTO offending
      FROM supervisor_approvals a LEFT JOIN sales_returns r ON r.id = a.used_document_id
     WHERE a.used_document_id IS NOT NULL AND a.kind = 'RETURN'
       AND (r.id IS NULL OR r.counter_id <> a.counter_id OR r.cashier_user_id <> a.requested_by_user_id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('return approvals used outside their counter or cashier: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Returns verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Returns verification passed (% on %).', current_database(), version();
END
$$;
