-- Retention verification (spec section 22, D-044): archive receipts are kept as received, and each is for a package
-- this server made of that business's locked month, with the same checksum. A removed month was removed against its
-- receipt, holds none of the records removal takes, and its stock movements are carried-forward totals of that removal.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
BEGIN
    IF to_regclass('public.month_removal_receipts') IS NULL THEN
        RAISE NOTICE 'Retention tables not present yet; skipping retention verification.';
        RETURN;
    END IF;

    -- 1. Receipts and removals cannot be changed or removed, and the removed tables' guards are back on.
    IF NOT EXISTS (SELECT 1 FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
                    WHERE c.relname = 'month_removal_receipts' AND NOT t.tgisinternal AND t.tgenabled = 'O') THEN
        failures := array_append(failures, 'month_removal_receipts is not append-only');
    END IF;
    IF to_regclass('public.month_removals') IS NOT NULL THEN
        SELECT string_agg(DISTINCT c.relname, ', ') INTO offending
          FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
         WHERE NOT t.tgisinternal AND t.tgenabled <> 'O'
           AND c.relname IN ('month_removals', 'month_removed_numbers', 'sales_invoices', 'sales_returns', 'grns', 'purchase_returns', 'stock_documents',
                             'stock_ledger', 'audit_events', 'sales_invoice_lines', 'sales_invoice_payments');
        IF offending IS NOT NULL THEN
            failures := array_append(failures, format('guards left switched off: %s', offending));
        END IF;
    END IF;

    -- 2. Each receipt is for a package of the same business and month, with the same checksum, of a locked month,
    --    and was registered after the package was made.
    SELECT string_agg(r.id::text, ', ') INTO offending
      FROM month_removal_receipts r
      LEFT JOIN month_packages p ON p.id = r.package_id
      LEFT JOIN month_locks l ON l.business_id = r.business_id AND l.month = r.month
     WHERE p.id IS NULL OR p.business_id <> r.business_id OR p.month <> r.month OR p.file_sha256 <> r.file_sha256
        OR l.id IS NULL OR r.registered_at_utc < p.created_at_utc;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('receipts that do not match a package of a locked month: %s', offending));
    END IF;

    -- 3. A receipt is registered with the archive receipt key it was signed with (the key it names).
    SELECT string_agg(r.id::text, ', ') INTO offending
      FROM month_removal_receipts r
     WHERE r.receipt::jsonb ->> 'keyId' IS DISTINCT FROM r.key_id;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('receipts whose key does not match what they name: %s', offending));
    END IF;

    IF to_regclass('public.month_removals') IS NOT NULL THEN
        -- 4. Each removal is of a locked month, against the receipt for that month of this business.
        SELECT string_agg(r.id::text, ', ') INTO offending
          FROM month_removals r LEFT JOIN month_removal_receipts c ON c.id = r.receipt_id
         WHERE c.id IS NULL OR c.business_id <> r.business_id OR c.month <> r.month
            OR NOT EXISTS (SELECT 1 FROM month_locks l WHERE l.business_id = r.business_id AND l.month = r.month);
        IF offending IS NOT NULL THEN
            failures := array_append(failures, format('removals without their receipt or lock: %s', offending));
        END IF;

        -- 5. A removed month holds none of the records removal takes; its stock movements are only carried-forward totals.
        SELECT string_agg(DISTINCT b.code || ' ' || to_char(r.month, 'YYYY-MM') || ' ' || x.t, ', ') INTO offending
          FROM month_removals r JOIN businesses b ON b.id = r.business_id
          JOIN LATERAL (
                SELECT 'sales_invoices' AS t FROM sales_invoices i WHERE i.business_id = r.business_id AND date_trunc('month', i.business_date)::date = r.month
                UNION ALL SELECT 'sales_returns' FROM sales_returns s WHERE s.business_id = r.business_id AND date_trunc('month', s.business_date)::date = r.month
                UNION ALL SELECT 'grns' FROM grns g WHERE g.business_id = r.business_id AND date_trunc('month', g.business_date)::date = r.month
                UNION ALL SELECT 'purchase_returns' FROM purchase_returns p WHERE p.business_id = r.business_id AND date_trunc('month', p.business_date)::date = r.month
                UNION ALL SELECT 'stock_documents' FROM stock_documents d WHERE d.business_id = r.business_id AND date_trunc('month', d.business_date)::date = r.month
                UNION ALL SELECT 'stock_ledger' FROM stock_ledger l
                           WHERE l.business_id = r.business_id AND date_trunc('month', l.business_date)::date = r.month
                             AND (l.movement_type <> 'CARRIED_FORWARD' OR l.document_id <> r.id)) x ON true;
        IF offending IS NOT NULL THEN
            failures := array_append(failures, format('removed months still holding records: %s', offending));
        END IF;

        -- 6. Every carried-forward total belongs to the removal of its own month.
        SELECT string_agg(DISTINCT l.document_id::text, ', ') INTO offending
          FROM stock_ledger l LEFT JOIN month_removals r ON r.id = l.document_id
         WHERE l.movement_type = 'CARRIED_FORWARD'
           AND (r.id IS NULL OR r.business_id <> l.business_id OR r.month <> date_trunc('month', l.business_date)::date);
        IF offending IS NOT NULL THEN
            failures := array_append(failures, format('carried-forward stock without its removal: %s', offending));
        END IF;
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Retention verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Retention verification passed (% on %).', current_database(), version();
END
$$;
