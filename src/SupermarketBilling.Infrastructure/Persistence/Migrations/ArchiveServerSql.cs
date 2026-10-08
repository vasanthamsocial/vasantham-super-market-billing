namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class ArchiveServerSql
{
    public static readonly string[] Tables = ["archive_grants", "archive_sources", "archive_imports", "archive_records", "archive_masters"];

    /// <summary>
    /// Archived records never change (spec: "archived transactions must be immutable"). An import keeps what it was; it
    /// only moves forward through the accountant's and the owner's approvals. Masters are refreshed, never removed. Grants
    /// and trusted store servers are revoked once, never deleted.
    /// </summary>
    public const string Guards = """
        CREATE FUNCTION sb_archive_import_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Archived months cannot be removed.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.business_code, NEW.business_name, NEW.month, NEW.source_id, NEW.file_sha256, NEW.file_size,
                NEW.manifest, NEW.imported_by_user_id, NEW.imported_at_utc)
                 IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.business_code, OLD.business_name, OLD.month, OLD.source_id, OLD.file_sha256,
                OLD.file_size, OLD.manifest, OLD.imported_by_user_id, OLD.imported_at_utc)
               OR (OLD.verification <> '{}'::jsonb AND NEW.verification IS DISTINCT FROM OLD.verification)
               OR NOT ((NEW.status = OLD.status)
                    OR (OLD.status = 'VERIFIED' AND NEW.status = 'ACCOUNTANT_APPROVED')
                    OR (OLD.status = 'ACCOUNTANT_APPROVED' AND NEW.status = 'APPROVED'))
               OR (OLD.accountant_approved_by_user_id IS NOT NULL AND (NEW.accountant_approved_by_user_id, NEW.accountant_approved_at_utc, NEW.accountant_note)
                    IS DISTINCT FROM (OLD.accountant_approved_by_user_id, OLD.accountant_approved_at_utc, OLD.accountant_note))
               OR (OLD.owner_approved_by_user_id IS NOT NULL AND (NEW.owner_approved_by_user_id, NEW.owner_approved_at_utc, NEW.owner_note)
                    IS DISTINCT FROM (OLD.owner_approved_by_user_id, OLD.owner_approved_at_utc, OLD.owner_note)) THEN
                RAISE EXCEPTION 'An archived month keeps what was imported; it only moves forward through its approvals.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_archive_imports_guard BEFORE UPDATE OR DELETE ON archive_imports FOR EACH ROW EXECUTE FUNCTION sb_archive_import_guard();
        CREATE TRIGGER trg_archive_imports_no_truncate BEFORE TRUNCATE ON archive_imports FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();

        CREATE FUNCTION sb_archive_master_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Archived master data cannot be removed.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.tenant_id, NEW.business_id, NEW.dataset, NEW.record_id) IS DISTINCT FROM (OLD.tenant_id, OLD.business_id, OLD.dataset, OLD.record_id)
               OR NEW.month < OLD.month THEN
                RAISE EXCEPTION 'Archived master data is only refreshed by a newer month.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_archive_masters_guard BEFORE UPDATE OR DELETE ON archive_masters FOR EACH ROW EXECUTE FUNCTION sb_archive_master_guard();
        CREATE TRIGGER trg_archive_masters_no_truncate BEFORE TRUNCATE ON archive_masters FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();

        CREATE FUNCTION sb_archive_revocable_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION '% are revoked, never deleted.', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
            END IF;
            IF (to_jsonb(NEW) - ARRAY['revoked_at_utc', 'revoked_by_user_id']) IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['revoked_at_utc', 'revoked_by_user_id'])
               OR (OLD.revoked_at_utc IS NOT NULL AND (NEW.revoked_at_utc, NEW.revoked_by_user_id) IS DISTINCT FROM (OLD.revoked_at_utc, OLD.revoked_by_user_id)) THEN
                RAISE EXCEPTION '% keep what they were; they are only revoked, once.', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_archive_grants_guard BEFORE UPDATE OR DELETE ON archive_grants FOR EACH ROW EXECUTE FUNCTION sb_archive_revocable_guard();
        CREATE TRIGGER trg_archive_sources_guard BEFORE UPDATE OR DELETE ON archive_sources FOR EACH ROW EXECUTE FUNCTION sb_archive_revocable_guard();
        """;

    public const string DropGuards = """
        DROP TRIGGER IF EXISTS trg_archive_sources_guard ON archive_sources;
        DROP TRIGGER IF EXISTS trg_archive_grants_guard ON archive_grants;
        DROP FUNCTION IF EXISTS sb_archive_revocable_guard();
        DROP TRIGGER IF EXISTS trg_archive_masters_no_truncate ON archive_masters;
        DROP TRIGGER IF EXISTS trg_archive_masters_guard ON archive_masters;
        DROP FUNCTION IF EXISTS sb_archive_master_guard();
        DROP TRIGGER IF EXISTS trg_archive_imports_no_truncate ON archive_imports;
        DROP TRIGGER IF EXISTS trg_archive_imports_guard ON archive_imports;
        DROP FUNCTION IF EXISTS sb_archive_import_guard();
        """;
}
