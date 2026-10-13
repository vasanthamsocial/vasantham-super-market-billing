namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

/// <summary>
/// Retention (D-044): removing an archived month from the store server. The removal runs only inside
/// <c>sb_remove_month</c>, owned by the schema owner: the runtime account cannot change locked or append-only records
/// itself. The function checks again, in the database, what does not depend on the application: the month is locked,
/// a receipt for this server's package of it is registered, earlier months went first, and nothing that stays refers
/// to it (<c>sb_retention_blockers</c>). The archive receipt's signature, the months always kept and the owner's
/// permission are checked by the application before it calls the function.
/// </summary>
internal static class RetentionSql
{
    public static readonly string[] Tables = ["month_removals", "month_removed_numbers"];

    /// <summary>The tables removal deletes from (in this order), whose guards it suspends for its own transaction only.</summary>
    public static readonly string[] Removed =
    [
        "credit_note_redemptions", "sales_return_refunds", "sales_return_lines", "sales_returns", "offline_bills", "sales_invoice_payments",
        "sales_invoice_lines", "sales_invoices", "purchase_return_lines", "purchase_returns", "grn_allocations", "grn_expenses", "grn_lines", "grns",
        "stock_documents", "stock_ledger", "audit_events",
    ];

    /// <summary>
    /// What stops a month from being removed: earlier months not removed yet, and records that stay and refer to it (or
    /// that its archive package does not hold). One row per kind, with a count and up to five examples.
    /// </summary>
    public const string Blockers = """
        CREATE FUNCTION sb_retention_blockers(p_business uuid, p_month date)
            RETURNS TABLE (code text, description text, count bigint, examples text[])
            LANGUAGE sql STABLE SET search_path = pg_catalog, public AS $$
        WITH m AS (SELECT p_month AS first, (p_month + interval '1 month')::date AS next),
        earlier AS (
            SELECT DISTINCT date_trunc('month', d)::date AS month FROM (
                SELECT business_date AS d FROM sales_invoices WHERE business_id = p_business AND business_date < p_month
                UNION ALL SELECT business_date FROM sales_returns WHERE business_id = p_business AND business_date < p_month
                UNION ALL SELECT business_date FROM grns WHERE business_id = p_business AND business_date < p_month
                UNION ALL SELECT business_date FROM purchase_returns WHERE business_id = p_business AND business_date < p_month
                UNION ALL SELECT business_date FROM stock_documents WHERE business_id = p_business AND business_date < p_month
                UNION ALL SELECT business_date FROM stock_ledger WHERE business_id = p_business AND business_date < p_month AND movement_type <> 'CARRIED_FORWARD'
                UNION ALL SELECT entry_date FROM debtor_ledger WHERE business_id = p_business AND entry_date < p_month
                UNION ALL SELECT entry_date FROM supplier_ledger WHERE business_id = p_business AND entry_date < p_month
                UNION ALL SELECT business_date FROM shifts WHERE business_id = p_business AND business_date < p_month) x
            WHERE NOT EXISTS (SELECT 1 FROM month_removals r WHERE r.business_id = p_business AND r.month = date_trunc('month', x.d)::date)),
        found AS (
            SELECT 'earlier_months' AS code, 'Earlier months with records must be removed first' AS description, count(*) AS count,
                   (array_agg(to_char(month, 'YYYY-MM') ORDER BY month))[1:5] AS examples
              FROM earlier
            UNION ALL
            SELECT 'later_returns', 'Credit notes of later months return bills of this month', count(*),
                   (array_agg(r.number || ' (' || r.business_date || ') returns ' || i.number ORDER BY r.business_date, r.number))[1:5]
              FROM sales_returns r JOIN sales_invoices i ON i.id = r.original_invoice_id, m
             WHERE i.business_id = p_business AND i.business_date >= m.first AND i.business_date < m.next AND r.business_date >= m.next
            UNION ALL
            SELECT 'later_store_credit', 'Store credit from this month''s credit notes was used on later bills', count(*),
                   (array_agg(r.number || ' used on ' || i.number ORDER BY i.number))[1:5]
              FROM credit_note_redemptions c JOIN sales_returns r ON r.id = c.return_id JOIN sales_invoices i ON i.id = c.invoice_id, m
             WHERE r.business_id = p_business AND r.business_date >= m.first AND r.business_date < m.next AND i.business_date >= m.next
            UNION ALL
            SELECT 'later_purchase_returns', 'Debit notes of later months return goods received this month', count(*),
                   (array_agg(p.number || ' (' || p.business_date || ') against ' || g.number ORDER BY p.business_date, p.number))[1:5]
              FROM purchase_returns p JOIN grns g ON g.id = p.grn_id, m
             WHERE g.business_id = p_business AND g.business_date >= m.first AND g.business_date < m.next AND p.business_date >= m.next
            UNION ALL
            SELECT 'deliveries', 'Bills of this month have delivery, packing or lorry records, which the archive package does not hold yet', count(*),
                   (array_agg(DISTINCT i.number))[1:5]
              FROM sales_invoices i, m
             WHERE i.business_id = p_business AND i.business_date >= m.first AND i.business_date < m.next
               AND (EXISTS (SELECT 1 FROM invoice_fulfilments f WHERE f.invoice_id = i.id)
                    OR EXISTS (SELECT 1 FROM packing_challans c WHERE c.invoice_id = i.id)
                    OR EXISTS (SELECT 1 FROM consignment_invoices c WHERE c.invoice_id = i.id))
            UNION ALL
            SELECT 'attachments', 'Goods receipts of this month have attached files, which the archive package does not hold', count(*),
                   (array_agg(DISTINCT g.number))[1:5]
              FROM attachments a JOIN grns g ON g.id = a.owner_id, m
             WHERE g.business_id = p_business AND g.business_date >= m.first AND g.business_date < m.next)
        SELECT code, description, count, examples FROM found WHERE count > 0
        $$;
        """;

    /// <summary>
    /// Removes a month: checks the database's gates again, records what goes (and the document numbers it takes out of
    /// each series), replaces the month's stock movements by one carried-forward total per store, item, batch, cost layer
    /// and direction (the same quantities and values, the latest running balance), deletes the month's documents, their
    /// lines and its audit trail, and records the removal. All in the caller's transaction: any failure undoes all of it.
    /// </summary>
    public static string Remove()
    {
        var disable = string.Join("\n", Removed.Select(t => $"    ALTER TABLE {t} DISABLE TRIGGER USER;"));
        var enable = string.Join("\n", Removed.Select(t => $"    ALTER TABLE {t} ENABLE TRIGGER USER;"));
        return $$"""
            CREATE FUNCTION sb_remove_month(p_business uuid, p_month date, p_removal uuid, p_user uuid) RETURNS jsonb
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, pg_temp AS $$
            DECLARE
                next_month date := (p_month + interval '1 month')::date;
                t_from timestamptz := p_month::timestamp AT TIME ZONE 'Asia/Kolkata';
                t_to timestamptz := next_month::timestamp AT TIME ZONE 'Asia/Kolkata';
                tenant uuid := sb_current_tenant();
                receipt uuid;
                problem record;
                counts jsonb := '{}'::jsonb;
                n bigint;
            BEGIN
                IF tenant IS NULL OR NOT EXISTS (SELECT 1 FROM businesses b WHERE b.id = p_business AND b.tenant_id = tenant) THEN
                    RAISE EXCEPTION 'The business is not this tenant''s.' USING ERRCODE = 'insufficient_privilege';
                END IF;
                IF date_trunc('month', p_month)::date <> p_month THEN
                    RAISE EXCEPTION 'Give the first day of the month.' USING ERRCODE = 'invalid_parameter_value';
                END IF;
                IF EXISTS (SELECT 1 FROM month_removals r WHERE r.business_id = p_business AND r.month = p_month) THEN
                    RAISE EXCEPTION 'The month % was already removed.', to_char(p_month, 'YYYY-MM') USING ERRCODE = 'restrict_violation';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM month_locks l WHERE l.business_id = p_business AND l.month = p_month) THEN
                    RAISE EXCEPTION 'The month % is not locked.', to_char(p_month, 'YYYY-MM') USING ERRCODE = 'restrict_violation';
                END IF;
                SELECT r.id INTO receipt
                  FROM month_removal_receipts r
                  JOIN month_packages p ON p.id = r.package_id AND p.business_id = r.business_id AND p.month = r.month AND p.file_sha256 = r.file_sha256
                 WHERE r.business_id = p_business AND r.month = p_month;
                IF receipt IS NULL THEN
                    RAISE EXCEPTION 'No archive receipt for this server''s package of %.', to_char(p_month, 'YYYY-MM') USING ERRCODE = 'restrict_violation';
                END IF;
                FOR problem IN SELECT * FROM sb_retention_blockers(p_business, p_month) LOOP
                    RAISE EXCEPTION 'The month % cannot be removed: %.', to_char(p_month, 'YYYY-MM'), problem.description USING ERRCODE = 'restrict_violation';
                END LOOP;

                -- What goes: the month's documents (by their business date) and what hangs on them.
                DROP TABLE IF EXISTS sb_rm_invoices, sb_rm_returns, sb_rm_grns, sb_rm_purchase_returns, sb_rm_numbers, sb_rm_carry;
                CREATE TEMP TABLE sb_rm_invoices ON COMMIT DROP AS
                    SELECT id, store_id, number_prefix, sequence_number FROM sales_invoices
                     WHERE business_id = p_business AND business_date >= p_month AND business_date < next_month;
                CREATE TEMP TABLE sb_rm_returns ON COMMIT DROP AS
                    SELECT id, store_id, number_prefix, sequence_number FROM sales_returns
                     WHERE business_id = p_business AND business_date >= p_month AND business_date < next_month;
                CREATE TEMP TABLE sb_rm_grns ON COMMIT DROP AS
                    SELECT id, store_id, sequence_number FROM grns WHERE business_id = p_business AND business_date >= p_month AND business_date < next_month;
                CREATE TEMP TABLE sb_rm_purchase_returns ON COMMIT DROP AS
                    SELECT id, store_id, sequence_number FROM purchase_returns
                     WHERE business_id = p_business AND business_date >= p_month AND business_date < next_month;

                -- The numbers taken out of each series, so numbering stays gapless counting them.
                CREATE TEMP TABLE sb_rm_numbers ON COMMIT DROP AS
                    SELECT store_id, 'INV-' || number_prefix AS series, count(*)::int AS count, max(sequence_number)::bigint AS last_number
                      FROM sb_rm_invoices GROUP BY store_id, number_prefix
                    UNION ALL
                    SELECT store_id, 'CN-' || number_prefix, count(*)::int, max(sequence_number)::bigint FROM sb_rm_returns GROUP BY store_id, number_prefix
                    UNION ALL
                    SELECT store_id, 'GRN', count(*)::int, max(sequence_number)::bigint FROM sb_rm_grns GROUP BY store_id
                    UNION ALL
                    SELECT store_id, 'DN', count(*)::int, max(sequence_number)::bigint FROM sb_rm_purchase_returns GROUP BY store_id
                    UNION ALL
                    SELECT store_id, 'STK-' || CASE type WHEN 'OPENING' THEN 'OPN' WHEN 'ADJUSTMENT' THEN 'ADJ' WHEN 'DAMAGE' THEN 'DMG'
                                                         WHEN 'WASTAGE' THEN 'WST' WHEN 'TRANSFER' THEN 'TRF' WHEN 'COUNT' THEN 'CNT' END,
                           count(*)::int, count(*)::bigint
                      FROM stock_documents WHERE business_id = p_business AND business_date >= p_month AND business_date < next_month
                     GROUP BY store_id, type
                    UNION ALL
                    SELECT store_id, 'OFFLINE-' || number_prefix, count(*)::int, max(sequence)::bigint
                      FROM offline_bills WHERE invoice_id IN (SELECT id FROM sb_rm_invoices) GROUP BY store_id, number_prefix;

                -- Stock movements become one total per store, item, batch, cost layer and direction: the same quantity and
                -- value, at the latest movement's sequence with its running balance (so later balances still chain).
                CREATE TEMP TABLE sb_rm_carry ON COMMIT DROP AS
                    SELECT store_id, variant_id, batch_id, layer_id, sum(quantity) AS quantity, sum(value) AS value, max(sequence) AS sequence,
                           (array_agg(balance_after ORDER BY sequence DESC))[1] AS balance_after, max(occurred_at_utc) AS occurred_at_utc,
                           max(business_date) AS business_date, tenant_id
                      FROM stock_ledger
                     WHERE business_id = p_business AND business_date >= p_month AND business_date < next_month
                     GROUP BY store_id, variant_id, batch_id, layer_id, sign(quantity), tenant_id;

            {{disable}}

                DELETE FROM credit_note_redemptions WHERE invoice_id IN (SELECT id FROM sb_rm_invoices) OR return_id IN (SELECT id FROM sb_rm_returns);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('credit_note_redemptions', n);
                DELETE FROM sales_return_refunds WHERE return_id IN (SELECT id FROM sb_rm_returns);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('sales_return_refunds', n);
                DELETE FROM sales_return_lines WHERE return_id IN (SELECT id FROM sb_rm_returns);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('sales_return_lines', n);
                DELETE FROM sales_returns WHERE id IN (SELECT id FROM sb_rm_returns);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('sales_returns', n);
                DELETE FROM offline_bills WHERE invoice_id IN (SELECT id FROM sb_rm_invoices);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('offline_bills', n);
                DELETE FROM sales_invoice_payments WHERE invoice_id IN (SELECT id FROM sb_rm_invoices);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('sales_invoice_payments', n);
                DELETE FROM sales_invoice_lines WHERE invoice_id IN (SELECT id FROM sb_rm_invoices);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('sales_invoice_lines', n);
                DELETE FROM sales_invoices WHERE id IN (SELECT id FROM sb_rm_invoices);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('sales_invoices', n);
                DELETE FROM purchase_return_lines WHERE purchase_return_id IN (SELECT id FROM sb_rm_purchase_returns);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('purchase_return_lines', n);
                DELETE FROM purchase_returns WHERE id IN (SELECT id FROM sb_rm_purchase_returns);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('purchase_returns', n);
                DELETE FROM grn_allocations WHERE expense_id IN (SELECT e.id FROM grn_expenses e WHERE e.grn_id IN (SELECT id FROM sb_rm_grns));
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('grn_allocations', n);
                DELETE FROM grn_expenses WHERE grn_id IN (SELECT id FROM sb_rm_grns);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('grn_expenses', n);
                DELETE FROM grn_lines WHERE grn_id IN (SELECT id FROM sb_rm_grns);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('grn_lines', n);
                DELETE FROM grns WHERE id IN (SELECT id FROM sb_rm_grns);
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('grns', n);
                DELETE FROM stock_documents WHERE business_id = p_business AND business_date >= p_month AND business_date < next_month;
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('stock_documents', n);
                DELETE FROM stock_ledger WHERE business_id = p_business AND business_date >= p_month AND business_date < next_month;
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('stock_ledger', n);
                INSERT INTO stock_ledger (id, sequence, business_id, store_id, variant_id, batch_id, layer_id, movement_type, quantity, unit_cost, value,
                                          balance_after, document_type, document_id, business_date, occurred_at_utc, created_by_user_id, tenant_id)
                    OVERRIDING SYSTEM VALUE
                    SELECT gen_random_uuid(), c.sequence, p_business, c.store_id, c.variant_id, c.batch_id, c.layer_id, 'CARRIED_FORWARD', c.quantity, 0, c.value,
                           c.balance_after, 'RETENTION', p_removal, c.business_date, c.occurred_at_utc, p_user, c.tenant_id
                      FROM sb_rm_carry c;
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('stock_carried_forward', n);
                DELETE FROM audit_events WHERE business_id = p_business AND occurred_at_utc >= t_from AND occurred_at_utc < t_to;
                GET DIAGNOSTICS n = ROW_COUNT; counts := counts || jsonb_build_object('audit_events', n);

            {{enable}}

                INSERT INTO month_removals (id, business_id, month, receipt_id, removed_by_user_id, removed_at_utc, counts, tenant_id)
                    VALUES (p_removal, p_business, p_month, receipt, p_user, now(), counts, tenant);
                INSERT INTO month_removed_numbers (removal_id, business_id, store_id, series, count, last_number, tenant_id)
                    SELECT p_removal, p_business, store_id, series, sum(count), max(last_number), tenant FROM sb_rm_numbers GROUP BY store_id, series;
                RETURN counts;
            END;
            $$;
            REVOKE ALL ON FUNCTION sb_remove_month(uuid, date, uuid, uuid) FROM PUBLIC;
            """;
    }

    /// <summary>The numbers removed per store and series (zero rows for a series nothing was removed from).</summary>
    public const string RemovedNumbersView = """
        CREATE VIEW sb_removed_numbers WITH (security_invoker = true) AS
            SELECT store_id, series, sum(count)::bigint AS count, max(last_number) AS last_number FROM month_removed_numbers GROUP BY store_id, series;
        """;

    public const string Drop = """
        DROP VIEW IF EXISTS sb_removed_numbers;
        DROP FUNCTION IF EXISTS sb_remove_month(uuid, date, uuid, uuid);
        DROP FUNCTION IF EXISTS sb_retention_blockers(uuid, date);
        """;
}
