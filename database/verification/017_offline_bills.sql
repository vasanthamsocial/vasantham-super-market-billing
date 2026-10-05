-- Offline counter billing verification (D-039): every number of a counter's offline series is accounted for, with no
-- gaps; every invoice in an offline series came from a received bill and is exactly that bill; one offline device per
-- counter.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
BEGIN
    IF to_regclass('public.offline_bills') IS NULL THEN
        RAISE NOTICE 'Offline billing tables not present yet; skipping offline billing verification.';
        RETURN;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_offline_bills_guard' AND NOT tgisinternal AND tgenabled = 'O') THEN
        failures := array_append(failures, 'trigger trg_offline_bills_guard is missing or disabled');
    END IF;

    -- 1. Each counter's offline series is numbered 1 to its last bill, none missing, and the series moves on from there.
    SELECT string_agg(DISTINCT b.counter_id || ' ' || b.number_prefix, ', ') INTO offending
      FROM (SELECT counter_id, store_id, number_prefix, count(*) AS bills, max(sequence) AS last
              FROM offline_bills GROUP BY counter_id, store_id, number_prefix) b
      LEFT JOIN document_sequences s ON s.store_id = b.store_id AND s.series = 'INV-' || b.number_prefix
     WHERE b.bills <> b.last OR s.next_number IS DISTINCT FROM b.last + 1;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('offline series with gaps or out of step: %s', offending));
    END IF;

    -- 2. An invoice in an offline series is a received offline bill, and is that bill (same number, total, time, cashier, counter).
    SELECT string_agg(i.number, ', ') INTO offending
      FROM sales_invoices i
      LEFT JOIN offline_bills b ON b.invoice_id = i.id
     WHERE i.number_prefix LIKE '%/OF'
       AND (b.id IS NULL OR b.number <> i.number OR b.grand_total <> i.grand_total OR b.issued_at_utc <> i.issued_at_utc
            OR b.cashier_user_id <> i.cashier_user_id OR b.counter_id <> i.counter_id OR b.device_id <> i.device_id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('offline-series invoices that are not their received bill: %s', offending));
    END IF;

    -- 3. Posted bills have their invoice; the others have none.
    SELECT string_agg(b.number, ', ') INTO offending
      FROM offline_bills b
      LEFT JOIN sales_invoices i ON i.id = b.invoice_id
     WHERE (b.status IN ('POSTED', 'RESOLVED_POSTED')) <> (i.id IS NOT NULL);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('offline bills whose invoice is missing or should not exist: %s', offending));
    END IF;

    -- 4. At most one device per counter bills offline, and never a revoked one.
    SELECT string_agg(counter_id::text, ', ') INTO offending
      FROM counter_devices
     WHERE offline_max_bills IS NOT NULL
     GROUP BY counter_id
    HAVING count(*) > 1 OR bool_or(revoked_at_utc IS NOT NULL);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('counters with more than one (or a revoked) offline device: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Offline billing verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Offline billing verification passed (% on %).', current_database(), version();
END
$$;
