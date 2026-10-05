-- Dispatch verification: every dispatch carries bills of one customer from its own store, delivered its way; lorry
-- bookings use the right offices; routes run from a booking office to a destination.
-- Run through scripts/verify-database.ps1. Runs as the superuser (row-level security does not apply).
DO $$
DECLARE
    failures text[] := ARRAY[]::text[];
    offending text;
    trigger_name text;
BEGIN
    IF to_regclass('public.consignments') IS NULL THEN
        RAISE NOTICE 'Dispatch tables not present yet; skipping dispatch verification.';
        RETURN;
    END IF;

    FOREACH trigger_name IN ARRAY ARRAY['trg_consignments_guard', 'trg_consignment_invoices_guard', 'trg_invoice_fulfilments_guard',
                                        'trg_consignment_invoices_no_update_delete']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = trigger_name AND NOT tgisinternal AND tgenabled = 'O') THEN
            failures := array_append(failures, format('trigger %s is missing or disabled', trigger_name));
        END IF;
    END LOOP;

    -- 1. Every dispatch has bills, all of its store and business, delivered its way, of one customer.
    SELECT string_agg(c.number, ', ') INTO offending
      FROM consignments c
     WHERE NOT EXISTS (SELECT 1 FROM consignment_invoices ci WHERE ci.consignment_id = c.id)
        OR EXISTS (SELECT 1 FROM consignment_invoices ci
                     JOIN invoice_fulfilments f ON f.invoice_id = ci.invoice_id
                     JOIN sales_invoices i ON i.id = ci.invoice_id
                    WHERE ci.consignment_id = c.id
                      AND (f.mode <> c.mode OR f.store_id <> c.store_id OR i.store_id <> c.store_id OR i.business_id <> c.business_id
                           OR f.transporter_id IS DISTINCT FROM c.transporter_id))
        OR (SELECT count(DISTINCT coalesce(i.debtor_id::text, lower(coalesce(i.buyer_name, '')))) FROM consignment_invoices ci
              JOIN sales_invoices i ON i.id = ci.invoice_id WHERE ci.consignment_id = c.id) > 1;
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('dispatches with bills that do not belong together: %s', offending));
    END IF;

    -- 2-3. A bill can go in several dispatches (in parts) since Stage 11b; what each carries and its value are checked
    --      line by line in 015_packing.sql.

    -- 4. Lorry bookings: from a booking office to a destination branch of the same lorry service.
    SELECT string_agg(c.number, ', ') INTO offending
      FROM consignments c
     WHERE c.mode = 'LORRY'
       AND (NOT EXISTS (SELECT 1 FROM transporter_branches b WHERE b.id = c.booking_branch_id AND b.transporter_id = c.transporter_id AND b.is_booking_office)
            OR NOT EXISTS (SELECT 1 FROM transporter_branches b WHERE b.id = c.destination_branch_id AND b.transporter_id = c.transporter_id AND b.is_destination));
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('lorry dispatches with the wrong offices: %s', offending));
    END IF;

    -- 5. Routes run from a booking office to a destination branch.
    SELECT string_agg(r.id::text, ', ') INTO offending
      FROM transporter_routes r
     WHERE NOT EXISTS (SELECT 1 FROM transporter_branches b WHERE b.id = r.from_branch_id AND b.is_booking_office)
        OR NOT EXISTS (SELECT 1 FROM transporter_branches b WHERE b.id = r.to_branch_id AND b.is_destination);
    IF offending IS NOT NULL THEN
        failures := array_append(failures, format('routes not from a booking office to a destination: %s', offending));
    END IF;

    IF array_length(failures, 1) > 0 THEN
        RAISE EXCEPTION E'Dispatch verification FAILED:\n  - %', array_to_string(failures, E'\n  - ');
    END IF;
    RAISE NOTICE 'Dispatch verification passed (% on %).', current_database(), version();
END
$$;
