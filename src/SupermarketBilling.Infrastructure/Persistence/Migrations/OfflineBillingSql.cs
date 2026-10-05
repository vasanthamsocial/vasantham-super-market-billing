namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class OfflineBillingSql
{
    public static readonly string[] Tables = ["offline_bills"];

    /// <summary>
    /// A received offline bill keeps what the counter sent and its number forever. Its only changes: a quarantined bill is
    /// resolved once (posted or not), and a posted bill's flagged points are marked reviewed once.
    /// </summary>
    public const string Guards = """
        CREATE FUNCTION sb_offline_bill_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE
            resolving boolean := OLD.status = 'QUARANTINED' AND NEW.status IN ('RESOLVED_POSTED', 'RESOLVED_VOID');
            reviewing boolean := OLD.status = 'POSTED' AND NEW.status = 'POSTED' AND OLD.review IS NOT NULL AND OLD.reviewed_at_utc IS NULL
                                 AND NEW.reviewed_at_utc IS NOT NULL;
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Offline bills cannot be deleted: every number of the series stays accounted for.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF NOT (resolving OR reviewing)
               OR (resolving AND (to_jsonb(NEW) - ARRAY['status', 'invoice_id', 'resolved_by_user_id', 'resolved_at_utc', 'resolution_note'])
                                 IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['status', 'invoice_id', 'resolved_by_user_id', 'resolved_at_utc', 'resolution_note']))
               OR (reviewing AND (to_jsonb(NEW) - ARRAY['reviewed_by_user_id', 'reviewed_at_utc', 'resolution_note'])
                                 IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['reviewed_by_user_id', 'reviewed_at_utc', 'resolution_note'])) THEN
                RAISE EXCEPTION 'An offline bill keeps what the counter sent; only a quarantined one is resolved, and a flagged one reviewed, once.'
                    USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_offline_bills_guard BEFORE UPDATE OR DELETE ON offline_bills FOR EACH ROW EXECUTE FUNCTION sb_offline_bill_guard();
        CREATE TRIGGER trg_offline_bills_no_truncate BEFORE TRUNCATE ON offline_bills FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
        """;

    public const string DropGuards = """
        DROP TRIGGER IF EXISTS trg_offline_bills_no_truncate ON offline_bills;
        DROP TRIGGER IF EXISTS trg_offline_bills_guard ON offline_bills;
        DROP FUNCTION IF EXISTS sb_offline_bill_guard();
        """;
}
