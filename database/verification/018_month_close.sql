-- Month close verification (spec section 22, D-040): every dated table is guarded by the month lock; months are locked
-- in order; nothing dated in a locked month was recorded after it was locked; every package belongs to a locked month.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    guarded text;
BEGIN
    IF to_regclass('public.month_locks') IS NULL THEN
        RAISE NOTICE 'Month close tables not present yet; skipping month close verification.';
        RETURN;
    END IF;

    -- 1. The lock guards every dated table, and the lock records cannot be changed.
    FOREACH guarded IN ARRAY ARRAY['sales_invoices', 'sales_returns', 'grns', 'purchase_returns', 'stock_documents', 'stock_ledger', 'debtor_ledger',
                                   'supplier_ledger', 'debtor_receipts', 'supplier_payments', 'shifts', 'collector_sessions', 'cheque_events']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_' || guarded || '_month_lock' AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('%s is not guarded by the month lock', guarded));
        END IF;
    END LOOP;
    FOREACH guarded IN ARRAY ARRAY['month_locks', 'month_packages']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE c.relname = guarded AND NOT t.tgisinternal AND t.tgenabled = 'O') THEN
            failures := array_append(failures, format('%s is not append-only', guarded));
        END IF;
    END LOOP;

    -- 2. Months are locked in order: no locked month has an unlocked month with records before it.
    SELECT string_agg(DISTINCT b.code || ' ' || to_char(l.month, 'YYYY-MM'), ', ') INTO offending
      FROM month_locks l
      JOIN businesses b ON b.id = l.business_id
      JOIN LATERAL (
            SELECT date_trunc('month', d)::date AS m FROM (
                SELECT business_date AS d FROM sales_invoices WHERE business_id = l.business_id AND business_date < l.month
                UNION ALL SELECT entry_date FROM debtor_ledger WHERE business_id = l.business_id AND entry_date < l.month
                UNION ALL SELECT entry_date FROM supplier_ledger WHERE business_id = l.business_id AND entry_date < l.month
                UNION ALL SELECT business_date FROM stock_ledger WHERE business_id = l.business_id AND business_date < l.month) x) earlier ON true
     WHERE NOT EXISTS (SELECT 1 FROM month_locks o WHERE o.business_id = l.business_id AND o.month = earlier.m);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('months locked with an earlier month still open: %s', offending));
    END IF;

    -- 3. Nothing dated in a locked month was recorded after the lock.
    SELECT string_agg(DISTINCT i.number, ', ') INTO offending
      FROM sales_invoices i JOIN month_locks l ON l.business_id = i.business_id AND l.month = date_trunc('month', i.business_date)::date
     WHERE i.issued_at_utc > l.locked_at_utc;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('invoices recorded in a month after it was locked: %s', offending));
    END IF;
    SELECT string_agg(DISTINCT s.id::text, ', ') INTO offending
      FROM stock_ledger s JOIN month_locks l ON l.business_id = s.business_id AND l.month = date_trunc('month', s.business_date)::date
     WHERE s.occurred_at_utc > l.locked_at_utc;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('stock movements recorded in a month after it was locked: %s', offending));
    END IF;

    -- 4. Every package is of a locked month (also a foreign key) and made after the lock.
    SELECT string_agg(p.id::text, ', ') INTO offending
      FROM month_packages p LEFT JOIN month_locks l ON l.business_id = p.business_id AND l.month = p.month
     WHERE l.id IS NULL OR p.created_at_utc < l.locked_at_utc;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('packages of months that were not locked: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Month close verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Month close verification passed (% on %).', current_database(), version();
END
$$;
