namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class MessagingSql
{
    public static readonly string[] Tables = ["message_templates", "outbound_messages", "message_events", "messaging_settings", "provider_message_refs"];

    /// <summary>
    /// A message's addressee, wording and document never change; its status moves only along the allowed steps
    /// (queued, sent, delivered, read; failed; a failed message never accepted by the provider may be queued again);
    /// skipped messages stay as they are; messages are never deleted.
    /// </summary>
    public const string MessageGuard = """
        CREATE FUNCTION sb_outbound_message_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Messages cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.debtor_id, NEW.channel, NEW.kind, NEW.document_id, NEW.document_number, NEW.to_number,
                NEW.parameters_json, NEW.body, NEW.skip_reason, NEW.created_at_utc)
               IS DISTINCT FROM
               (OLD.id, OLD.tenant_id, OLD.business_id, OLD.debtor_id, OLD.channel, OLD.kind, OLD.document_id, OLD.document_number, OLD.to_number,
                OLD.parameters_json, OLD.body, OLD.skip_reason, OLD.created_at_utc)
               OR (OLD.provider_message_id IS NOT NULL AND NEW.provider_message_id IS DISTINCT FROM OLD.provider_message_id)
               OR NOT ((OLD.status, NEW.status) IN (('QUEUED', 'QUEUED'), ('QUEUED', 'SENT'), ('QUEUED', 'FAILED'), ('SENT', 'DELIVERED'), ('SENT', 'READ'),
                                                     ('SENT', 'FAILED'), ('DELIVERED', 'READ'), ('DELIVERED', 'FAILED'))
                       OR (OLD.status = 'FAILED' AND NEW.status = 'QUEUED' AND OLD.provider_message_id IS NULL)) THEN
                RAISE EXCEPTION 'A message can only move forward (from %).', OLD.status USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_outbound_messages_guard BEFORE UPDATE OR DELETE ON outbound_messages FOR EACH ROW EXECUTE FUNCTION sb_outbound_message_guard();
        """;

    /// <summary>The active tenants' ids, for background work (sending the outbox) that runs without a signed-in user. Only ids, never row data.</summary>
    public const string ActiveTenantsFunction = """
        CREATE FUNCTION sb_active_tenants() RETURNS SETOF uuid
            LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
            AS $$ SELECT id FROM public.tenants WHERE is_active $$;
        REVOKE ALL ON FUNCTION sb_active_tenants() FROM PUBLIC;
        """;

    public const string DropActiveTenantsFunction = "DROP FUNCTION IF EXISTS sb_active_tenants();";

    /// <summary>
    /// The provider's delivery reports name only the provider's message id; this finds which tenant and message it is,
    /// before any tenant is set (the table itself is protected by row-level security like every tenant table).
    /// </summary>
    public const string MessageRefFunction = """
        CREATE FUNCTION sb_provider_message_ref(p_provider_message_id text) RETURNS TABLE (tenant_id uuid, message_id uuid)
            LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
            AS $$ SELECT r.tenant_id, r.message_id FROM public.provider_message_refs r WHERE r.provider_message_id = p_provider_message_id $$;
        REVOKE ALL ON FUNCTION sb_provider_message_ref(text) FROM PUBLIC;
        """;

    public const string DropMessageRefFunction = "DROP FUNCTION IF EXISTS sb_provider_message_ref(text);";

    public const string DropMessageGuard = """
        DROP TRIGGER IF EXISTS trg_outbound_messages_guard ON outbound_messages;
        DROP FUNCTION IF EXISTS sb_outbound_message_guard();
        """;
}
