namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class OfflineSql
{
    public static readonly string[] Tables = ["collection_devices", "offline_submissions"];

    /// <summary>
    /// A phone keeps who it belongs to; its sequence only moves forward and its revocation is final; it is never deleted.
    /// A received collection keeps what the phone sent; its only change is a quarantined one being resolved, once.
    /// </summary>
    public const string Guards = """
        CREATE FUNCTION sb_collection_device_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Collection phones are revoked, never deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.collector_user_id, NEW.token_hash, NEW.enrolled_by_user_id, NEW.enrolled_at_utc)
                 IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.collector_user_id, OLD.token_hash, OLD.enrolled_by_user_id, OLD.enrolled_at_utc)
               OR NEW.last_sequence < OLD.last_sequence
               OR (OLD.revoked_at_utc IS NOT NULL AND (NEW.revoked_at_utc, NEW.revoked_by_user_id) IS DISTINCT FROM (OLD.revoked_at_utc, OLD.revoked_by_user_id)) THEN
                RAISE EXCEPTION 'A collection phone keeps its owner, its order of collections and its revocation.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_collection_devices_guard BEFORE UPDATE OR DELETE ON collection_devices FOR EACH ROW EXECUTE FUNCTION sb_collection_device_guard();

        CREATE FUNCTION sb_offline_submission_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Offline collections cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.status <> 'QUARANTINED' OR NEW.status NOT IN ('RESOLVED_ACCEPTED', 'RESOLVED_REJECTED')
               OR (to_jsonb(NEW) - ARRAY['status', 'receipt_id', 'resolved_by_user_id', 'resolved_at_utc', 'resolution_note'])
                  IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['status', 'receipt_id', 'resolved_by_user_id', 'resolved_at_utc', 'resolution_note']) THEN
                RAISE EXCEPTION 'An offline collection keeps what was sent; only a quarantined one is resolved, once.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_offline_submissions_guard BEFORE UPDATE OR DELETE ON offline_submissions FOR EACH ROW EXECUTE FUNCTION sb_offline_submission_guard();
        """;

    public const string DropGuards = """
        DROP TRIGGER IF EXISTS trg_offline_submissions_guard ON offline_submissions;
        DROP FUNCTION IF EXISTS sb_offline_submission_guard();
        DROP TRIGGER IF EXISTS trg_collection_devices_guard ON collection_devices;
        DROP FUNCTION IF EXISTS sb_collection_device_guard();
        """;
}
