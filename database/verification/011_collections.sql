-- Collection set-up verification: visits and promises are protected, plans point at their own business, and plans
-- whose collectors can no longer collect are reported.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.collection_plans') IS NULL THEN
        RAISE NOTICE 'Collection tables not present yet; skipping collection verification.';
        RETURN;
    END IF;

    FOREACH trigger_name IN ARRAY ARRAY['trg_collection_visits_guard', 'trg_payment_promises_guard']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 1. Plans, visits and promises belong to the business of their debtor (and route).
    SELECT string_agg(d.code, ', ') INTO offending
      FROM collection_plans p JOIN debtors d ON d.id = p.debtor_id
      LEFT JOIN routes r ON r.id = p.route_id
     WHERE p.business_id <> d.business_id OR (r.id IS NOT NULL AND r.business_id <> p.business_id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('collection plans outside their business: %s', offending));
    END IF;

    SELECT string_agg(v.id::text, ', ') INTO offending
      FROM collection_visits v JOIN debtors d ON d.id = v.debtor_id WHERE v.business_id <> d.business_id;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('visits outside their business: %s', offending));
    END IF;

    -- 2. Plans whose collector no longer holds a collecting role are reported (not an error: roles are revoked in the normal course).
    SELECT string_agg(DISTINCT d.code, ', ') INTO offending
      FROM collection_plans p JOIN debtors d ON d.id = p.debtor_id
     WHERE p.primary_collector_user_id IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM role_assignments a
                        WHERE a.user_id = p.primary_collector_user_id AND a.business_id = p.business_id AND a.revoked_at_utc IS NULL
                          AND a.role_code IN ('owner', 'collection_person'));
    IF offending IS NOT NULL THEN
        RAISE NOTICE 'Debtors whose primary collector can no longer collect: %', offending;
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Collection verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Collection verification passed (% on %).', current_database(), version();
END
$$;
