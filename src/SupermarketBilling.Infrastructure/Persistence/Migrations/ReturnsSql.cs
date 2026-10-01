namespace SupermarketBilling.Infrastructure.Persistence.Migrations;

internal static class ReturnsSql
{
    public static readonly string[] Tables = ["sales_returns", "sales_return_lines", "sales_return_refunds", "credit_note_redemptions"];

    /// <summary>Credit notes, their lines and refunds, and uses of store credit are never edited or deleted.</summary>
    public static readonly string[] AppendOnly = Tables;

    /// <summary>The supervisor-approval guard, for the renamed columns (max_amount, used_document_id).</summary>
    public const string ApprovalGuard = """
        CREATE OR REPLACE FUNCTION sb_supervisor_approval_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Supervisor approvals cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.used_at_utc IS NOT NULL
               OR (NEW.id, NEW.tenant_id, NEW.business_id, NEW.counter_id, NEW.kind, NEW.variant_unit_id, NEW.approved_price, NEW.max_amount,
                   NEW.reason, NEW.approved_by_user_id, NEW.requested_by_user_id, NEW.token_hash, NEW.created_at_utc, NEW.expires_at_utc)
                  IS DISTINCT FROM
                  (OLD.id, OLD.tenant_id, OLD.business_id, OLD.counter_id, OLD.kind, OLD.variant_unit_id, OLD.approved_price, OLD.max_amount,
                   OLD.reason, OLD.approved_by_user_id, OLD.requested_by_user_id, OLD.token_hash, OLD.created_at_utc, OLD.expires_at_utc) THEN
                RAISE EXCEPTION 'A supervisor approval can only be marked used, once.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        """;

    /// <summary>The guard as it was before the rename (for rolling back).</summary>
    public const string PreviousApprovalGuard = """
        CREATE OR REPLACE FUNCTION sb_supervisor_approval_guard() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'Supervisor approvals cannot be deleted.' USING ERRCODE = 'restrict_violation';
            END IF;
            IF OLD.used_at_utc IS NOT NULL
               OR (NEW.id, NEW.tenant_id, NEW.business_id, NEW.counter_id, NEW.kind, NEW.variant_unit_id, NEW.approved_price, NEW.max_discount,
                   NEW.reason, NEW.approved_by_user_id, NEW.requested_by_user_id, NEW.token_hash, NEW.created_at_utc, NEW.expires_at_utc)
                  IS DISTINCT FROM
                  (OLD.id, OLD.tenant_id, OLD.business_id, OLD.counter_id, OLD.kind, OLD.variant_unit_id, OLD.approved_price, OLD.max_discount,
                   OLD.reason, OLD.approved_by_user_id, OLD.requested_by_user_id, OLD.token_hash, OLD.created_at_utc, OLD.expires_at_utc) THEN
                RAISE EXCEPTION 'A supervisor approval can only be marked used, once.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END;
        $$;
        """;
}
