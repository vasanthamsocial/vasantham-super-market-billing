-- Foundation verification checks. Run through scripts/verify-database.ps1.
-- Raises an exception (non-zero exit) when any check fails.
\o /dev/null
SELECT set_config('sb.app_role', :'app_role', false);
SELECT set_config('sb.migrator_role', :'migrator_role', false);
\o

DO $$
DECLARE
    app_role text := current_setting('sb.app_role');
    migrator_role text := current_setting('sb.migrator_role');
    failures text[] := ARRAY[]::text[];
    offending text;
BEGIN
    -- 1. Migrations have been applied.
    IF to_regclass('public.__ef_migrations_history') IS NULL THEN
        failures := array_append(failures, 'migration history table is missing (run scripts/db-migrate.ps1)');
    END IF;

    -- 2. Audit trail exists and is append-only.
    IF to_regclass('public.audit_events') IS NULL THEN
        failures := array_append(failures, 'audit_events table is missing');
    ELSE
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_audit_events_no_update_delete' AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, 'audit_events UPDATE/DELETE guard trigger is missing or disabled');
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_audit_events_no_truncate' AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, 'audit_events TRUNCATE guard trigger is missing or disabled');
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_audit_events_payload_is_object') THEN
            failures := array_append(failures, 'audit_events payload check constraint is missing');
        END IF;
    END IF;

    -- 3. Least privilege: the runtime account owns nothing and cannot create objects.
    IF has_schema_privilege(app_role, 'public', 'CREATE') THEN
        failures := array_append(failures, format('role %s can CREATE in schema public', app_role));
    END IF;
    IF has_database_privilege(app_role, current_database(), 'CREATE') THEN
        failures := array_append(failures, format('role %s can CREATE schemas in the database', app_role));
    END IF;
    SELECT string_agg(c.relname, ', ') INTO offending
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = 'public' AND pg_get_userbyid(c.relowner) = app_role;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('role %s owns objects: %s', app_role, offending));
    END IF;
    SELECT string_agg(c.relname, ', ') INTO offending
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'S', 'v')
       AND pg_get_userbyid(c.relowner) <> migrator_role;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('objects not owned by %s: %s', migrator_role, offending));
    END IF;
    IF (SELECT rolsuper FROM pg_roles WHERE rolname = app_role) THEN
        failures := array_append(failures, format('role %s is a superuser', app_role));
    END IF;

    -- 4. Exact arithmetic: no floating-point columns and no unbounded numerics anywhere in the schema.
    SELECT string_agg(table_name || '.' || column_name, ', ') INTO offending
      FROM information_schema.columns
     WHERE table_schema = 'public' AND data_type IN ('real', 'double precision');
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('floating-point columns found: %s', offending));
    END IF;
    SELECT string_agg(table_name || '.' || column_name, ', ') INTO offending
      FROM information_schema.columns
     WHERE table_schema = 'public' AND data_type = 'numeric' AND numeric_scale IS NULL;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('numeric columns without explicit precision/scale: %s', offending));
    END IF;

    -- 5. Multi-tenancy: every table with a tenant_id (and tenants itself) is protected by row-level security,
    --    and the runtime account cannot bypass it.
    SELECT string_agg(c.relname, ', ') INTO offending
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = 'public' AND c.relkind = 'r'
       AND (c.relname = 'tenants' OR EXISTS (
             SELECT 1 FROM information_schema.columns col
              WHERE col.table_schema = 'public' AND col.table_name = c.relname AND col.column_name = 'tenant_id'))
       AND c.relname <> 'installation'
       AND (NOT c.relrowsecurity OR NOT EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid));
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('tenant tables without row-level security or policies: %s', offending));
    END IF;
    IF to_regclass('public.tenants') IS NOT NULL AND (SELECT rolbypassrls FROM pg_roles WHERE rolname = app_role) THEN
        failures := array_append(failures, format('role %s can bypass row-level security', app_role));
    END IF;
    SELECT string_agg(p.proname, ', ') INTO offending
      FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
     WHERE n.nspname = 'public' AND p.prosecdef
       AND (p.proconfig IS NULL OR NOT EXISTS (SELECT 1 FROM unnest(p.proconfig) cfg WHERE cfg LIKE 'search_path=%')
            OR has_function_privilege('public', p.oid, 'EXECUTE'));
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('SECURITY DEFINER functions without a fixed search_path or executable by PUBLIC: %s', offending));
    END IF;

    -- 6. Storage integrity and UTC.
    IF current_setting('data_checksums') <> 'on' THEN
        failures := array_append(failures, 'data checksums are not enabled on this cluster');
    END IF;
    IF current_setting('server_encoding') <> 'UTF8' THEN
        failures := array_append(failures, 'server encoding is not UTF8');
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Database verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Foundation verification passed (% on %).', current_database(), version();
END
$$;
