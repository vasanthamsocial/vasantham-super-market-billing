-- Retention verification (spec section 22, D-044): archive receipts are kept as received, and each is for a package
-- this server made of that business's locked month, with the same checksum. (Removal itself is not available yet.)
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

    -- 1. Receipts cannot be changed or removed.
    IF NOT EXISTS (SELECT 1 FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
                    WHERE c.relname = 'month_removal_receipts' AND NOT t.tgisinternal AND t.tgenabled = 'O') THEN
        failures := array_append(failures, 'month_removal_receipts is not append-only');
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

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Retention verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Retention verification passed (% on %).', current_database(), version();
END
$$;
