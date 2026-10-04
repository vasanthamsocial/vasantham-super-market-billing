namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class CustodySql
{
    public static readonly string[] Tables = ["collector_sessions", "collector_session_counts", "cheques", "cheque_events", "receipt_reversals", "visit_outcomes"];

    public static readonly string[] AppendOnly = ["collector_session_counts", "cheque_events", "receipt_reversals", "visit_outcomes"];

    /// <summary>
    /// Settlements may now take back an earlier settlement of the same payment and charge (a negative amount, when the
    /// payment is reversed), never more than that pair settled. Positive settlements are checked as before.
    /// </summary>
    public static string SettlementGuardWithUndo(string table, string ledger) => $$"""
        CREATE OR REPLACE FUNCTION sb_{{table}}_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE
            charge numeric;
            payment numeric;
        BEGIN
            SELECT amount INTO charge FROM {{ledger}} WHERE id = NEW.charge_entry_id FOR UPDATE;
            SELECT amount INTO payment FROM {{ledger}} WHERE id = NEW.payment_entry_id FOR UPDATE;
            IF charge IS NULL OR charge <= 0 OR payment IS NULL OR payment >= 0 THEN
                RAISE EXCEPTION 'A settlement applies a payment to a charge.' USING ERRCODE = 'check_violation';
            END IF;
            IF NEW.amount < 0 THEN
                IF (SELECT coalesce(sum(amount), 0) FROM {{table}} WHERE charge_entry_id = NEW.charge_entry_id AND payment_entry_id = NEW.payment_entry_id) + NEW.amount < 0 THEN
                    RAISE EXCEPTION 'A settlement can only be taken back as far as it was made.' USING ERRCODE = 'check_violation';
                END IF;
                RETURN NEW;
            END IF;
            IF (SELECT coalesce(sum(amount), 0) FROM {{table}} WHERE charge_entry_id = NEW.charge_entry_id) + NEW.amount > charge
               OR (SELECT coalesce(sum(amount), 0) FROM {{table}} WHERE payment_entry_id = NEW.payment_entry_id) + NEW.amount > -payment THEN
                RAISE EXCEPTION 'A settlement cannot exceed what is left of the charge or the payment.' USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        """;

    /// <summary>The guard as it was before (positive settlements only), for rolling back.</summary>
    public static string OriginalSettlementGuard(string table, string ledger)
    {
        var full = AccountsSql.SettlementGuard(table, ledger);
        return full[..full.IndexOf("CREATE TRIGGER", StringComparison.Ordinal)].Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal);
    }

    /// <summary>A field receipt belongs to the open round of the collector who took it.</summary>
    public const string ReceiptRoundGuard = """
        CREATE FUNCTION sb_debtor_receipt_round() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE
            s collector_sessions%ROWTYPE;
        BEGIN
            IF NEW.collector_session_id IS NULL THEN
                RETURN NEW;
            END IF;
            SELECT * INTO s FROM collector_sessions WHERE id = NEW.collector_session_id;
            IF s.status IS DISTINCT FROM 'OPEN' OR s.collector_user_id <> NEW.cashier_user_id OR s.store_id <> NEW.store_id THEN
                RAISE EXCEPTION 'A field receipt must be in the collector''s own open round.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_debtor_receipts_round BEFORE INSERT ON debtor_receipts FOR EACH ROW EXECUTE FUNCTION sb_debtor_receipt_round();
        """;

    /// <summary>A round only moves forward (open, handed over, confirmed); what was recorded at each step never changes; rounds are never deleted.</summary>
    public const string SessionGuard = """
        CREATE FUNCTION sb_collector_session_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Collection rounds cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.collector_user_id, NEW.business_date, NEW.opened_at_utc)
               IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.collector_user_id, OLD.business_date, OLD.opened_at_utc)
               OR NOT ((OLD.status = 'OPEN' AND NEW.status = 'HANDED_OVER') OR (OLD.status = 'HANDED_OVER' AND NEW.status = 'CONFIRMED'))
               OR (OLD.status = 'HANDED_OVER' AND (NEW.expected_cash, NEW.declared_cash, NEW.handed_over_at_utc)
                                                  IS DISTINCT FROM (OLD.expected_cash, OLD.declared_cash, OLD.handed_over_at_utc)) THEN
                RAISE EXCEPTION 'A collection round can only move forward, once per step.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_collector_sessions_guard BEFORE UPDATE OR DELETE ON collector_sessions FOR EACH ROW EXECUTE FUNCTION sb_collector_session_guard();
        """;

    /// <summary>A cheque's details never change; its status only makes the allowed moves; cheques are never deleted.</summary>
    public const string ChequeGuard = """
        CREATE FUNCTION sb_cheque_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Cheques cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.receipt_id, NEW.debtor_id, NEW.kind, NEW.number, NEW.bank_name, NEW.cheque_date, NEW.amount, NEW.received_at_utc)
               IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.receipt_id, OLD.debtor_id, OLD.kind, OLD.number, OLD.bank_name, OLD.cheque_date, OLD.amount, OLD.received_at_utc)
               OR NOT ((OLD.status, NEW.status) IN (('RECEIVED', 'DEPOSITED'), ('RECEIVED', 'CANCELLED'), ('DEPOSITED', 'CLEARED'), ('DEPOSITED', 'BOUNCED'),
                                                     ('BOUNCED', 'REPLACED'), ('CANCELLED', 'REPLACED'))) THEN
                RAISE EXCEPTION 'A cheque can only move from % to an allowed next step.', OLD.status USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER trg_cheques_guard BEFORE UPDATE OR DELETE ON cheques FOR EACH ROW EXECUTE FUNCTION sb_cheque_guard();
        """;

    public const string DropGuards = """
        DROP TRIGGER IF EXISTS trg_cheques_guard ON cheques;
        DROP FUNCTION IF EXISTS sb_cheque_guard();
        DROP TRIGGER IF EXISTS trg_collector_sessions_guard ON collector_sessions;
        DROP FUNCTION IF EXISTS sb_collector_session_guard();
        DROP TRIGGER IF EXISTS trg_debtor_receipts_round ON debtor_receipts;
        DROP FUNCTION IF EXISTS sb_debtor_receipt_round();
        """;
}
