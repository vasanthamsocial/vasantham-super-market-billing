using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Returns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_supervisor_approvals_use",
                table: "supervisor_approvals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_invoice_payments_method",
                table: "sales_invoice_payments");

            migrationBuilder.RenameColumn(
                name: "used_invoice_id",
                table: "supervisor_approvals",
                newName: "used_document_id");

            migrationBuilder.RenameColumn(
                name: "max_discount",
                table: "supervisor_approvals",
                newName: "max_amount");

            migrationBuilder.RenameIndex(
                name: "ix_supervisor_approvals_used_invoice_id",
                table: "supervisor_approvals",
                newName: "ix_supervisor_approvals_used_document_id");

            migrationBuilder.AlterColumn<string>(
                name: "method",
                table: "sales_invoice_payments",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.CreateTable(
                name: "sales_returns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_invoice_number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    original_invoice_date = table.Column<DateOnly>(type: "date", nullable: false),
                    number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    number_prefix = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    tax_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_inter_state = table.Column<bool>(type: "boolean", nullable: false),
                    place_of_supply_state_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cashier_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    taxable_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cess_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    round_off = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    store_credit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_returns", x => x.id);
                    table.UniqueConstraint("ak_sales_returns_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_returns_gst_split", "cgst_total = sgst_total AND (CASE WHEN is_inter_state THEN cgst_total = 0 ELSE igst_total = 0 END)");
                    table.CheckConstraint("ck_sales_returns_number", "char_length(number) <= 16 AND number_prefix ~ '^[A-Z0-9]{1,7}$' AND sequence_number > 0 AND number = number_prefix || '/CN' || CASE WHEN sequence_number < 1000000 THEN lpad(sequence_number::text, 6, '0') ELSE sequence_number::text END");
                    table.CheckConstraint("ck_sales_returns_store_credit", "store_credit >= 0 AND store_credit <= grand_total");
                    table.CheckConstraint("ck_sales_returns_tax_mode", "tax_mode IN ('GST_REGULAR', 'GST_COMPOSITION', 'NOT_GST_REGISTERED')");
                    table.CheckConstraint("ck_sales_returns_total", "grand_total = taxable_total + cgst_total + sgst_total + igst_total + cess_total + round_off AND abs(round_off) <= 0.5 AND grand_total = round(grand_total) AND grand_total >= 0");
                    table.ForeignKey(
                        name: "fk_sales_returns_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_returns_counters_counter_id_business_id",
                        columns: x => new { x.counter_id, x.business_id },
                        principalTable: "counters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_returns_sales_invoices_original_invoice_id_business_id",
                        columns: x => new { x.original_invoice_id, x.business_id },
                        principalTable: "sales_invoices",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_returns_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_returns_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "credit_note_redemptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    redeemed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_note_redemptions", x => x.id);
                    table.CheckConstraint("ck_credit_note_redemptions_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_credit_note_redemptions_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_credit_note_redemptions_sales_invoices_invoice_id_business_",
                        columns: x => new { x.invoice_id, x.business_id },
                        principalTable: "sales_invoices",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_credit_note_redemptions_sales_returns_return_id_business_id",
                        columns: x => new { x.return_id, x.business_id },
                        principalTable: "sales_returns",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_credit_note_redemptions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_return_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    original_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    base_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    restocked = table.Column<bool>(type: "boolean", nullable: false),
                    gross = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    item_discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    bill_discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    taxable = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cess = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cost_returned = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_return_lines", x => x.id);
                    table.CheckConstraint("ck_sales_return_lines_amounts", "quantity > 0 AND base_quantity > 0 AND taxable >= 0 AND cgst >= 0 AND igst >= 0 AND cess >= 0 AND cost_returned >= 0");
                    table.CheckConstraint("ck_sales_return_lines_restock", "restocked OR cost_returned = 0");
                    table.CheckConstraint("ck_sales_return_lines_total", "total = taxable + cgst + sgst + igst + cess AND cgst = sgst AND (cgst = 0 OR igst = 0)");
                    table.ForeignKey(
                        name: "fk_sales_return_lines_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_lines_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_lines_sales_invoice_lines_original_line_id",
                        column: x => x.original_line_id,
                        principalTable: "sales_invoice_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_lines_sales_returns_return_id",
                        column: x => x.return_id,
                        principalTable: "sales_returns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_lines_sales_returns_return_id_business_id",
                        columns: x => new { x.return_id, x.business_id },
                        principalTable: "sales_returns",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_lines_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_return_refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_order = table.Column<int>(type: "integer", nullable: false),
                    method = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_return_refunds", x => x.id);
                    table.CheckConstraint("ck_sales_return_refunds_amount", "amount > 0");
                    table.CheckConstraint("ck_sales_return_refunds_method", "method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'STORE_CREDIT')");
                    table.ForeignKey(
                        name: "fk_sales_return_refunds_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_refunds_sales_returns_return_id",
                        column: x => x.return_id,
                        principalTable: "sales_returns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_refunds_sales_returns_return_id_business_id",
                        columns: x => new { x.return_id, x.business_id },
                        principalTable: "sales_returns",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_return_refunds_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals",
                sql: "(kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_amount IS NULL) OR (kind IN ('DISCOUNT', 'RETURN') AND variant_unit_id IS NULL AND approved_price IS NULL AND max_amount > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_supervisor_approvals_use",
                table: "supervisor_approvals",
                sql: "(used_at_utc IS NULL) = (used_document_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_invoice_payments_credit_note",
                table: "sales_invoice_payments",
                sql: "method <> 'CREDIT_NOTE' OR reference IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_invoice_payments_method",
                table: "sales_invoice_payments",
                sql: "method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'CREDIT_NOTE')");

            migrationBuilder.CreateIndex(
                name: "ix_credit_note_redemptions_business_id_tenant_id",
                table: "credit_note_redemptions",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_note_redemptions_invoice_id",
                table: "credit_note_redemptions",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_credit_note_redemptions_invoice_id_business_id",
                table: "credit_note_redemptions",
                columns: new[] { "invoice_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_note_redemptions_return_id",
                table: "credit_note_redemptions",
                column: "return_id");

            migrationBuilder.CreateIndex(
                name: "ix_credit_note_redemptions_return_id_business_id",
                table: "credit_note_redemptions",
                columns: new[] { "return_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_note_redemptions_tenant_id",
                table: "credit_note_redemptions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_lines_business_id_tenant_id",
                table: "sales_return_lines",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_lines_original_line_id",
                table: "sales_return_lines",
                column: "original_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_lines_return_id_business_id",
                table: "sales_return_lines",
                columns: new[] { "return_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_lines_return_id_line_number",
                table: "sales_return_lines",
                columns: new[] { "return_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_lines_tenant_id",
                table: "sales_return_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_lines_variant_id_business_id",
                table: "sales_return_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_refunds_business_id_tenant_id",
                table: "sales_return_refunds",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_refunds_return_id_business_id",
                table: "sales_return_refunds",
                columns: new[] { "return_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_refunds_return_id_refund_order",
                table: "sales_return_refunds",
                columns: new[] { "return_id", "refund_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_return_refunds_tenant_id",
                table: "sales_return_refunds",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_returns_business_id_idempotency_key",
                table: "sales_returns",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_returns_business_id_number",
                table: "sales_returns",
                columns: new[] { "business_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_returns_business_id_tenant_id",
                table: "sales_returns",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_returns_counter_id_business_id",
                table: "sales_returns",
                columns: new[] { "counter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_returns_counter_id_number_prefix_sequence_number",
                table: "sales_returns",
                columns: new[] { "counter_id", "number_prefix", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_returns_original_invoice_id",
                table: "sales_returns",
                column: "original_invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_returns_original_invoice_id_business_id",
                table: "sales_returns",
                columns: new[] { "original_invoice_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_returns_store_id_business_id",
                table: "sales_returns",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_returns_tenant_id",
                table: "sales_returns",
                column: "tenant_id");

            migrationBuilder.Sql(ReturnsSql.ApprovalGuard);
            migrationBuilder.Sql(TenancySql.ProtectTenantTables(ReturnsSql.Tables));
            foreach (var table in ReturnsSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Protect(table));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in ReturnsSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Unprotect(table));
            }

            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(ReturnsSql.Tables));

            migrationBuilder.DropTable(
                name: "credit_note_redemptions");

            migrationBuilder.DropTable(
                name: "sales_return_lines");

            migrationBuilder.DropTable(
                name: "sales_return_refunds");

            migrationBuilder.DropTable(
                name: "sales_returns");

            migrationBuilder.DropCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_supervisor_approvals_use",
                table: "supervisor_approvals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_invoice_payments_credit_note",
                table: "sales_invoice_payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_invoice_payments_method",
                table: "sales_invoice_payments");

            migrationBuilder.RenameColumn(
                name: "used_document_id",
                table: "supervisor_approvals",
                newName: "used_invoice_id");

            migrationBuilder.RenameColumn(
                name: "max_amount",
                table: "supervisor_approvals",
                newName: "max_discount");

            migrationBuilder.RenameIndex(
                name: "ix_supervisor_approvals_used_document_id",
                table: "supervisor_approvals",
                newName: "ix_supervisor_approvals_used_invoice_id");

            migrationBuilder.AlterColumn<string>(
                name: "method",
                table: "sales_invoice_payments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(12)",
                oldMaxLength: 12);

            migrationBuilder.AddCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals",
                sql: "(kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_discount IS NULL) OR (kind = 'DISCOUNT' AND variant_unit_id IS NULL AND approved_price IS NULL AND max_discount > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_supervisor_approvals_use",
                table: "supervisor_approvals",
                sql: "(used_at_utc IS NULL) = (used_invoice_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_invoice_payments_method",
                table: "sales_invoice_payments",
                sql: "method IN ('CASH', 'CARD', 'UPI', 'WALLET')");

            migrationBuilder.Sql(ReturnsSql.PreviousApprovalGuard);
        }
    }
}
