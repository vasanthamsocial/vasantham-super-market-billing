using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Accounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "consent_changed_at_utc",
                table: "suppliers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_person",
                table: "suppliers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "credit_period_days",
                table: "suppliers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "suppliers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "sms_consent",
                table: "suppliers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "sms_number",
                table: "suppliers",
                type: "character varying(13)",
                maxLength: 13,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trade_name",
                table: "suppliers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "whatsapp_consent",
                table: "suppliers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "whatsapp_number",
                table: "suppliers",
                type: "character varying(13)",
                maxLength: 13,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "debtors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    legal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    trade_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    gstin = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    state_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    contact_person = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    whatsapp_number = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    sms_number = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    whatsapp_consent = table.Column<bool>(type: "boolean", nullable: false),
                    sms_consent = table.Column<bool>(type: "boolean", nullable: false),
                    consent_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    credit_period_days = table.Column<int>(type: "integer", nullable: false),
                    credit_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    customer_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_debtors", x => x.id);
                    table.UniqueConstraint("ak_debtors_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_debtors_code", "code ~ '^[A-Z0-9-]{1,20}$'");
                    table.CheckConstraint("ck_debtors_consent", "(NOT whatsapp_consent OR whatsapp_number IS NOT NULL) AND (NOT sms_consent OR sms_number IS NOT NULL)");
                    table.CheckConstraint("ck_debtors_credit", "credit_limit >= 0 AND credit_period_days BETWEEN 0 AND 365");
                    table.CheckConstraint("ck_debtors_gstin_state", "gstin IS NULL OR left(gstin, 2) = state_code");
                    table.CheckConstraint("ck_debtors_status", "status IN ('ACTIVE', 'ON_HOLD', 'CLOSED')");
                    table.ForeignKey(
                        name: "fk_debtors_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtors_customer_groups_customer_group_id_business_id",
                        columns: x => new { x.customer_group_id, x.business_id },
                        principalTable: "customer_groups",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtors_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    entry_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    entry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    narration = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_ledger", x => x.id);
                    table.UniqueConstraint("ak_supplier_ledger_id_supplier_id", x => new { x.id, x.supplier_id });
                    table.CheckConstraint("ck_supplier_ledger_amount", "amount <> 0 AND sequence > 0");
                    table.CheckConstraint("ck_supplier_ledger_due", "(amount > 0) = (due_date IS NOT NULL)");
                    table.CheckConstraint("ck_supplier_ledger_sign", "(entry_type NOT IN ('GRN', 'INVOICE') OR amount > 0) AND (entry_type NOT IN ('PAYMENT', 'DEBIT_NOTE', 'RECEIPT', 'CREDIT_NOTE') OR amount < 0)");
                    table.CheckConstraint("ck_supplier_ledger_type", "entry_type IN ('OPENING', 'GRN', 'PAYMENT', 'DEBIT_NOTE', 'ADJUSTMENT')");
                    table.ForeignKey(
                        name: "fk_supplier_ledger_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_ledger_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_ledger_suppliers_supplier_id_business_id",
                        columns: x => new { x.supplier_id, x.business_id },
                        principalTable: "suppliers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_ledger_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    payment_date = table.Column<DateOnly>(type: "date", nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    paid_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_payments", x => x.id);
                    table.CheckConstraint("ck_supplier_payments_amount", "amount > 0");
                    table.CheckConstraint("ck_supplier_payments_cheque", "method <> 'CHEQUE' OR reference IS NOT NULL");
                    table.CheckConstraint("ck_supplier_payments_method", "method IN ('CASH', 'BANK_TRANSFER', 'UPI', 'CHEQUE')");
                    table.ForeignKey(
                        name: "fk_supplier_payments_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_payments_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_payments_suppliers_supplier_id_business_id",
                        columns: x => new { x.supplier_id, x.business_id },
                        principalTable: "suppliers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_payments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "debtor_ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debtor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    entry_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    entry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    narration = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_debtor_ledger", x => x.id);
                    table.UniqueConstraint("ak_debtor_ledger_id_debtor_id", x => new { x.id, x.debtor_id });
                    table.CheckConstraint("ck_debtor_ledger_amount", "amount <> 0 AND sequence > 0");
                    table.CheckConstraint("ck_debtor_ledger_due", "(amount > 0) = (due_date IS NOT NULL)");
                    table.CheckConstraint("ck_debtor_ledger_sign", "(entry_type NOT IN ('GRN', 'INVOICE') OR amount > 0) AND (entry_type NOT IN ('PAYMENT', 'DEBIT_NOTE', 'RECEIPT', 'CREDIT_NOTE') OR amount < 0)");
                    table.CheckConstraint("ck_debtor_ledger_type", "entry_type IN ('OPENING', 'INVOICE', 'RECEIPT', 'CREDIT_NOTE', 'ADJUSTMENT')");
                    table.ForeignKey(
                        name: "fk_debtor_ledger_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtor_ledger_debtors_debtor_id_business_id",
                        columns: x => new { x.debtor_id, x.business_id },
                        principalTable: "debtors",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtor_ledger_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtor_ledger_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_settlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supplier_settlements", x => x.id);
                    table.CheckConstraint("ck_supplier_settlements_amount", "amount > 0 AND charge_entry_id <> payment_entry_id");
                    table.ForeignKey(
                        name: "fk_supplier_settlements_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_settlements_supplier_ledger_charge_entry_id_suppli",
                        columns: x => new { x.charge_entry_id, x.supplier_id },
                        principalTable: "supplier_ledger",
                        principalColumns: new[] { "id", "supplier_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_settlements_supplier_ledger_payment_entry_id_suppl",
                        columns: x => new { x.payment_entry_id, x.supplier_id },
                        principalTable: "supplier_ledger",
                        principalColumns: new[] { "id", "supplier_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supplier_settlements_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "debtor_settlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debtor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_debtor_settlements", x => x.id);
                    table.CheckConstraint("ck_debtor_settlements_amount", "amount > 0 AND charge_entry_id <> payment_entry_id");
                    table.ForeignKey(
                        name: "fk_debtor_settlements_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtor_settlements_debtor_ledger_charge_entry_id_debtor_id",
                        columns: x => new { x.charge_entry_id, x.debtor_id },
                        principalTable: "debtor_ledger",
                        principalColumns: new[] { "id", "debtor_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtor_settlements_debtor_ledger_payment_entry_id_debtor_id",
                        columns: x => new { x.payment_entry_id, x.debtor_id },
                        principalTable: "debtor_ledger",
                        principalColumns: new[] { "id", "debtor_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtor_settlements_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_suppliers_consent",
                table: "suppliers",
                sql: "(NOT whatsapp_consent OR whatsapp_number IS NOT NULL) AND (NOT sms_consent OR sms_number IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_suppliers_credit_period",
                table: "suppliers",
                sql: "credit_period_days BETWEEN 0 AND 365");

            migrationBuilder.CreateIndex(
                name: "ix_debtor_ledger_business_id_tenant_id",
                table: "debtor_ledger",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtor_ledger_debtor_id_business_id",
                table: "debtor_ledger",
                columns: new[] { "debtor_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtor_ledger_debtor_id_sequence",
                table: "debtor_ledger",
                columns: new[] { "debtor_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_debtor_ledger_document_id_entry_type",
                table: "debtor_ledger",
                columns: new[] { "document_id", "entry_type" });

            migrationBuilder.CreateIndex(
                name: "ix_debtor_ledger_store_id_business_id",
                table: "debtor_ledger",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtor_ledger_tenant_id",
                table: "debtor_ledger",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_debtor_settlements_business_id_tenant_id",
                table: "debtor_settlements",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtor_settlements_charge_entry_id",
                table: "debtor_settlements",
                column: "charge_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_debtor_settlements_charge_entry_id_debtor_id",
                table: "debtor_settlements",
                columns: new[] { "charge_entry_id", "debtor_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtor_settlements_payment_entry_id",
                table: "debtor_settlements",
                column: "payment_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_debtor_settlements_payment_entry_id_debtor_id",
                table: "debtor_settlements",
                columns: new[] { "payment_entry_id", "debtor_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtor_settlements_tenant_id",
                table: "debtor_settlements",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_debtors_business_id_code",
                table: "debtors",
                columns: new[] { "business_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_debtors_business_id_gstin",
                table: "debtors",
                columns: new[] { "business_id", "gstin" });

            migrationBuilder.CreateIndex(
                name: "ix_debtors_business_id_tenant_id",
                table: "debtors",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtors_customer_group_id_business_id",
                table: "debtors",
                columns: new[] { "customer_group_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtors_tenant_id",
                table: "debtors",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_ledger_business_id_tenant_id",
                table: "supplier_ledger",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_ledger_document_id_entry_type",
                table: "supplier_ledger",
                columns: new[] { "document_id", "entry_type" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_ledger_store_id_business_id",
                table: "supplier_ledger",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_ledger_supplier_id_business_id",
                table: "supplier_ledger",
                columns: new[] { "supplier_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_ledger_supplier_id_sequence",
                table: "supplier_ledger",
                columns: new[] { "supplier_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supplier_ledger_tenant_id",
                table: "supplier_ledger",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_payments_business_id_idempotency_key",
                table: "supplier_payments",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supplier_payments_business_id_tenant_id",
                table: "supplier_payments",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_payments_store_id_business_id",
                table: "supplier_payments",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_payments_store_id_sequence_number",
                table: "supplier_payments",
                columns: new[] { "store_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supplier_payments_supplier_id_business_id",
                table: "supplier_payments",
                columns: new[] { "supplier_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_payments_supplier_id_payment_date",
                table: "supplier_payments",
                columns: new[] { "supplier_id", "payment_date" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_payments_tenant_id",
                table: "supplier_payments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_settlements_business_id_tenant_id",
                table: "supplier_settlements",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_settlements_charge_entry_id",
                table: "supplier_settlements",
                column: "charge_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_settlements_charge_entry_id_supplier_id",
                table: "supplier_settlements",
                columns: new[] { "charge_entry_id", "supplier_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_settlements_payment_entry_id",
                table: "supplier_settlements",
                column: "payment_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_supplier_settlements_payment_entry_id_supplier_id",
                table: "supplier_settlements",
                columns: new[] { "payment_entry_id", "supplier_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supplier_settlements_tenant_id",
                table: "supplier_settlements",
                column: "tenant_id");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(AccountsSql.Tables));
            migrationBuilder.Sql(AccountsSql.BackfillSupplierLedger);
            migrationBuilder.Sql(AccountsSql.LedgerChain("supplier_ledger", "supplier_id"));
            migrationBuilder.Sql(AccountsSql.LedgerChain("debtor_ledger", "debtor_id"));
            migrationBuilder.Sql(AccountsSql.SettlementGuard("supplier_settlements", "supplier_ledger"));
            migrationBuilder.Sql(AccountsSql.SettlementGuard("debtor_settlements", "debtor_ledger"));
            foreach (var table in AccountsSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Protect(table));
            }

            migrationBuilder.Sql(AccountsSql.DebtorGuard);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AccountsSql.DropDebtorGuard);
            foreach (var table in AccountsSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Unprotect(table));
            }

            migrationBuilder.Sql(AccountsSql.DropSettlementGuard("debtor_settlements"));
            migrationBuilder.Sql(AccountsSql.DropSettlementGuard("supplier_settlements"));
            migrationBuilder.Sql(AccountsSql.DropLedgerChain("debtor_ledger"));
            migrationBuilder.Sql(AccountsSql.DropLedgerChain("supplier_ledger"));
            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(AccountsSql.Tables));

            migrationBuilder.DropTable(
                name: "debtor_settlements");

            migrationBuilder.DropTable(
                name: "supplier_payments");

            migrationBuilder.DropTable(
                name: "supplier_settlements");

            migrationBuilder.DropTable(
                name: "debtor_ledger");

            migrationBuilder.DropTable(
                name: "supplier_ledger");

            migrationBuilder.DropTable(
                name: "debtors");

            migrationBuilder.DropCheckConstraint(
                name: "ck_suppliers_consent",
                table: "suppliers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_suppliers_credit_period",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "consent_changed_at_utc",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "contact_person",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "credit_period_days",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "email",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "sms_consent",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "sms_number",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "trade_name",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "whatsapp_consent",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "whatsapp_number",
                table: "suppliers");
        }
    }
}
