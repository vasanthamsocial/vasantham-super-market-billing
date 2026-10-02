-- Shift verification: drawer counts, expected cash and the documents in each shift. Run through
-- scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply), so it checks every tenant.
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.shifts') IS NULL THEN
        RAISE NOTICE 'Shift tables not present yet; skipping shift verification.';
        RETURN;
    END IF;

    -- 1. Guards are in place.
    FOREACH trigger_name IN ARRAY ARRAY[
        'trg_shifts_guard', 'trg_shifts_no_truncate', 'trg_shift_counts_no_update_delete', 'trg_cash_movements_no_update_delete',
        'trg_sales_invoices_open_shift', 'trg_sales_returns_open_shift', 'trg_cash_movements_open_shift']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 2. The opening float and the closing count are what was counted, note by note.
    SELECT string_agg(s.id::text, ', ') INTO offending
      FROM shifts s
      LEFT JOIN (SELECT shift_id, sum(denomination * count) FILTER (WHERE kind = 'OPENING') opening,
                        sum(denomination * count) FILTER (WHERE kind = 'CLOSING') closing
                   FROM shift_counts GROUP BY shift_id) c ON c.shift_id = s.id
     WHERE coalesce(c.opening, 0) <> s.opening_float
        OR (s.status = 'CLOSED' AND coalesce(c.closing, 0) <> s.counted_cash);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('shifts whose float or closing count differs from the notes counted: %s', offending));
    END IF;

    -- 3. A closed shift's expected cash is still what its documents and cash movements add up to.
    SELECT string_agg(format('%s: stored %s, recomputed %s', x.id, x.expected_cash, x.recomputed), '; ') INTO offending
      FROM (
        SELECT s.id, s.expected_cash,
               s.opening_float
               + coalesce((SELECT sum(p.amount) FROM sales_invoice_payments p JOIN sales_invoices i ON i.id = p.invoice_id
                            WHERE i.shift_id = s.id AND p.method = 'CASH'), 0)
               - coalesce((SELECT sum(i.change_due) FROM sales_invoices i WHERE i.shift_id = s.id), 0)
               - coalesce((SELECT sum(f.amount) FROM sales_return_refunds f JOIN sales_returns r ON r.id = f.return_id
                            WHERE r.shift_id = s.id AND f.method = 'CASH'), 0)
               + coalesce((SELECT sum(m.amount) FROM cash_movements m WHERE m.shift_id = s.id AND m.kind = 'PAY_IN'), 0)
               - coalesce((SELECT sum(m.amount) FROM cash_movements m WHERE m.shift_id = s.id AND m.kind IN ('PAY_OUT', 'DROP')), 0) AS recomputed
          FROM shifts s WHERE s.status = 'CLOSED'
      ) x
     WHERE x.expected_cash <> x.recomputed;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('closed shifts whose expected cash no longer matches their documents: %s', offending));
    END IF;

    -- 4. Every document in a shift is of that shift's counter and cashier, and was issued while it was open.
    SELECT string_agg(d.number, ', ') INTO offending
      FROM (SELECT number, shift_id, counter_id, cashier_user_id, issued_at_utc FROM sales_invoices WHERE shift_id IS NOT NULL
            UNION ALL
            SELECT number, shift_id, counter_id, cashier_user_id, issued_at_utc FROM sales_returns WHERE shift_id IS NOT NULL) d
      JOIN shifts s ON s.id = d.shift_id
     WHERE d.counter_id <> s.counter_id OR d.cashier_user_id <> s.cashier_user_id
        OR d.issued_at_utc < s.opened_at_utc OR (s.closed_at_utc IS NOT NULL AND d.issued_at_utc > s.closed_at_utc);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('documents outside their shift: %s', offending));
    END IF;

    -- 5. Pay-out approvals were used for a pay-out of their own counter's shift and cashier, within the amount.
    SELECT string_agg(a.id::text, ', ') INTO offending
      FROM supervisor_approvals a
      LEFT JOIN cash_movements m ON m.id = a.used_document_id
      LEFT JOIN shifts s ON s.id = m.shift_id
     WHERE a.kind = 'PAY_OUT' AND a.used_document_id IS NOT NULL
       AND (m.id IS NULL OR m.kind <> 'PAY_OUT' OR m.amount > a.max_amount OR s.counter_id <> a.counter_id OR m.recorded_by_user_id <> a.requested_by_user_id);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('pay-out approvals used outside their counter, cashier or amount: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Shift verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Shift verification passed (% on %).', current_database(), version();
END
$$;
