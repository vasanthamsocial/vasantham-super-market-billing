using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Custody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_supplier_settlements_amount",
                table: "supplier_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_supplier_ledger_sign",
                table: "supplier_ledger");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_settlements_amount",
                table: "debtor_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_receipts_cheque",
                table: "debtor_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_receipts_counter",
                table: "debtor_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_receipts_method",
                table: "debtor_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_ledger_sign",
                table: "debtor_ledger");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_ledger_type",
                table: "debtor_ledger");

            migrationBuilder.AddColumn<Guid>(
                name: "collector_session_id",
                table: "debtor_receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cheques",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debtor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    bank_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    cheque_date = table.Column<DateOnly>(type: "date", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    replaced_by_receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cheques", x => x.id);
                    table.CheckConstraint("ck_cheques_kind", "kind IN ('CHEQUE', 'DEMAND_DRAFT') AND amount > 0");
                    table.CheckConstraint("ck_cheques_replaced", "(status = 'REPLACED') = (replaced_by_receipt_id IS NOT NULL)");
                    table.CheckConstraint("ck_cheques_status", "status IN ('RECEIVED', 'DEPOSITED', 'CLEARED', 'BOUNCED', 'CANCELLED', 'REPLACED')");
                    table.ForeignKey(
                        name: "fk_cheques_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cheques_debtor_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalTable: "debtor_receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cheques_debtor_receipts_replaced_by_receipt_id",
                        column: x => x.replaced_by_receipt_id,
                        principalTable: "debtor_receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cheques_debtors_debtor_id_business_id",
                        columns: x => new { x.debtor_id, x.business_id },
                        principalTable: "debtors",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cheques_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "collector_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collector_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    opened_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expected_cash = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    declared_cash = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    handed_over_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    counted_cash = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    variance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collector_sessions", x => x.id);
                    table.CheckConstraint("ck_collector_sessions_status", "status IN ('OPEN', 'HANDED_OVER', 'CONFIRMED')");
                    table.CheckConstraint("ck_collector_sessions_steps", "(status = 'OPEN') = (handed_over_at_utc IS NULL) AND (status = 'CONFIRMED') = (confirmed_at_utc IS NOT NULL) AND (status = 'OPEN' OR (expected_cash IS NOT NULL AND declared_cash IS NOT NULL)) AND (status <> 'CONFIRMED' OR (counted_cash IS NOT NULL AND variance = counted_cash - expected_cash AND received_by_user_id <> collector_user_id))");
                    table.ForeignKey(
                        name: "fk_collector_sessions_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collector_sessions_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collector_sessions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collector_sessions_users_collector_user_id",
                        column: x => x.collector_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collector_sessions_users_received_by_user_id",
                        column: x => x.received_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "receipt_reversals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reversed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reversed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipt_reversals", x => x.id);
                    table.CheckConstraint("ck_receipt_reversals_kind", "kind IN ('BOUNCED', 'CANCELLED', 'CORRECTION')");
                    table.ForeignKey(
                        name: "fk_receipt_reversals_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipt_reversals_debtor_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalTable: "debtor_receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_receipt_reversals_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "visit_outcomes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debtor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collector_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_date = table.Column<DateOnly>(type: "date", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_visit_outcomes", x => x.id);
                    table.CheckConstraint("ck_visit_outcomes_outcome", "outcome IN ('NO_PAYMENT', 'NOT_AVAILABLE', 'SHOP_CLOSED', 'DISPUTED')");
                    table.ForeignKey(
                        name: "fk_visit_outcomes_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_visit_outcomes_debtors_debtor_id_business_id",
                        columns: x => new { x.debtor_id, x.business_id },
                        principalTable: "debtors",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_visit_outcomes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cheque_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cheque_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    event_date = table.Column<DateOnly>(type: "date", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cheque_events", x => x.id);
                    table.CheckConstraint("ck_cheque_events_status", "status IN ('RECEIVED', 'DEPOSITED', 'CLEARED', 'BOUNCED', 'CANCELLED', 'REPLACED')");
                    table.ForeignKey(
                        name: "fk_cheque_events_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cheque_events_cheques_cheque_id",
                        column: x => x.cheque_id,
                        principalTable: "cheques",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cheque_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "collector_session_counts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    denomination = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collector_session_counts", x => x.id);
                    table.CheckConstraint("ck_collector_session_counts", "kind IN ('DECLARED', 'COUNTED') AND count > 0 AND denomination > 0");
                    table.ForeignKey(
                        name: "fk_collector_session_counts_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collector_session_counts_collector_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "collector_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collector_session_counts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_supplier_settlements_amount",
                table: "supplier_settlements",
                sql: "amount <> 0 AND charge_entry_id <> payment_entry_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_supplier_ledger_sign",
                table: "supplier_ledger",
                sql: "(entry_type NOT IN ('GRN', 'INVOICE', 'RECEIPT_REVERSAL') OR amount > 0) AND (entry_type NOT IN ('PAYMENT', 'DEBIT_NOTE', 'RECEIPT', 'CREDIT_NOTE') OR amount < 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_settlements_amount",
                table: "debtor_settlements",
                sql: "amount <> 0 AND charge_entry_id <> payment_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_debtor_receipts_collector_session_id",
                table: "debtor_receipts",
                column: "collector_session_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_receipts_cheque",
                table: "debtor_receipts",
                sql: "method NOT IN ('CHEQUE', 'DEMAND_DRAFT', 'OTHER') OR reference IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_receipts_counter",
                table: "debtor_receipts",
                sql: "(shift_id IS NULL) = (counter_id IS NULL) AND (shift_id IS NULL) = (device_id IS NULL) AND (shift_id IS NULL OR collector_session_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_receipts_method",
                table: "debtor_receipts",
                sql: "method IN ('CASH', 'CARD', 'UPI', 'BANK_TRANSFER', 'CHEQUE', 'DEMAND_DRAFT', 'OTHER')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_ledger_sign",
                table: "debtor_ledger",
                sql: "(entry_type NOT IN ('GRN', 'INVOICE', 'RECEIPT_REVERSAL') OR amount > 0) AND (entry_type NOT IN ('PAYMENT', 'DEBIT_NOTE', 'RECEIPT', 'CREDIT_NOTE') OR amount < 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_ledger_type",
                table: "debtor_ledger",
                sql: "entry_type IN ('OPENING', 'INVOICE', 'RECEIPT', 'CREDIT_NOTE', 'ADJUSTMENT', 'RECEIPT_REVERSAL')");

            migrationBuilder.CreateIndex(
                name: "ix_cheque_events_business_id_tenant_id",
                table: "cheque_events",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cheque_events_cheque_id",
                table: "cheque_events",
                column: "cheque_id");

            migrationBuilder.CreateIndex(
                name: "ix_cheque_events_tenant_id",
                table: "cheque_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_cheques_business_id_status",
                table: "cheques",
                columns: new[] { "business_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_cheques_business_id_tenant_id",
                table: "cheques",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cheques_debtor_id",
                table: "cheques",
                column: "debtor_id");

            migrationBuilder.CreateIndex(
                name: "ix_cheques_debtor_id_business_id",
                table: "cheques",
                columns: new[] { "debtor_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cheques_receipt_id",
                table: "cheques",
                column: "receipt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cheques_replaced_by_receipt_id",
                table: "cheques",
                column: "replaced_by_receipt_id");

            migrationBuilder.CreateIndex(
                name: "ix_cheques_tenant_id",
                table: "cheques",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_collector_session_counts_business_id_tenant_id",
                table: "collector_session_counts",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collector_session_counts_session_id_kind_denomination",
                table: "collector_session_counts",
                columns: new[] { "session_id", "kind", "denomination" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_collector_session_counts_tenant_id",
                table: "collector_session_counts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_collector_sessions_business_id_collector_user_id",
                table: "collector_sessions",
                columns: new[] { "business_id", "collector_user_id" },
                unique: true,
                filter: "status = 'OPEN'");

            migrationBuilder.CreateIndex(
                name: "ix_collector_sessions_business_id_tenant_id",
                table: "collector_sessions",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collector_sessions_collector_user_id",
                table: "collector_sessions",
                column: "collector_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_collector_sessions_received_by_user_id",
                table: "collector_sessions",
                column: "received_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_collector_sessions_store_id_business_id",
                table: "collector_sessions",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collector_sessions_store_id_status",
                table: "collector_sessions",
                columns: new[] { "store_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_collector_sessions_tenant_id",
                table: "collector_sessions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_business_id_tenant_id",
                table: "receipt_reversals",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_receipt_id",
                table: "receipt_reversals",
                column: "receipt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_receipt_reversals_tenant_id",
                table: "receipt_reversals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_visit_outcomes_business_id_tenant_id",
                table: "visit_outcomes",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_visit_outcomes_debtor_id_business_id",
                table: "visit_outcomes",
                columns: new[] { "debtor_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_visit_outcomes_debtor_id_visit_date",
                table: "visit_outcomes",
                columns: new[] { "debtor_id", "visit_date" });

            migrationBuilder.CreateIndex(
                name: "ix_visit_outcomes_tenant_id",
                table: "visit_outcomes",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "fk_debtor_receipts_collector_sessions_collector_session_id",
                table: "debtor_receipts",
                column: "collector_session_id",
                principalTable: "collector_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(CustodySql.Tables));
            foreach (var table in CustodySql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Protect(table));
            }

            migrationBuilder.Sql(CustodySql.SettlementGuardWithUndo("debtor_settlements", "debtor_ledger"));
            migrationBuilder.Sql(CustodySql.SettlementGuardWithUndo("supplier_settlements", "supplier_ledger"));
            migrationBuilder.Sql(CustodySql.ReceiptRoundGuard);
            migrationBuilder.Sql(CustodySql.SessionGuard);
            migrationBuilder.Sql(CustodySql.ChequeGuard);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CustodySql.DropGuards);
            migrationBuilder.Sql(CustodySql.OriginalSettlementGuard("supplier_settlements", "supplier_ledger"));
            migrationBuilder.Sql(CustodySql.OriginalSettlementGuard("debtor_settlements", "debtor_ledger"));
            foreach (var table in CustodySql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Unprotect(table));
            }

            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(CustodySql.Tables));

            migrationBuilder.DropForeignKey(
                name: "fk_debtor_receipts_collector_sessions_collector_session_id",
                table: "debtor_receipts");

            migrationBuilder.DropTable(
                name: "cheque_events");

            migrationBuilder.DropTable(
                name: "collector_session_counts");

            migrationBuilder.DropTable(
                name: "receipt_reversals");

            migrationBuilder.DropTable(
                name: "visit_outcomes");

            migrationBuilder.DropTable(
                name: "cheques");

            migrationBuilder.DropTable(
                name: "collector_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_supplier_settlements_amount",
                table: "supplier_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_supplier_ledger_sign",
                table: "supplier_ledger");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_settlements_amount",
                table: "debtor_settlements");

            migrationBuilder.DropIndex(
                name: "ix_debtor_receipts_collector_session_id",
                table: "debtor_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_receipts_cheque",
                table: "debtor_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_receipts_counter",
                table: "debtor_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_receipts_method",
                table: "debtor_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_ledger_sign",
                table: "debtor_ledger");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debtor_ledger_type",
                table: "debtor_ledger");

            migrationBuilder.DropColumn(
                name: "collector_session_id",
                table: "debtor_receipts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_supplier_settlements_amount",
                table: "supplier_settlements",
                sql: "amount > 0 AND charge_entry_id <> payment_entry_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_supplier_ledger_sign",
                table: "supplier_ledger",
                sql: "(entry_type NOT IN ('GRN', 'INVOICE') OR amount > 0) AND (entry_type NOT IN ('PAYMENT', 'DEBIT_NOTE', 'RECEIPT', 'CREDIT_NOTE') OR amount < 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_settlements_amount",
                table: "debtor_settlements",
                sql: "amount > 0 AND charge_entry_id <> payment_entry_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_receipts_cheque",
                table: "debtor_receipts",
                sql: "method <> 'CHEQUE' OR reference IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_receipts_counter",
                table: "debtor_receipts",
                sql: "(shift_id IS NULL) = (counter_id IS NULL) AND (shift_id IS NULL) = (device_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_receipts_method",
                table: "debtor_receipts",
                sql: "method IN ('CASH', 'CARD', 'UPI', 'BANK_TRANSFER', 'CHEQUE')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_ledger_sign",
                table: "debtor_ledger",
                sql: "(entry_type NOT IN ('GRN', 'INVOICE') OR amount > 0) AND (entry_type NOT IN ('PAYMENT', 'DEBIT_NOTE', 'RECEIPT', 'CREDIT_NOTE') OR amount < 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debtor_ledger_type",
                table: "debtor_ledger",
                sql: "entry_type IN ('OPENING', 'INVOICE', 'RECEIPT', 'CREDIT_NOTE', 'ADJUSTMENT')");
        }
    }
}
