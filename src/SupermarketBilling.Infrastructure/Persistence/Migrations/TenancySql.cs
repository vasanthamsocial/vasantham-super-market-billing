namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

/// <summary>
/// SQL for multi-tenancy: back-filling existing single-tenant data, PostgreSQL row-level security, and the two
/// narrowly scoped lookup functions needed before a tenant is known (cloud sign-in, session cookie).
/// </summary>
internal static class TenancySql
{
    /// <summary>Tables whose rows belong to one tenant (column tenant_id NOT NULL).</summary>
    public static readonly string[] TenantTables =
    [
        "businesses", "stores", "users", "role_assignments", "sessions",
        "password_reset_tokens", "mfa_recovery_codes", "approval_requests",
    ];

    /// <summary>
    /// Creates the installation row. If the database already holds data from before multi-tenancy (an in-store
    /// server set up earlier), it all becomes one tenant, derived from the first business, and the installation is
    /// bound to it. Audit history is back-filled with its append-only trigger briefly disabled; only this
    /// migration (running as the schema owner) can do that.
    /// </summary>
    public static string SeedInstallationAndBackfill()
    {
        var updates = string.Join("\n", TenantTables.Select(t => $"    UPDATE {t} SET tenant_id = new_tenant;"));
        var dropDefaults = string.Join("\n", TenantTables.Select(t => $"ALTER TABLE {t} ALTER COLUMN tenant_id DROP DEFAULT;"));
        return $$"""
            INSERT INTO installation (id, installation_id, tenant_id, created_at_utc)
            VALUES (1, gen_random_uuid(), NULL, now());

            DO $$
            DECLARE
                first_business record;
                new_tenant uuid;
            BEGIN
                SELECT * INTO first_business FROM businesses ORDER BY created_at_utc LIMIT 1;
                IF NOT FOUND THEN
                    RETURN;
                END IF;

                new_tenant := gen_random_uuid();
                INSERT INTO tenants (id, code, name, is_active, created_at_utc)
                VALUES (new_tenant, rpad(first_business.code, 3, 'X'), first_business.legal_name, true, now());
            {{updates}}
                ALTER TABLE audit_events DISABLE TRIGGER trg_audit_events_no_update_delete;
                UPDATE audit_events SET tenant_id = new_tenant;
                ALTER TABLE audit_events ENABLE TRIGGER trg_audit_events_no_update_delete;
                UPDATE installation SET tenant_id = new_tenant WHERE id = 1;
            END
            $$;

            {{dropDefaults}}
            """;
    }

    /// <summary>
    /// Row-level security: the runtime account only sees and writes rows of the tenant named in the connection
    /// setting sb.tenant_id. With no tenant set, tenant tables look empty. The schema owner (migrations,
    /// SECURITY DEFINER functions) and the container superuser (backups) are not restricted.
    /// </summary>
    public static string EnableRowLevelSecurity()
    {
        var policies = string.Join("\n", TenantTables.Select(t => $"""
            ALTER TABLE {t} ENABLE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON {t}
                USING (tenant_id = sb_current_tenant())
                WITH CHECK (tenant_id = sb_current_tenant());
            """));
        return $$"""
            CREATE FUNCTION sb_current_tenant() RETURNS uuid
                LANGUAGE sql STABLE
                AS $$ SELECT nullif(current_setting('sb.tenant_id', true), '')::uuid $$;

            {{policies}}

            ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON tenants
                USING (id = sb_current_tenant())
                WITH CHECK (id = sb_current_tenant());

            -- Audit events: each tenant sees its own. Events recorded before any tenant was known (for example a
            -- cloud sign-in with an unknown company code) belong to no tenant and are visible only without one.
            ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
            CREATE POLICY tenant_read ON audit_events FOR SELECT
                USING (tenant_id = sb_current_tenant() OR (tenant_id IS NULL AND sb_current_tenant() IS NULL));
            CREATE POLICY tenant_append ON audit_events FOR INSERT
                WITH CHECK (tenant_id IS NULL OR tenant_id = sb_current_tenant());
            -- UPDATE and DELETE are always rejected by the append-only trigger. These policies only let such an
            -- attempt reach the trigger, so tampering fails loudly instead of silently matching no rows.
            CREATE POLICY tenant_tamper_detection_update ON audit_events FOR UPDATE
                USING (tenant_id = sb_current_tenant());
            CREATE POLICY tenant_tamper_detection_delete ON audit_events FOR DELETE
                USING (tenant_id = sb_current_tenant());

            -- Lookups needed before the tenant is known. They return only an id, never row data.
            CREATE FUNCTION sb_tenant_by_code(p_code text) RETURNS uuid
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$ SELECT id FROM public.tenants WHERE code = p_code AND is_active $$;
            CREATE FUNCTION sb_session_tenant(p_token_hash bytea) RETURNS uuid
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
                AS $$ SELECT tenant_id FROM public.sessions WHERE token_hash = p_token_hash AND revoked_at_utc IS NULL $$;
            REVOKE ALL ON FUNCTION sb_tenant_by_code(text) FROM PUBLIC;
            REVOKE ALL ON FUNCTION sb_session_tenant(bytea) FROM PUBLIC;
            """;
    }

    public static string DisableRowLevelSecurity()
    {
        var tables = TenantTables.Append("tenants").ToArray();
        var drops = string.Join("\n", tables.Select(t => $"DROP POLICY IF EXISTS tenant_isolation ON {t};\nALTER TABLE {t} DISABLE ROW LEVEL SECURITY;"));
        return $"""
            {drops}
            DROP POLICY IF EXISTS tenant_read ON audit_events;
            DROP POLICY IF EXISTS tenant_append ON audit_events;
            DROP POLICY IF EXISTS tenant_tamper_detection_update ON audit_events;
            DROP POLICY IF EXISTS tenant_tamper_detection_delete ON audit_events;
            ALTER TABLE audit_events DISABLE ROW LEVEL SECURITY;
            DROP FUNCTION IF EXISTS sb_tenant_by_code(text);
            DROP FUNCTION IF EXISTS sb_session_tenant(bytea);
            DROP FUNCTION IF EXISTS sb_current_tenant();
            """;
    }
}
