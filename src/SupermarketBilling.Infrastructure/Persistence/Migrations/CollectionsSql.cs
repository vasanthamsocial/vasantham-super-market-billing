namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class CollectionsSql
{
    public static readonly string[] Tables = ["routes", "collection_plans", "collection_visits", "payment_promises", "collector_absences"];

    /// <summary>Assigned visits and promises are kept as made: never deleted, and the only change is being cancelled (once).</summary>
    public static readonly string[] CancelOnly = ["collection_visits", "payment_promises"];

    public static string CancelOnlyGuard(string table) => $$"""
        CREATE FUNCTION sb_{{table}}_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION '% rows cannot be deleted; cancel them instead.', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.is_cancelled OR NOT NEW.is_cancelled
               OR (to_jsonb(NEW) - 'is_cancelled') IS DISTINCT FROM (to_jsonb(OLD) - 'is_cancelled') THEN
                RAISE EXCEPTION '% rows can only be cancelled, once.', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_{{table}}_guard BEFORE UPDATE OR DELETE ON {{table}} FOR EACH ROW EXECUTE FUNCTION sb_{{table}}_guard();
        """;

    public static string DropCancelOnlyGuard(string table) => $"""
        DROP TRIGGER IF EXISTS trg_{table}_guard ON {table};
        DROP FUNCTION IF EXISTS sb_{table}_guard();
        """;
}
