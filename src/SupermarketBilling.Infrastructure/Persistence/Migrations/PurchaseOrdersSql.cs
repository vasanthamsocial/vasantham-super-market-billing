namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class PurchaseOrdersSql
{
    public static readonly string[] Tables = ["purchase_orders", "purchase_order_lines", "attachments"];

    /// <summary>Order lines and attached files never change once saved.</summary>
    public static readonly string[] AppendOnly = ["purchase_order_lines", "attachments"];

    /// <summary>An order is never deleted and its terms never change; it moves once from open to closed or cancelled.</summary>
    public const string OrderGuard = """
        CREATE FUNCTION sb_purchase_order_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Purchase orders cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.supplier_id, NEW.number, NEW.sequence_number, NEW.order_date,
                NEW.expected_date, NEW.notes, NEW.created_by_user_id, NEW.created_at_utc)
               IS DISTINCT FROM
               (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.supplier_id, OLD.number, OLD.sequence_number, OLD.order_date,
                OLD.expected_date, OLD.notes, OLD.created_by_user_id, OLD.created_at_utc) THEN
                RAISE EXCEPTION 'A purchase order''s terms cannot be changed.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.status <> 'OPEN' AND (NEW.status, NEW.closed_at_utc) IS DISTINCT FROM (OLD.status, OLD.closed_at_utc) THEN
                RAISE EXCEPTION 'A % purchase order cannot change.', lower(OLD.status) USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_purchase_orders_guard BEFORE UPDATE OR DELETE ON purchase_orders FOR EACH ROW EXECUTE FUNCTION sb_purchase_order_guard();
        CREATE TRIGGER trg_purchase_orders_no_truncate BEFORE TRUNCATE ON purchase_orders FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
        """;

    public const string DropOrderGuard = """
        DROP TRIGGER IF EXISTS trg_purchase_orders_no_truncate ON purchase_orders;
        DROP TRIGGER IF EXISTS trg_purchase_orders_guard ON purchase_orders;
        DROP FUNCTION IF EXISTS sb_purchase_order_guard();
        """;

    /// <summary>The goods-receipt guard, now also keeping the order a receipt was made against.</summary>
    public static readonly string GrnGuardWithOrder = GuardFunction(PurchasesSql.GrnGuard)
        .Replace("NEW.idempotency_key, NEW.request_hash)", "NEW.idempotency_key, NEW.request_hash, NEW.purchase_order_id)", StringComparison.Ordinal)
        .Replace("OLD.idempotency_key, OLD.request_hash)", "OLD.idempotency_key, OLD.request_hash, OLD.purchase_order_id)", StringComparison.Ordinal);

    public static readonly string GrnGuardWithoutOrder = GuardFunction(PurchasesSql.GrnGuard);

    /// <summary>The function part of a guard (its triggers already exist), as a replacement.</summary>
    private static string GuardFunction(string guard) =>
        guard[..guard.IndexOf("CREATE TRIGGER", StringComparison.Ordinal)].Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal);
}
