-- Archive server verification (D-042): archived records cannot change; every imported month holds exactly the records
-- its package described; approvals came in order from two different people; every record and master belongs to an import.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    guarded text;
BEGIN
    IF to_regclass('public.archive_imports') IS NULL THEN
        RAISE NOTICE 'Archive server tables not present yet; skipping archive server verification.';
        RETURN;
    END IF;

    FOREACH guarded IN ARRAY ARRAY['trg_archive_imports_guard', 'trg_archive_masters_guard', 'trg_archive_grants_guard', 'trg_archive_sources_guard']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = guarded AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', guarded));
        END IF;
    END LOOP;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE c.relname = 'archive_records' AND NOT t.tgisinternal AND t.tgenabled = 'O') THEN
        failures := array_append(failures, 'archive_records is not append-only');
    END IF;

    -- 1. Each imported month holds exactly the number of records its package described, dataset by dataset.
    SELECT string_agg(DISTINCT i.business_code || ' ' || to_char(i.month, 'YYYY-MM') || ' ' || (d ->> 'name'), ', ') INTO offending
      FROM archive_imports i
     CROSS JOIN LATERAL jsonb_array_elements(i.manifest -> 'datasets') d
     WHERE (d ->> 'name') NOT IN ('businesses', 'users', 'cheques', 'tax_registrations', 'stores', 'counters', 'units', 'categories', 'brands', 'customer_groups',
                                  'products', 'product_variants', 'variant_units', 'variant_mrps', 'variant_barcodes', 'debtors', 'suppliers', 'routes',
                                  'transporters', 'role_assignments')
       AND (d ->> 'records')::bigint <> (SELECT count(*) FROM archive_records r WHERE r.import_id = i.id AND r.dataset = d ->> 'name');
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('imported months whose records differ from their package: %s', offending));
    END IF;

    -- 2. A record belongs to its import's business and month.
    SELECT string_agg(DISTINCT r.import_id::text, ', ') INTO offending
      FROM archive_records r JOIN archive_imports i ON i.id = r.import_id
     WHERE r.business_id <> i.business_id OR r.month <> i.month;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('records filed under another business or month than their import: %s', offending));
    END IF;

    -- 3. Approvals in order and by two different people; every verified import has its verification report.
    SELECT string_agg(i.id::text, ', ') INTO offending
      FROM archive_imports i
     WHERE (i.status = 'APPROVED' AND (i.accountant_approved_by_user_id IS NULL OR i.owner_approved_by_user_id = i.accountant_approved_by_user_id
                                      OR i.owner_approved_at_utc < i.accountant_approved_at_utc))
        OR i.verification = '{}'::jsonb;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('imports approved out of order, by one person, or without verification: %s', offending));
    END IF;

    -- 4. Every import came from a registered store server.
    SELECT string_agg(i.id::text, ', ') INTO offending
      FROM archive_imports i LEFT JOIN archive_sources s ON s.id = i.source_id
     WHERE s.id IS NULL OR s.registered_at_utc > i.imported_at_utc;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('imports from a store server that was not registered: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Archive server verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Archive server verification passed (% on %).', current_database(), version();
END
$$;
