-- Custody verification: reversed receipts put exactly their amount back and pay nothing any more, cheques match their
-- receipts and statuses, settlements are never taken back beyond what was settled, and handovers add up.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.collector_sessions') IS NULL THEN
        RAISE NOTICE 'Custody tables not present yet; skipping custody verification.';
        RETURN;
    END IF;

    FOREACH trigger_name IN ARRAY ARRAY[
        'trg_debtor_receipts_round', 'trg_collector_sessions_guard', 'trg_cheques_guard', 'trg_collector_session_counts_no_update_delete',
        'trg_cheque_events_no_update_delete', 'trg_receipt_reversals_no_update_delete', 'trg_visit_outcomes_no_update_delete']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    IF NOT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'sb_debtor_settlements_guard' AND prosrc LIKE '%taken back%') THEN
        failures := array_append(failures, 'the debtor settlement guard does not check taken-back settlements');
    END IF;

    -- 1. No payment/charge pair is taken back beyond what it settled.
    SELECT string_agg(format('%s/%s', charge_entry_id, payment_entry_id), ', ') INTO offending
      FROM (SELECT charge_entry_id, payment_entry_id FROM debtor_settlements GROUP BY 1, 2 HAVING sum(amount) < 0
            UNION ALL SELECT charge_entry_id, payment_entry_id FROM supplier_settlements GROUP BY 1, 2 HAVING sum(amount) < 0) x;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('settlements taken back beyond what was settled: %s', offending));
    END IF;

    -- 2. A reversed receipt has exactly one reversal entry for its amount, and pays nothing but that entry.
    SELECT string_agg(r.number, ', ') INTO offending
      FROM receipt_reversals v JOIN debtor_receipts r ON r.id = v.receipt_id
      JOIN debtor_ledger p ON p.document_id = r.id AND p.entry_type = 'RECEIPT'
      LEFT JOIN (SELECT document_id, count(*) n, sum(amount) a FROM debtor_ledger WHERE entry_type = 'RECEIPT_REVERSAL' GROUP BY document_id) e ON e.document_id = r.id
     WHERE e.n IS DISTINCT FROM 1 OR e.a <> r.amount
        OR (SELECT coalesce(sum(s.amount), 0) FROM debtor_settlements s JOIN debtor_ledger c ON c.id = s.charge_entry_id
             WHERE s.payment_entry_id = p.id AND c.entry_type <> 'RECEIPT_REVERSAL') <> 0;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('reversed receipts that still pay bills or are not put back exactly: %s', offending));
    END IF;

    SELECT string_agg(e.document_number, ', ') INTO offending
      FROM debtor_ledger e WHERE e.entry_type = 'RECEIPT_REVERSAL' AND NOT EXISTS (SELECT 1 FROM receipt_reversals v WHERE v.receipt_id = e.document_id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('reversal entries without a recorded reversal: %s', offending));
    END IF;

    -- 3. Cheques match their receipts; bounced, cancelled and replaced cheques are reversed, and only they.
    SELECT string_agg(q.number, ', ') INTO offending
      FROM cheques q JOIN debtor_receipts r ON r.id = q.receipt_id
      LEFT JOIN receipt_reversals v ON v.receipt_id = r.id
     WHERE q.amount <> r.amount OR q.kind <> r.method OR q.number <> r.reference OR q.debtor_id <> r.debtor_id
        OR (q.status IN ('BOUNCED', 'CANCELLED', 'REPLACED')) <> (v.id IS NOT NULL);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('cheques that do not match their receipt or status: %s', offending));
    END IF;

    SELECT string_agg(r.number, ', ') INTO offending
      FROM debtor_receipts r WHERE r.method IN ('CHEQUE', 'DEMAND_DRAFT') AND NOT EXISTS (SELECT 1 FROM cheques q WHERE q.receipt_id = r.id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('cheque receipts missing from the cheque register: %s', offending));
    END IF;

    -- 4. Handovers add up: expected is the round's cash receipts, declared and counted are their denomination counts.
    SELECT string_agg(s.id::text, ', ') INTO offending
      FROM collector_sessions s
     WHERE s.status <> 'OPEN'
       AND (s.expected_cash <> (SELECT coalesce(sum(amount), 0) FROM debtor_receipts r WHERE r.collector_session_id = s.id AND r.method = 'CASH')
            OR s.declared_cash <> (SELECT coalesce(sum(denomination * count), 0) FROM collector_session_counts c WHERE c.session_id = s.id AND c.kind = 'DECLARED')
            OR (s.status = 'CONFIRMED' AND s.counted_cash <> (SELECT coalesce(sum(denomination * count), 0) FROM collector_session_counts c WHERE c.session_id = s.id AND c.kind = 'COUNTED')));
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('collection rounds whose handover does not add up: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Custody verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Custody verification passed (% on %).', current_database(), version();
END
$$;
