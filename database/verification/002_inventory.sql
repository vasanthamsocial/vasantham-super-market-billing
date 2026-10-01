-- Inventory verification: ledger protection and stock reconciliation. Run through scripts/verify-database.ps1.
-- Runs as the superuser (row-level security does not apply), so it reconciles every tenant.
-- Raises an exception (non-zero exit) when any check fails.
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.stock_ledger') IS NULL THEN
        RAISE NOTICE 'Inventory tables not present yet; skipping inventory verification.';
        RETURN;
    END IF;

    -- 1. The stock ledger and posted documents are append-only; cost layers only ever shrink.
    FOREACH trigger_name IN ARRAY ARRAY[
        'trg_stock_ledger_no_update_delete', 'trg_stock_ledger_no_truncate',
        'trg_stock_documents_no_update_delete', 'trg_stock_documents_no_truncate',
        'trg_cost_layers_guard', 'trg_cost_layers_no_truncate']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 2. Every business has inventory settings.
    SELECT string_agg(b.code, ', ') INTO offending
      FROM businesses b LEFT JOIN inventory_settings s ON s.business_id = b.id
     WHERE s.business_id IS NULL;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('businesses without inventory settings: %s', offending));
    END IF;

    -- 3. Each stock balance equals the sum of its ledger entries, and the last entry's running balance.
    SELECT string_agg(format('store %s variant %s: balance %s, ledger %s', x.store_id, x.variant_id, x.balance, x.ledger), '; ') INTO offending
      FROM (
        SELECT coalesce(b.store_id, l.store_id) AS store_id, coalesce(b.variant_id, l.variant_id) AS variant_id,
               coalesce(b.quantity, 0) AS balance, coalesce(l.total, 0) AS ledger
          FROM stock_balances b
          FULL JOIN (SELECT store_id, variant_id, sum(quantity) AS total FROM stock_ledger GROUP BY store_id, variant_id) l
            ON l.store_id = b.store_id AND l.variant_id = b.variant_id
      ) x
     WHERE x.balance <> x.ledger;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('stock balances not equal to their ledger: %s', offending));
    END IF;

    SELECT string_agg(format('store %s variant %s', b.store_id, b.variant_id), '; ') INTO offending
      FROM stock_balances b
      JOIN LATERAL (SELECT balance_after FROM stock_ledger l
                     WHERE l.store_id = b.store_id AND l.variant_id = b.variant_id
                     ORDER BY sequence DESC LIMIT 1) last ON true
     WHERE last.balance_after <> b.quantity;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('last ledger running balance differs from the balance: %s', offending));
    END IF;

    -- 4. Each cost layer's remainder is what came in, less what the ledger took from it, less shortfall it settled.
    SELECT string_agg(format('layer %s: remaining %s, expected %s', c.id, c.remaining_quantity,
                             c.original_quantity + coalesce(t.taken, 0) - c.settled_shortfall), '; ') INTO offending
      FROM cost_layers c
      LEFT JOIN (SELECT layer_id, sum(quantity) AS taken FROM stock_ledger WHERE layer_id IS NOT NULL AND quantity < 0 GROUP BY layer_id) t
        ON t.layer_id = c.id
     WHERE c.remaining_quantity <> c.original_quantity + coalesce(t.taken, 0) - c.settled_shortfall;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('cost layers out of step with the ledger: %s', offending));
    END IF;

    -- 5. Stock on the shelf (open layers) equals the positive part of each balance.
    SELECT string_agg(format('store %s variant %s: balance %s, layers %s', b.store_id, b.variant_id, b.quantity, coalesce(c.open, 0)), '; ') INTO offending
      FROM stock_balances b
      LEFT JOIN (SELECT store_id, variant_id, sum(remaining_quantity) AS open FROM cost_layers GROUP BY store_id, variant_id) c
        ON c.store_id = b.store_id AND c.variant_id = b.variant_id
     WHERE greatest(b.quantity, 0) <> coalesce(c.open, 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('open cost layers do not match balances: %s', offending));
    END IF;

    -- 6. Stock document numbers are gapless: each series has issued exactly as many numbers as there are documents.
    SELECT string_agg(format('store %s series %s: issued %s, documents %s', q.store_id, q.series, q.next_number - 1, coalesce(d.n, 0)), '; ') INTO offending
      FROM document_sequences q
      LEFT JOIN (SELECT store_id,
                        'STK-' || CASE type WHEN 'OPENING' THEN 'OPN' WHEN 'ADJUSTMENT' THEN 'ADJ' WHEN 'DAMAGE' THEN 'DMG'
                                            WHEN 'WASTAGE' THEN 'WST' WHEN 'TRANSFER' THEN 'TRF' WHEN 'COUNT' THEN 'CNT' END AS series,
                        count(*) AS n
                   FROM stock_documents GROUP BY 1, 2) d
        ON d.store_id = q.store_id AND d.series = q.series
     WHERE q.series LIKE 'STK-%' AND q.next_number - 1 <> coalesce(d.n, 0);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('stock document numbering has gaps: %s', offending));
    END IF;

    -- 7. Every ledger entry belongs to a posted document of the same business.
    SELECT string_agg(DISTINCT l.document_id::text, ', ') INTO offending
      FROM stock_ledger l LEFT JOIN stock_documents d ON d.id = l.document_id AND d.business_id = l.business_id
     WHERE l.document_type IN ('OPENING', 'ADJUSTMENT', 'DAMAGE', 'WASTAGE', 'TRANSFER', 'COUNT') AND d.id IS NULL;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('ledger entries without their stock document: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Inventory verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Inventory verification passed (% on %).', current_database(), version();
END
$$;
