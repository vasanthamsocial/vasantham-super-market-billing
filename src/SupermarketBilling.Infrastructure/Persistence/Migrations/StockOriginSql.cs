namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class StockOriginSql
{
    /// <summary>
    /// Lots received on a goods receipt take their origin from its classification. Other lots stay OTHER: opening stock,
    /// adjustments and counts have no purchase document, and lots moved by a transfer or a customer return before this
    /// change cannot be traced reliably to the lot they came from.
    /// </summary>
    public const string Backfill = """
        UPDATE cost_layers l
           SET origin = CASE g.classification
                            WHEN 'GST_TAX_INVOICE' THEN 'GST' WHEN 'IMPORT' THEN 'GST' WHEN 'REVERSE_CHARGE' THEN 'GST'
                            WHEN 'BILL_OF_SUPPLY' THEN 'NON_GST' WHEN 'UNREGISTERED' THEN 'NON_GST'
                            ELSE 'OTHER' END
          FROM stock_ledger e
          JOIN grns g ON g.id = e.document_id
         WHERE e.layer_id = l.id AND e.movement_type = 'RECEIPT' AND e.document_type = 'GRN';
        """;

    /// <summary>The cost layer guard, now also keeping the origin fixed.</summary>
    public static string Guard(bool withOrigin) => $$"""
        CREATE OR REPLACE FUNCTION sb_cost_layer_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Cost layers cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;

            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.variant_id, NEW.batch_id, NEW.expires_on, NEW.unit_cost,
                NEW.original_quantity, NEW.received_at_utc, NEW.sequence{{(withOrigin ? ", NEW.origin" : string.Empty)}})
               IS DISTINCT FROM
               (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.variant_id, OLD.batch_id, OLD.expires_on, OLD.unit_cost,
                OLD.original_quantity, OLD.received_at_utc, OLD.sequence{{(withOrigin ? ", OLD.origin" : string.Empty)}}) THEN
                RAISE EXCEPTION 'A cost layer''s origin cannot be changed.' USING ERRCODE = 'restrict_violation';
            END IF;

            IF NEW.remaining_quantity > OLD.remaining_quantity OR NEW.settled_shortfall < OLD.settled_shortfall THEN
                RAISE EXCEPTION 'Stock cannot be put back into a cost layer; post a new receipt instead.' USING ERRCODE = 'restrict_violation';
            END IF;

            RETURN NEW;
        END;
        $$;
        """;
}
