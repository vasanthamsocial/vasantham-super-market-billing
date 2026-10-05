-- Offline collection verification: every phone's collections arrived in order with no gaps; each posted one is a receipt
-- for exactly what the phone sent (party, amount, method), keyed by the phone's id; an accepted one is a field receipt
-- by its collector, in a round.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.offline_submissions') IS NULL THEN
        RAISE NOTICE 'Offline tables not present yet; skipping offline verification.';
        RETURN;
    END IF;

    FOREACH trigger_name IN ARRAY ARRAY['trg_collection_devices_guard', 'trg_offline_submissions_guard']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 1. Each phone's collections are numbered 1 to its last sequence, none missing.
    SELECT string_agg(d.name, ', ') INTO offending
      FROM collection_devices d
     WHERE d.last_sequence <> (SELECT count(*) FROM offline_submissions s WHERE s.device_id = d.id)
        OR d.last_sequence <> coalesce((SELECT max(s.sequence) FROM offline_submissions s WHERE s.device_id = d.id), 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('phones whose collections are not numbered 1 to the last: %s', offending));
    END IF;

    -- 2. A posted collection is a receipt for what the phone sent, keyed by the phone's id.
    SELECT string_agg(s.id::text, ', ') INTO offending
      FROM offline_submissions s
      JOIN debtor_receipts r ON r.id = s.receipt_id
     WHERE r.debtor_id <> s.debtor_id OR r.amount <> s.amount OR r.method <> s.method
        OR r.idempotency_key <> 'offline-' || replace(s.id::text, '-', '');
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('offline collections whose receipt differs from what was sent: %s', offending));
    END IF;

    -- 3. Accepted on arrival: a field receipt by the collector, in one of their rounds.
    SELECT string_agg(s.id::text, ', ') INTO offending
      FROM offline_submissions s
      JOIN debtor_receipts r ON r.id = s.receipt_id
      LEFT JOIN collector_sessions c ON c.id = r.collector_session_id
     WHERE s.status = 'ACCEPTED' AND (c.id IS NULL OR c.collector_user_id <> s.collector_user_id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('accepted offline collections that are not the collector''s field receipts: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Offline verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Offline verification passed (% on %).', current_database(), version();
END
$$;
