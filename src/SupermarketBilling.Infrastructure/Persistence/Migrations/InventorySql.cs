namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class InventorySql
{
    public static readonly string[] Tables =
    [
        "inventory_settings", "negative_stock_rules", "batches", "cost_layers", "stock_balances",
        "stock_ledger", "stock_documents", "reorder_levels", "document_sequences",
    ];

    /// <summary>Every business that existed before inventory starts on FIFO.</summary>
    public const string SeedSettings = """
        INSERT INTO inventory_settings (business_id, tenant_id, valuation_method)
        SELECT id, tenant_id, 'FIFO' FROM businesses
        ON CONFLICT (business_id) DO NOTHING;
        """;

    /// <summary>
    /// A cost layer's origin (what came in, when, at what cost) is fixed; only what is left of it may go down, and
    /// only as shortfall is settled. Layers are never deleted, so the ledger can always be reconciled against them.
    /// </summary>
    public const string CostLayerGuard = """
        CREATE FUNCTION sb_cost_layer_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Cost layers cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;

            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.variant_id, NEW.batch_id, NEW.expires_on, NEW.unit_cost,
                NEW.original_quantity, NEW.received_at_utc, NEW.sequence)
               IS DISTINCT FROM
               (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.variant_id, OLD.batch_id, OLD.expires_on, OLD.unit_cost,
                OLD.original_quantity, OLD.received_at_utc, OLD.sequence) THEN
                RAISE EXCEPTION 'A cost layer''s origin cannot be changed.' USING ERRCODE = 'restrict_violation';
            END IF;

            IF NEW.remaining_quantity > OLD.remaining_quantity OR NEW.settled_shortfall < OLD.settled_shortfall THEN
                RAISE EXCEPTION 'Stock cannot be put back into a cost layer; post a new receipt instead.' USING ERRCODE = 'restrict_violation';
            END IF;

            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_cost_layers_guard BEFORE UPDATE OR DELETE ON cost_layers
            FOR EACH ROW EXECUTE FUNCTION sb_cost_layer_guard();
        CREATE TRIGGER trg_cost_layers_no_truncate BEFORE TRUNCATE ON cost_layers
            FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
        """;

    public const string DropCostLayerGuard = """
        DROP TRIGGER IF EXISTS trg_cost_layers_no_truncate ON cost_layers;
        DROP TRIGGER IF EXISTS trg_cost_layers_guard ON cost_layers;
        DROP FUNCTION IF EXISTS sb_cost_layer_guard();
        """;
}
