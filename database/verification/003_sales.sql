-- Sales verification: invoice arithmetic, numbering, payments and stock. Run through scripts/verify-database.ps1.
-- Runs as the superuser (row-level security does not apply), so it checks every tenant.
-- Raises an exception (non-zero exit) when any check fails.
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.sales_invoices') IS NULL THEN
        RAISE NOTICE 'Sales tables not present yet; skipping sales verification.';
        RETURN;
    END IF;

    -- 1. Issued invoices, lines and payments are append-only; approvals and counter codes are guarded.
    FOREACH trigger_name IN ARRAY ARRAY[
        'trg_sales_invoices_no_update_delete', 'trg_sales_invoices_no_truncate',
        'trg_sales_invoice_lines_no_update_delete', 'trg_sales_invoice_lines_no_truncate',
        'trg_sales_invoice_payments_no_update_delete', 'trg_sales_invoice_payments_no_truncate',
        'trg_supervisor_approvals_guard', 'trg_counters_guard']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 2. Every invoice header equals the sum of its lines.
    SELECT string_agg(i.number, ', ') INTO offending
      FROM sales_invoices i
      LEFT JOIN (SELECT invoice_id, sum(gross) g, sum(item_discount + bill_discount) d, sum(taxable) t, sum(cgst) c, sum(sgst) s,
                        sum(igst) ig, sum(cess) ce, count(*) n
                   FROM sales_invoice_lines GROUP BY invoice_id) l ON l.invoice_id = i.id
     WHERE l.n IS NULL OR (i.gross_total, i.discount_total, i.taxable_total, i.cgst_total, i.sgst_total, i.igst_total, i.cess_total)
           IS DISTINCT FROM (l.g, l.d, l.t, l.c, l.s, l.ig, l.ce);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('invoice totals differ from their lines: %s', offending));
    END IF;

    -- 3. Payments add up to what was paid, and line numbers run 1..n.
    SELECT string_agg(i.number, ', ') INTO offending
      FROM sales_invoices i
      LEFT JOIN (SELECT invoice_id, sum(amount) total, sum(amount) FILTER (WHERE method = 'CASH') cash FROM sales_invoice_payments GROUP BY invoice_id) p
        ON p.invoice_id = i.id
     WHERE p.total IS DISTINCT FROM i.paid_total OR i.change_due > coalesce(p.cash, 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('invoice payments do not add up (or change exceeds cash): %s', offending));
    END IF;

    SELECT string_agg(i.number, ', ') INTO offending
      FROM sales_invoices i
      JOIN (SELECT invoice_id, count(*) n, max(line_number) m, min(line_number) f FROM sales_invoice_lines GROUP BY invoice_id) l ON l.invoice_id = i.id
     WHERE l.n <> l.m OR l.f <> 1;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('invoice line numbers have gaps: %s', offending));
    END IF;

    -- 4. Each invoice series (a counter's code, plus a letter after a tax-registration change) is gapless:
    --    numbers 1..n issued and n equals the series counter. Series counters with no invoices must be unused.
    SELECT string_agg(format('series %s: issued %s, invoices %s, highest %s', x.series, x.issued, x.n, x.m), '; ') INTO offending
      FROM (
        SELECT coalesce(q.series, 'INV-' || i.number_prefix) AS series, coalesce(q.next_number, 1) - 1 AS issued, coalesce(i.n, 0) AS n, coalesce(i.m, 0) AS m
          FROM (SELECT * FROM document_sequences WHERE series LIKE 'INV-%') q
          FULL JOIN (SELECT store_id, number_prefix, count(*) n, max(sequence_number) m FROM sales_invoices GROUP BY store_id, number_prefix) i
            ON i.store_id = q.store_id AND q.series = 'INV-' || i.number_prefix
      ) x
     WHERE x.issued <> x.n OR x.n <> x.m;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('invoice numbering has gaps: %s', offending));
    END IF;

    SELECT string_agg(i.number, ', ') INTO offending
      FROM sales_invoices i JOIN counters c ON c.id = i.counter_id
     WHERE left(i.number_prefix, length(c.code)) <> c.code OR length(i.number_prefix) > length(c.code) + 1 OR i.store_id <> c.store_id;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('invoice numbers that do not belong to their counter: %s', offending));
    END IF;

    -- 5. Stock left for exactly what was sold, and the cost on each line is the stock ledger's cost.
    SELECT string_agg(DISTINCT i.number, ', ') INTO offending
      FROM sales_invoices i
      JOIN (SELECT invoice_id, variant_id, sum(base_quantity) q, sum(cost_of_goods) c FROM sales_invoice_lines GROUP BY invoice_id, variant_id) l
        ON l.invoice_id = i.id
      LEFT JOIN (SELECT document_id, variant_id, -sum(quantity) q, -sum(value) c FROM stock_ledger
                  WHERE document_type = 'SALES_INVOICE' GROUP BY document_id, variant_id) s
        ON s.document_id = i.id AND s.variant_id = l.variant_id
     WHERE s.q IS DISTINCT FROM l.q OR s.c IS DISTINCT FROM l.c;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('invoices whose stock movements differ from their lines: %s', offending));
    END IF;

    SELECT string_agg(DISTINCT s.document_id::text, ', ') INTO offending
      FROM stock_ledger s LEFT JOIN sales_invoices i ON i.id = s.document_id
     WHERE s.document_type = 'SALES_INVOICE' AND (i.id IS NULL OR s.movement_type <> 'SALE' OR s.store_id <> i.store_id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('sale stock movements without their invoice: %s', offending));
    END IF;

    -- 6. Every used supervisor approval belongs to an invoice of its own counter.
    SELECT string_agg(a.id::text, ', ') INTO offending
      FROM supervisor_approvals a LEFT JOIN sales_invoices i ON i.id = a.used_invoice_id
     WHERE a.used_invoice_id IS NOT NULL AND (i.id IS NULL OR i.counter_id <> a.counter_id OR i.cashier_user_id <> a.requested_by_user_id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('supervisor approvals used outside their counter or cashier: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Sales verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Sales verification passed (% on %).', current_database(), version();
END
$$;
