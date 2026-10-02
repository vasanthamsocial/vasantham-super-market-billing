-- Credit sale verification: what is on account on invoices and credit notes, and every debtor receipt, is in the
-- debtor's ledger exactly once and for the right amount.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.debtor_receipts') IS NULL THEN
        RAISE NOTICE 'Credit sale tables not present yet; skipping credit sale verification.';
        RETURN;
    END IF;

    FOREACH trigger_name IN ARRAY ARRAY['trg_debtor_receipts_open_shift', 'trg_debtor_receipts_no_update_delete']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 1. An invoice with an amount on account names its debtor and due date, and is owed exactly once for that amount; others are not owed.
    SELECT string_agg(i.number, ', ') INTO offending
      FROM sales_invoices i
      LEFT JOIN (SELECT invoice_id, sum(amount) a FROM sales_invoice_payments WHERE method = 'ON_ACCOUNT' GROUP BY invoice_id) p ON p.invoice_id = i.id
      LEFT JOIN (SELECT document_id, count(*) n, sum(amount) a, min(debtor_id::text) d FROM debtor_ledger WHERE entry_type = 'INVOICE' GROUP BY document_id) l
        ON l.document_id = i.id
     WHERE (p.a IS NOT NULL AND (i.debtor_id IS NULL OR i.due_date IS NULL OR l.n IS DISTINCT FROM 1 OR l.a <> p.a OR l.d <> i.debtor_id::text))
        OR (p.a IS NULL AND (l.n IS NOT NULL OR i.due_date IS NOT NULL));
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('invoices whose amount on account does not match the debtor ledger: %s', offending));
    END IF;

    -- 2. A credit note refunded to an account is taken off the original invoice's debtor exactly once.
    SELECT string_agg(r.number, ', ') INTO offending
      FROM sales_returns r
      JOIN sales_invoices i ON i.id = r.original_invoice_id
      LEFT JOIN (SELECT return_id, sum(amount) a FROM sales_return_refunds WHERE method = 'ON_ACCOUNT' GROUP BY return_id) f ON f.return_id = r.id
      LEFT JOIN (SELECT document_id, count(*) n, sum(amount) a, min(debtor_id::text) d FROM debtor_ledger WHERE entry_type = 'CREDIT_NOTE' GROUP BY document_id) l
        ON l.document_id = r.id
     WHERE (f.a IS NOT NULL AND (i.debtor_id IS NULL OR l.n IS DISTINCT FROM 1 OR l.a <> -f.a OR l.d <> i.debtor_id::text))
        OR (f.a IS NULL AND l.n IS NOT NULL);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('credit notes whose refund to an account does not match the debtor ledger: %s', offending));
    END IF;

    -- 3. Every receipt is in its debtor's ledger exactly once, for its amount.
    SELECT string_agg(r.number, ', ') INTO offending
      FROM debtor_receipts r
      LEFT JOIN (SELECT document_id, count(*) n, sum(amount) a, min(debtor_id::text) d FROM debtor_ledger WHERE entry_type = 'RECEIPT' GROUP BY document_id) l
        ON l.document_id = r.id
     WHERE l.n IS DISTINCT FROM 1 OR l.a <> -r.amount OR l.d <> r.debtor_id::text;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('debtor receipts not in the ledger exactly once: %s', offending));
    END IF;

    -- 4. Receipt numbering per store is gapless.
    SELECT string_agg(format('store %s: issued %s, receipts %s', q.store_id, q.next_number - 1, coalesce(r.n, 0)), '; ') INTO offending
      FROM document_sequences q
      LEFT JOIN (SELECT store_id, count(*) n FROM debtor_receipts GROUP BY store_id) r ON r.store_id = q.store_id
     WHERE q.series = 'RCT' AND q.next_number - 1 <> coalesce(r.n, 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('debtor receipt numbering has gaps: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Credit sale verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Credit sale verification passed (% on %).', current_database(), version();
END
$$;
