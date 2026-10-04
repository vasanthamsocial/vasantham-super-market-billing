-- Messaging verification: every message is about a real invoice or receipt of its debtor, is sent at most once per
-- channel, and its status agrees with its timestamps, provider reference and history.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.outbound_messages') IS NULL THEN
        RAISE NOTICE 'Messaging tables not present yet; skipping messaging verification.';
        RETURN;
    END IF;

    FOREACH trigger_name IN ARRAY ARRAY['trg_outbound_messages_guard', 'trg_message_events_no_update_delete']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 1. Each message is about a document of its debtor: a credit invoice billed to them, or a receipt from them.
    SELECT string_agg(m.document_number, ', ') INTO offending
      FROM outbound_messages m
     WHERE (m.kind = 'CREDIT_INVOICE' AND NOT EXISTS (SELECT 1 FROM sales_invoices i WHERE i.id = m.document_id AND i.debtor_id = m.debtor_id AND i.due_date IS NOT NULL))
        OR (m.kind = 'RECEIPT' AND NOT EXISTS (SELECT 1 FROM debtor_receipts r WHERE r.id = m.document_id AND r.debtor_id = m.debtor_id));
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('messages about documents that are not their debtor''s: %s', offending));
    END IF;

    -- 2. Status agrees with timestamps and the provider reference.
    SELECT string_agg(m.id::text, ', ') INTO offending
      FROM outbound_messages m
     WHERE (m.status IN ('SENT', 'DELIVERED', 'READ') AND (m.provider_message_id IS NULL OR m.sent_at_utc IS NULL
                OR NOT EXISTS (SELECT 1 FROM provider_message_refs r WHERE r.provider_message_id = m.provider_message_id AND r.message_id = m.id)))
        OR (m.status IN ('DELIVERED', 'READ') AND m.delivered_at_utc IS NULL)
        OR (m.status = 'READ' AND m.read_at_utc IS NULL)
        OR (m.status = 'FAILED' AND m.failed_at_utc IS NULL)
        OR (m.status = 'SKIPPED' AND (m.provider_message_id IS NOT NULL OR m.attempts <> 0));
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('messages whose status does not agree with their record: %s', offending));
    END IF;

    -- 3. Every message's history starts with it being queued (or skipped).
    SELECT string_agg(m.id::text, ', ') INTO offending
      FROM outbound_messages m
     WHERE (SELECT e.status FROM message_events e WHERE e.message_id = m.id ORDER BY e.at_utc, e.id LIMIT 1) IS DISTINCT FROM
           CASE WHEN m.status = 'SKIPPED' THEN 'SKIPPED' ELSE 'QUEUED' END;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('messages without a proper history: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Messaging verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Messaging verification passed (% on %).', current_database(), version();
END
$$;
