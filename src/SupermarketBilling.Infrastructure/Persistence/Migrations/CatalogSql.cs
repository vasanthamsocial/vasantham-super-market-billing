namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class CatalogSql
{
    public static readonly string[] Tables =
    [
        "tax_registrations", "units", "categories", "brands", "customer_groups", "products",
        "product_variants", "variant_units", "variant_barcodes", "variant_mrps", "price_rules",
    ];

    /// <summary>
    /// A price rule's price and conditions are fixed once created; only its status may move
    /// (pending -> active/rejected, active/pending -> retired). Invoices reference rules by id, so a rule must
    /// always mean what it meant when an invoice used it.
    /// </summary>
    public const string PriceRuleGuard = """
        CREATE FUNCTION sb_price_rule_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Price rules cannot be deleted; retire them instead.' USING ERRCODE = 'restrict_violation';
            END IF;

            IF (NEW.business_id, NEW.variant_id, NEW.variant_unit_id, NEW.rate_type, NEW.channel, NEW.price, NEW.tax_inclusive,
                NEW.mrp, NEW.store_id, NEW.customer_group_id, NEW.members_only, NEW.min_quantity, NEW.max_quantity,
                NEW.valid_from_utc, NEW.valid_to_utc, NEW.priority, NEW.created_by_user_id, NEW.created_at_utc, NEW.tenant_id)
               IS DISTINCT FROM
               (OLD.business_id, OLD.variant_id, OLD.variant_unit_id, OLD.rate_type, OLD.channel, OLD.price, OLD.tax_inclusive,
                OLD.mrp, OLD.store_id, OLD.customer_group_id, OLD.members_only, OLD.min_quantity, OLD.max_quantity,
                OLD.valid_from_utc, OLD.valid_to_utc, OLD.priority, OLD.created_by_user_id, OLD.created_at_utc, OLD.tenant_id) THEN
                RAISE EXCEPTION 'A price rule''s price and conditions cannot be changed; create a new rule and retire this one.'
                    USING ERRCODE = 'restrict_violation';
            END IF;

            IF OLD.status IN ('REJECTED', 'RETIRED') AND NEW.status IS DISTINCT FROM OLD.status THEN
                RAISE EXCEPTION 'A % price rule cannot change status.', lower(OLD.status) USING ERRCODE = 'restrict_violation';
            END IF;

            IF OLD.status = 'ACTIVE' AND NEW.status NOT IN ('ACTIVE', 'RETIRED') THEN
                RAISE EXCEPTION 'An active price rule can only be retired.' USING ERRCODE = 'restrict_violation';
            END IF;

            RETURN NEW;
        END;
        $$;

        CREATE TRIGGER trg_price_rules_guard
            BEFORE UPDATE OR DELETE ON price_rules
            FOR EACH ROW EXECUTE FUNCTION sb_price_rule_guard();
        """;

    public const string DropPriceRuleGuard = """
        DROP TRIGGER IF EXISTS trg_price_rules_guard ON price_rules;
        DROP FUNCTION IF EXISTS sb_price_rule_guard();
        """;
}
