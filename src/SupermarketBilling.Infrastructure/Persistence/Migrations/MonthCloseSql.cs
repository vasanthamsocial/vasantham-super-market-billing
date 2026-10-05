namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class MonthCloseSql
{
    public static readonly string[] Tables = ["month_locks", "month_packages", "archive_recipients"];

    public static readonly string[] AppendOnly = ["month_locks", "month_packages"];

    /// <summary>Every dated record a month lock protects, and the column that dates it.</summary>
    public static readonly (string Table, string Column)[] Dated =
    [
        ("sales_invoices", "business_date"), ("sales_returns", "business_date"), ("grns", "business_date"), ("purchase_returns", "business_date"),
        ("stock_documents", "business_date"), ("stock_ledger", "business_date"), ("debtor_ledger", "entry_date"), ("supplier_ledger", "entry_date"),
        ("debtor_receipts", "receipt_date"), ("supplier_payments", "payment_date"), ("shifts", "business_date"), ("collector_sessions", "business_date"),
        ("cheque_events", "event_date"),
    ];

    /// <summary>
    /// A locked month is closed for good: nothing dated in it can be added, changed or removed, in any of the tables
    /// that record business (the lock itself is append-only).
    /// </summary>
    public static string Guards()
    {
        var sql = new System.Text.StringBuilder("""
            CREATE FUNCTION sb_month_lock_guard() RETURNS trigger
            LANGUAGE plpgsql AS $$
            DECLARE
                day date;
                business uuid;
            BEGIN
                IF TG_OP IN ('UPDATE', 'DELETE') THEN
                    EXECUTE format('SELECT ($1).%I, ($1).business_id', TG_ARGV[0]) USING OLD INTO day, business;
                    IF EXISTS (SELECT 1 FROM month_locks l WHERE l.business_id = business AND l.month = date_trunc('month', day)::date) THEN
                        RAISE EXCEPTION 'The month of % is locked: its records cannot be changed or removed.', day USING ERRCODE = 'restrict_violation';
                    END IF;
                END IF;
                IF TG_OP IN ('INSERT', 'UPDATE') THEN
                    EXECUTE format('SELECT ($1).%I, ($1).business_id', TG_ARGV[0]) USING NEW INTO day, business;
                    IF EXISTS (SELECT 1 FROM month_locks l WHERE l.business_id = business AND l.month = date_trunc('month', day)::date) THEN
                        RAISE EXCEPTION 'The month of % is locked: nothing dated in it can be recorded.', day USING ERRCODE = 'restrict_violation';
                    END IF;
                END IF;
                RETURN COALESCE(NEW, OLD);
            END;
            $$;

            """);
        foreach (var (table, column) in Dated)
        {
            sql.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"CREATE TRIGGER trg_{table}_month_lock BEFORE INSERT OR UPDATE OR DELETE ON {table} FOR EACH ROW EXECUTE FUNCTION sb_month_lock_guard('{column}');\n");
        }

        return sql.ToString();
    }

    public static string DropGuards()
    {
        var sql = new System.Text.StringBuilder();
        foreach (var (table, _) in Dated)
        {
            sql.Append(System.Globalization.CultureInfo.InvariantCulture, $"DROP TRIGGER IF EXISTS trg_{table}_month_lock ON {table};\n");
        }

        return sql.Append("DROP FUNCTION IF EXISTS sb_month_lock_guard();\n").ToString();
    }
}
