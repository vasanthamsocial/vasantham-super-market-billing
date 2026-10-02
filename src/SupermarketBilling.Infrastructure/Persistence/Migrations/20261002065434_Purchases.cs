using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Purchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "purchase_settings",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cost_reason_threshold_percent = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    cost_approval_threshold_percent = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    allow_loss_leader = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_settings", x => x.business_id);
                    table.CheckConstraint("ck_purchase_settings_thresholds", "cost_reason_threshold_percent >= 0 AND cost_approval_threshold_percent >= cost_reason_threshold_percent");
                    table.ForeignKey(
                        name: "fk_purchase_settings_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "suppliers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    gstin = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    state_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suppliers", x => x.id);
                    table.UniqueConstraint("ak_suppliers_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_suppliers_code", "code ~ '^[A-Z0-9-]{1,20}$'");
                    table.CheckConstraint("ck_suppliers_gstin_state", "gstin IS NULL OR left(gstin, 2) = state_code");
                    table.ForeignKey(
                        name: "fk_suppliers_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_suppliers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "grns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    supplier_invoice_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    supplier_invoice_date = table.Column<DateOnly>(type: "date", nullable: false),
                    classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    purchase_order_reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    is_inter_state = table.Column<bool>(type: "boolean", nullable: false),
                    tax_recoverable = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    gross_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    taxable_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cess_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    round_off = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    invoice_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    expenses_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    landed_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    posted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grns", x => x.id);
                    table.UniqueConstraint("ak_grns_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_grns_classification", "classification IN ('GST_TAX_INVOICE', 'BILL_OF_SUPPLY', 'UNREGISTERED', 'IMPORT', 'REVERSE_CHARGE', 'PENDING_DOCUMENT', 'OTHER')");
                    table.CheckConstraint("ck_grns_posted", "(status = 'POSTED') = (posted_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_grns_status", "status IN ('PENDING_APPROVAL', 'POSTED', 'REJECTED')");
                    table.CheckConstraint("ck_grns_totals", "invoice_total = taxable_total + cgst_total + sgst_total + igst_total + cess_total + round_off AND abs(round_off) <= 1 AND taxable_total = gross_total - discount_total AND cgst_total = sgst_total");
                    table.ForeignKey(
                        name: "fk_grns_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grns_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grns_suppliers_supplier_id_business_id",
                        columns: x => new { x.supplier_id, x.business_id },
                        principalTable: "suppliers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grns_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "grn_expenses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grn_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expense_order = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grn_expenses", x => x.id);
                    table.CheckConstraint("ck_grn_expenses_amount", "amount > 0");
                    table.CheckConstraint("ck_grn_expenses_kind", "kind IN ('FREIGHT', 'LOADING', 'INSURANCE', 'PACKING', 'HANDLING', 'TRANSPORT', 'CUSTOMS', 'OTHER')");
                    table.CheckConstraint("ck_grn_expenses_method", "method IN ('QUANTITY', 'VALUE', 'WEIGHT', 'VOLUME', 'EQUAL', 'MANUAL')");
                    table.ForeignKey(
                        name: "fk_grn_expenses_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grn_expenses_grns_grn_id_business_id",
                        columns: x => new { x.grn_id, x.business_id },
                        principalTable: "grns",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grn_expenses_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "grn_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grn_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    unit_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    factor_to_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    free_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    base_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    mrp = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    rate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    gst_rate_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    cess_rate_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    gross = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    taxable = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cess = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    expense_share = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    non_recoverable_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    landed_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    landed_unit_cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    batch_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    manufactured_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    selling_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    previous_unit_cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    cost_change_percent = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    cost_change_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    loss_leader_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    weight = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    volume = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grn_lines", x => x.id);
                    table.CheckConstraint("ck_grn_lines_amounts", "quantity >= 0 AND free_quantity >= 0 AND quantity + free_quantity > 0 AND factor_to_base > 0 AND rate >= 0 AND discount >= 0 AND taxable = gross - discount AND total = taxable + cgst + sgst + igst + cess AND cgst = sgst AND (cgst = 0 OR igst = 0)");
                    table.CheckConstraint("ck_grn_lines_landed", "landed_total = taxable + non_recoverable_tax + expense_share AND base_quantity = (quantity + free_quantity) * factor_to_base AND landed_unit_cost >= 0");
                    table.ForeignKey(
                        name: "fk_grn_lines_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grn_lines_grns_grn_id_business_id",
                        columns: x => new { x.grn_id, x.business_id },
                        principalTable: "grns",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grn_lines_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grn_lines_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grn_lines_variant_units_variant_unit_id",
                        column: x => x.variant_unit_id,
                        principalTable: "variant_units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "grn_allocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expense_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grn_allocations", x => x.id);
                    table.CheckConstraint("ck_grn_allocations_amount", "amount >= 0");
                    table.ForeignKey(
                        name: "fk_grn_allocations_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grn_allocations_grn_expenses_expense_id",
                        column: x => x.expense_id,
                        principalTable: "grn_expenses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grn_allocations_grn_lines_line_id",
                        column: x => x.line_id,
                        principalTable: "grn_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_grn_allocations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_grn_allocations_business_id_tenant_id",
                table: "grn_allocations",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_grn_allocations_expense_id_line_id",
                table: "grn_allocations",
                columns: new[] { "expense_id", "line_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_grn_allocations_line_id",
                table: "grn_allocations",
                column: "line_id");

            migrationBuilder.CreateIndex(
                name: "ix_grn_allocations_tenant_id",
                table: "grn_allocations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_grn_expenses_business_id_tenant_id",
                table: "grn_expenses",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_grn_expenses_grn_id_business_id",
                table: "grn_expenses",
                columns: new[] { "grn_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_grn_expenses_grn_id_expense_order",
                table: "grn_expenses",
                columns: new[] { "grn_id", "expense_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_grn_expenses_tenant_id",
                table: "grn_expenses",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_grn_lines_business_id_tenant_id",
                table: "grn_lines",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_grn_lines_grn_id_business_id",
                table: "grn_lines",
                columns: new[] { "grn_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_grn_lines_grn_id_line_number",
                table: "grn_lines",
                columns: new[] { "grn_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_grn_lines_tenant_id",
                table: "grn_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_grn_lines_variant_id_business_id",
                table: "grn_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_grn_lines_variant_id_mrp",
                table: "grn_lines",
                columns: new[] { "variant_id", "mrp" });

            migrationBuilder.CreateIndex(
                name: "ix_grn_lines_variant_unit_id",
                table: "grn_lines",
                column: "variant_unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_grns_business_id_idempotency_key",
                table: "grns",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_grns_business_id_tenant_id",
                table: "grns",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_grns_store_id_business_date",
                table: "grns",
                columns: new[] { "store_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_grns_store_id_business_id",
                table: "grns",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_grns_store_id_sequence_number",
                table: "grns",
                columns: new[] { "store_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_grns_supplier_id_business_id",
                table: "grns",
                columns: new[] { "supplier_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_grns_tenant_id",
                table: "grns",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_grns_supplier_invoice",
                table: "grns",
                columns: new[] { "business_id", "supplier_id", "supplier_invoice_number" },
                unique: true,
                filter: "status <> 'REJECTED'");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_settings_business_id_tenant_id",
                table: "purchase_settings",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_settings_tenant_id",
                table: "purchase_settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_suppliers_business_id_code",
                table: "suppliers",
                columns: new[] { "business_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_suppliers_business_id_gstin",
                table: "suppliers",
                columns: new[] { "business_id", "gstin" });

            migrationBuilder.CreateIndex(
                name: "ix_suppliers_business_id_tenant_id",
                table: "suppliers",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_suppliers_tenant_id",
                table: "suppliers",
                column: "tenant_id");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(PurchasesSql.Tables));
            foreach (var table in PurchasesSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Protect(table));
            }

            migrationBuilder.Sql(PurchasesSql.GrnGuard);
            migrationBuilder.Sql(PurchasesSql.SeedSettings);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PurchasesSql.DropGrnGuard);
            foreach (var table in PurchasesSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Unprotect(table));
            }

            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(PurchasesSql.Tables));

            migrationBuilder.DropTable(
                name: "grn_allocations");

            migrationBuilder.DropTable(
                name: "purchase_settings");

            migrationBuilder.DropTable(
                name: "grn_expenses");

            migrationBuilder.DropTable(
                name: "grn_lines");

            migrationBuilder.DropTable(
                name: "grns");

            migrationBuilder.DropTable(
                name: "suppliers");
        }
    }
}
