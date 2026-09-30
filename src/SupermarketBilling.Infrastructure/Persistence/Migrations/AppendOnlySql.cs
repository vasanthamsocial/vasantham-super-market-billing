namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

/// <summary>
/// SQL used by migrations to make tables append-only at the database level. This protects ledgers and audit
/// history even from code paths that bypass the application (ad-hoc SQL, a compromised runtime account).
/// Only the schema owner can drop the triggers, and the runtime account is not the schema owner.
/// </summary>
internal static class AppendOnlySql
{
    public const string CreateGuardFunction = """
        CREATE OR REPLACE FUNCTION sb_reject_mutation() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'Table % is append-only; % is not permitted. Post a correcting entry instead.',
                TG_TABLE_NAME, TG_OP
                USING ERRCODE = 'restrict_violation';
        END;
        $$;
        """;

    public const string DropGuardFunction = "DROP FUNCTION IF EXISTS sb_reject_mutation();";

    public static string Protect(string table) => $"""
        CREATE TRIGGER trg_{table}_no_update_delete
            BEFORE UPDATE OR DELETE ON {table}
            FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
        CREATE TRIGGER trg_{table}_no_truncate
            BEFORE TRUNCATE ON {table}
            FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
        """;

    public static string Unprotect(string table) => $"""
        DROP TRIGGER IF EXISTS trg_{table}_no_update_delete ON {table};
        DROP TRIGGER IF EXISTS trg_{table}_no_truncate ON {table};
        """;
}
