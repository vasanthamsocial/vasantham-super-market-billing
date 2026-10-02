-- Account verification: supplier and debtor ledgers chain correctly, settlements never exceed what they settle, and
-- every posted receipt and supplier payment is in its supplier's ledger exactly once.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
    ledger text;
    party text;
    settlements text;
BEGIN
    IF to_regclass('public.supplier_ledger') IS NULL THEN
        RAISE NOTICE 'Account tables not present yet; skipping account verification.';
        RETURN;
    END IF;

    FOREACH trigger_name IN ARRAY ARRAY[
        'trg_supplier_ledger_chain', 'trg_debtor_ledger_chain', 'trg_supplier_settlements_guard', 'trg_debtor_settlements_guard', 'trg_debtors_guard',
        'trg_supplier_ledger_no_update_delete', 'trg_debtor_ledger_no_update_delete', 'trg_supplier_settlements_no_update_delete',
        'trg_debtor_settlements_no_update_delete', 'trg_supplier_payments_no_update_delete']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    FOR ledger, party, settlements IN SELECT * FROM (VALUES ('supplier_ledger', 'supplier_id', 'supplier_settlements'), ('debtor_ledger', 'debtor_id', 'debtor_settlements')) v
    LOOP
        -- 1. Entries of each account are numbered 1, 2, 3... and each balance is the running total.
        EXECUTE format(
            'SELECT string_agg(DISTINCT %2$I::text, '', '') FROM (
                 SELECT %2$I, sequence, balance_after,
                        row_number() OVER (PARTITION BY %2$I ORDER BY sequence) AS expected_sequence,
                        sum(amount) OVER (PARTITION BY %2$I ORDER BY sequence ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS expected_balance
                   FROM %1$I) x
              WHERE sequence <> expected_sequence OR balance_after <> expected_balance', ledger, party) INTO offending;
        IF offending IS NOT NULL THEN
            failures := array_append(failures, format('%s: accounts whose entries do not chain: %s', ledger, offending));
        END IF;

        -- 2. Settlements apply payments to charges of the same account and never exceed either.
        EXECUTE format(
            'SELECT string_agg(s.id::text, '', '') FROM %1$I s
               JOIN %2$I c ON c.id = s.charge_entry_id JOIN %2$I p ON p.id = s.payment_entry_id
              WHERE c.amount <= 0 OR p.amount >= 0 OR c.%3$I <> s.%3$I OR p.%3$I <> s.%3$I', settlements, ledger, party) INTO offending;
        IF offending IS NOT NULL THEN
            failures := array_append(failures, format('%s: settlements not applying a payment to a charge of the same account: %s', settlements, offending));
        END IF;

        EXECUTE format(
            'SELECT string_agg(e.id::text, '', '') FROM %2$I e
               LEFT JOIN (SELECT charge_entry_id id, sum(amount) a FROM %1$I GROUP BY charge_entry_id) c ON c.id = e.id
               LEFT JOIN (SELECT payment_entry_id id, sum(amount) a FROM %1$I GROUP BY payment_entry_id) p ON p.id = e.id
              WHERE coalesce(c.a, 0) > greatest(e.amount, 0) OR coalesce(p.a, 0) > greatest(-e.amount, 0)', settlements, ledger) INTO offending;
        IF offending IS NOT NULL THEN
            failures := array_append(failures, format('%s: entries settled beyond their amount: %s', ledger, offending));
        END IF;
    END LOOP;

    -- 3. Every posted receipt is owed to its supplier exactly once, for its invoice total; nothing else is.
    SELECT string_agg(g.number, ', ') INTO offending
      FROM grns g
      LEFT JOIN (SELECT document_id, count(*) n, sum(amount) a, min(supplier_id::text) s FROM supplier_ledger WHERE entry_type = 'GRN' GROUP BY document_id) l
        ON l.document_id = g.id
     WHERE (g.status = 'POSTED' AND g.invoice_total > 0 AND (l.n IS DISTINCT FROM 1 OR l.a <> g.invoice_total OR l.s <> g.supplier_id::text))
        OR ((g.status <> 'POSTED' OR g.invoice_total <= 0) AND l.n IS NOT NULL);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('goods receipts not owed exactly once to their supplier: %s', offending));
    END IF;

    -- 4. Every supplier payment is in its supplier's ledger exactly once, for its amount.
    SELECT string_agg(p.number, ', ') INTO offending
      FROM supplier_payments p
      LEFT JOIN (SELECT document_id, count(*) n, sum(amount) a, min(supplier_id::text) s FROM supplier_ledger WHERE entry_type = 'PAYMENT' GROUP BY document_id) l
        ON l.document_id = p.id
     WHERE l.n IS DISTINCT FROM 1 OR l.a <> -p.amount OR l.s <> p.supplier_id::text;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('supplier payments not in the ledger exactly once: %s', offending));
    END IF;

    -- 5. Payment numbering per store is gapless; closed debtor accounts are at zero.
    SELECT string_agg(format('store %s: issued %s, payments %s', q.store_id, q.next_number - 1, coalesce(p.n, 0)), '; ') INTO offending
      FROM document_sequences q
      LEFT JOIN (SELECT store_id, count(*) n FROM supplier_payments GROUP BY store_id) p ON p.store_id = q.store_id
     WHERE q.series = 'PMT' AND q.next_number - 1 <> coalesce(p.n, 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('supplier payment numbering has gaps: %s', offending));
    END IF;

    SELECT string_agg(d.code, ', ') INTO offending
      FROM debtors d WHERE d.status = 'CLOSED' AND (SELECT coalesce(sum(amount), 0) FROM debtor_ledger l WHERE l.debtor_id = d.id) <> 0;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('closed debtor accounts with a balance: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Account verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Account verification passed (% on %).', current_database(), version();
END
$$;
