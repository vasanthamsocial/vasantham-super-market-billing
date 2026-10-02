using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreditSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_return_refunds_method",
                table: "sales_return_refunds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_invoice_payments_method",
                table: "sales_invoice_payments");

            migrationBuilder.AddColumn<Guid>(
                name: "credit_approval_id",
                table: "sales_invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "debtor_id",
                table: "sales_invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "due_date",
                table: "sales_invoices",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "debtor_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debtor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    receipt_date = table.Column<DateOnly>(type: "date", nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    counter_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cashier_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_debtor_receipts", x => x.id);
                    table.CheckConstraint("ck_debtor_receipts_amount", "amount > 0");
                    table.CheckConstraint("ck_debtor_receipts_cheque", "method <> 'CHEQUE' OR reference IS NOT NULL");
                    table.CheckConstraint("ck_debtor_receipts_counter", "(shift_id IS NULL) = (counter_id IS NULL) AND (shift_id IS NULL) = (device_id IS NULL)");
                    table.CheckConstraint("ck_debtor_receipts_method", "method IN ('CASH', 'CARD', 'UPI', 'BANK_TRANSFER', 'CHEQUE')");
                    table.ForeignKey(
                        name: "fk_debtor_receipts_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtor_receipts_counters_counter_id",
                        column: x => x.counter_id,
                        principalTable: "counters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtor_receipts_debtors_debtor_id_business_id",
                        columns: x => new { x.debtor_id, x.business_id },
                        principalTable: "debtors",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtor_receipts_shifts_shift_id",
                        column: x => x.shift_id,
                        principalTable: "shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtor_receipts_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_debtor_receipts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals",
                sql: "(kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_amount IS NULL) OR (kind IN ('DISCOUNT', 'RETURN', 'PAY_OUT', 'CREDIT_LIMIT') AND variant_unit_id IS NULL AND approved_price IS NULL AND max_amount > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_return_refunds_method",
                table: "sales_return_refunds",
                sql: "method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'STORE_CREDIT', 'ON_ACCOUNT')");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_credit_approval_id",
                table: "sales_invoices",
                column: "credit_approval_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_debtor_id_business_date",
                table: "sales_invoices",
                columns: new[] { "debtor_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_debtor_id_business_id",
                table: "sales_invoices",
                columns: new[] { "debtor_id", "business_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_invoices_credit",
                table: "sales_invoices",
                sql: "(due_date IS NULL OR debtor_id IS NOT NULL) AND (credit_approval_id IS NULL OR due_date IS NOT NULL) AND (due_date IS NULL OR due_date >= business_date)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_invoice_payments_method",
                table: "sales_invoice_payments",
                sql: "method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'CREDIT_NOTE', 'ON_ACCOUNT')");

            migrationBuilder.CreateIndex(
                name: "ix_debtor_receipts_business_id_idempotency_key",
                table: "debtor_receipts",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_debtor_receipts_business_id_tenant_id",
                table: "debtor_receipts",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtor_receipts_counter_id",
                table: "debtor_receipts",
                column: "counter_id");

            migrationBuilder.CreateIndex(
                name: "ix_debtor_receipts_debtor_id_business_id",
                table: "debtor_receipts",
                columns: new[] { "debtor_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtor_receipts_debtor_id_receipt_date",
                table: "debtor_receipts",
                columns: new[] { "debtor_id", "receipt_date" });

            migrationBuilder.CreateIndex(
                name: "ix_debtor_receipts_shift_id",
                table: "debtor_receipts",
                column: "shift_id");

            migrationBuilder.CreateIndex(
                name: "ix_debtor_receipts_store_id_business_id",
                table: "debtor_receipts",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtor_receipts_store_id_sequence_number",
                table: "debtor_receipts",
                columns: new[] { "store_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_debtor_receipts_tenant_id",
                table: "debtor_receipts",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "fk_sales_invoices_debtors_debtor_id_business_id",
                table: "sales_invoices",
                columns: new[] { "debtor_id", "business_id" },
                principalTable: "debtors",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_sales_invoices_supervisor_approvals_credit_approval_id",
                table: "sales_invoices",
                column: "credit_approval_id",
                principalTable: "supervisor_approvals",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(CreditSalesSql.Tables));
            foreach (var table in CreditSalesSql.Tables)
            {
                migrationBuilder.Sql(AppendOnlySql.Protect(table));
            }

            migrationBuilder.Sql(CreditSalesSql.ReceiptShiftGuard);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreditSalesSql.DropReceiptShiftGuard);
            foreach (var table in CreditSalesSql.Tables)
            {
                migrationBuilder.Sql(AppendOnlySql.Unprotect(table));
            }

            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(CreditSalesSql.Tables));

            migrationBuilder.DropForeignKey(
                name: "fk_sales_invoices_debtors_debtor_id_business_id",
                table: "sales_invoices");

            migrationBuilder.DropForeignKey(
                name: "fk_sales_invoices_supervisor_approvals_credit_approval_id",
                table: "sales_invoices");

            migrationBuilder.DropTable(
                name: "debtor_receipts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_return_refunds_method",
                table: "sales_return_refunds");

            migrationBuilder.DropIndex(
                name: "ix_sales_invoices_credit_approval_id",
                table: "sales_invoices");

            migrationBuilder.DropIndex(
                name: "ix_sales_invoices_debtor_id_business_date",
                table: "sales_invoices");

            migrationBuilder.DropIndex(
                name: "ix_sales_invoices_debtor_id_business_id",
                table: "sales_invoices");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_invoices_credit",
                table: "sales_invoices");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_invoice_payments_method",
                table: "sales_invoice_payments");

            migrationBuilder.DropColumn(
                name: "credit_approval_id",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "debtor_id",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "due_date",
                table: "sales_invoices");

            migrationBuilder.AddCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals",
                sql: "(kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_amount IS NULL) OR (kind IN ('DISCOUNT', 'RETURN', 'PAY_OUT') AND variant_unit_id IS NULL AND approved_price IS NULL AND max_amount > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_return_refunds_method",
                table: "sales_return_refunds",
                sql: "method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'STORE_CREDIT')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_invoice_payments_method",
                table: "sales_invoice_payments",
                sql: "method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'CREDIT_NOTE')");
        }
    }
}
