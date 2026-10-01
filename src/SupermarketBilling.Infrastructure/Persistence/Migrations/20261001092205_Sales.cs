using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Sales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "counters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_counters", x => x.id);
                    table.UniqueConstraint("ak_counters_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_counters_code", "code ~ '^[A-Z0-9]{1,6}$'");
                    table.ForeignKey(
                        name: "fk_counters_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_counters_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_counters_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "counter_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    enrolled_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrolled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_counter_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_counter_devices_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_counter_devices_counters_counter_id_business_id",
                        columns: x => new { x.counter_id, x.business_id },
                        principalTable: "counters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_counter_devices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    number_prefix = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tax_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    channel = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cashier_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    seller_gstin = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    seller_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    seller_state_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    buyer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    buyer_gstin = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    buyer_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    buyer_address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    place_of_supply_state_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    is_inter_state = table.Column<bool>(type: "boolean", nullable: false),
                    gross_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    taxable_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cess_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    round_off = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    paid_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    change_due = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    discount_approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    negative_stock_override = table.Column<bool>(type: "boolean", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_invoices", x => x.id);
                    table.UniqueConstraint("ak_sales_invoices_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_sales_invoices_amounts", "gross_total >= 0 AND discount_total >= 0 AND taxable_total >= 0 AND cgst_total >= 0 AND igst_total >= 0 AND cess_total >= 0");
                    table.CheckConstraint("ck_sales_invoices_channel", "channel IN ('RETAIL', 'WHOLESALE')");
                    table.CheckConstraint("ck_sales_invoices_composition_intra_state", "tax_mode <> 'GST_COMPOSITION' OR NOT is_inter_state");
                    table.CheckConstraint("ck_sales_invoices_gst_split", "cgst_total = sgst_total AND (CASE WHEN is_inter_state THEN cgst_total = 0 ELSE igst_total = 0 END)");
                    table.CheckConstraint("ck_sales_invoices_kind", "kind IN ('TAX_INVOICE', 'BILL_OF_SUPPLY', 'INVOICE')");
                    table.CheckConstraint("ck_sales_invoices_kind_mode", "(tax_mode = 'GST_REGULAR' AND kind IN ('TAX_INVOICE', 'BILL_OF_SUPPLY')) OR (tax_mode = 'GST_COMPOSITION' AND kind = 'BILL_OF_SUPPLY') OR (tax_mode = 'NOT_GST_REGISTERED' AND kind = 'INVOICE')");
                    table.CheckConstraint("ck_sales_invoices_number", "char_length(number) <= 16 AND number_prefix ~ '^[A-Z0-9]{1,7}$' AND sequence_number > 0 AND number = number_prefix || '-' || CASE WHEN sequence_number < 1000000 THEN lpad(sequence_number::text, 6, '0') ELSE sequence_number::text END");
                    table.CheckConstraint("ck_sales_invoices_only_regular_collects_tax", "tax_mode = 'GST_REGULAR' OR (cgst_total = 0 AND sgst_total = 0 AND igst_total = 0 AND cess_total = 0)");
                    table.CheckConstraint("ck_sales_invoices_paid", "change_due >= 0 AND paid_total - change_due = grand_total");
                    table.CheckConstraint("ck_sales_invoices_tax_mode", "tax_mode IN ('GST_REGULAR', 'GST_COMPOSITION', 'NOT_GST_REGISTERED')");
                    table.CheckConstraint("ck_sales_invoices_total", "grand_total = taxable_total + cgst_total + sgst_total + igst_total + cess_total + round_off AND abs(round_off) <= 0.5 AND grand_total = round(grand_total)");
                    table.ForeignKey(
                        name: "fk_sales_invoices_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoices_counters_counter_id_business_id",
                        columns: x => new { x.counter_id, x.business_id },
                        principalTable: "counters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoices_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supervisor_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    variant_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    max_discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supervisor_approvals", x => x.id);
                    table.CheckConstraint("ck_supervisor_approvals_kind", "(kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_discount IS NULL) OR (kind = 'DISCOUNT' AND variant_unit_id IS NULL AND approved_price IS NULL AND max_discount > 0)");
                    table.CheckConstraint("ck_supervisor_approvals_two_people", "approved_by_user_id <> requested_by_user_id");
                    table.CheckConstraint("ck_supervisor_approvals_use", "(used_at_utc IS NULL) = (used_invoice_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_supervisor_approvals_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supervisor_approvals_counters_counter_id_business_id",
                        columns: x => new { x.counter_id, x.business_id },
                        principalTable: "counters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_supervisor_approvals_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_invoice_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    hsn_sac = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    unit_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    base_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    mrp = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    price_rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rate_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    price_override_approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tax_inclusive = table.Column<bool>(type: "boolean", nullable: false),
                    supply_type = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    gst_rate_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    cess_rate_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    gross = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    item_discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    bill_discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    taxable = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cess = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cost_of_goods = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_invoice_lines", x => x.id);
                    table.CheckConstraint("ck_sales_invoice_lines_amounts", "quantity > 0 AND base_quantity > 0 AND unit_price >= 0 AND item_discount >= 0 AND bill_discount >= 0 AND taxable >= 0 AND cgst >= 0 AND igst >= 0 AND cess >= 0");
                    table.CheckConstraint("ck_sales_invoice_lines_net", "gross - item_discount - bill_discount = CASE WHEN tax_inclusive OR cgst + sgst + igst + cess = 0 THEN total ELSE taxable END");
                    table.CheckConstraint("ck_sales_invoice_lines_price_source", "(rate_type = 'OVERRIDE' AND price_override_approval_id IS NOT NULL AND price_rule_id IS NULL) OR (rate_type = 'OVERRIDE_SELF' AND price_override_approval_id IS NULL AND price_rule_id IS NULL) OR (rate_type NOT IN ('OVERRIDE', 'OVERRIDE_SELF') AND price_rule_id IS NOT NULL AND price_override_approval_id IS NULL)");
                    table.CheckConstraint("ck_sales_invoice_lines_supply_type", "supply_type IN ('TAXABLE', 'EXEMPT', 'NIL_RATED', 'NON_GST')");
                    table.CheckConstraint("ck_sales_invoice_lines_total", "total = taxable + cgst + sgst + igst + cess AND cgst = sgst AND (cgst = 0 OR igst = 0)");
                    table.CheckConstraint("ck_sales_invoice_lines_untaxed", "supply_type = 'TAXABLE' OR (cgst = 0 AND igst = 0 AND cess = 0)");
                    table.ForeignKey(
                        name: "fk_sales_invoice_lines_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoice_lines_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoice_lines_sales_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "sales_invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoice_lines_sales_invoices_invoice_id_business_id",
                        columns: x => new { x.invoice_id, x.business_id },
                        principalTable: "sales_invoices",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoice_lines_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoice_lines_variant_units_variant_unit_id",
                        column: x => x.variant_unit_id,
                        principalTable: "variant_units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_invoice_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_order = table.Column<int>(type: "integer", nullable: false),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_invoice_payments", x => x.id);
                    table.CheckConstraint("ck_sales_invoice_payments_amount", "amount > 0");
                    table.CheckConstraint("ck_sales_invoice_payments_method", "method IN ('CASH', 'CARD', 'UPI', 'WALLET')");
                    table.ForeignKey(
                        name: "fk_sales_invoice_payments_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoice_payments_sales_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "sales_invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoice_payments_sales_invoices_invoice_id_business_id",
                        columns: x => new { x.invoice_id, x.business_id },
                        principalTable: "sales_invoices",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoice_payments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_counter_devices_business_id_tenant_id",
                table: "counter_devices",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_counter_devices_counter_id_business_id",
                table: "counter_devices",
                columns: new[] { "counter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_counter_devices_tenant_id",
                table: "counter_devices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_counter_devices_token_hash",
                table: "counter_devices",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_counters_business_id_code",
                table: "counters",
                columns: new[] { "business_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_counters_business_id_tenant_id",
                table: "counters",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_counters_store_id_business_id",
                table: "counters",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_counters_tenant_id",
                table: "counters",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_lines_business_id_tenant_id",
                table: "sales_invoice_lines",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_lines_invoice_id_business_id",
                table: "sales_invoice_lines",
                columns: new[] { "invoice_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_lines_invoice_id_line_number",
                table: "sales_invoice_lines",
                columns: new[] { "invoice_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_lines_tenant_id",
                table: "sales_invoice_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_lines_variant_id_business_id",
                table: "sales_invoice_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_lines_variant_id_invoice_id",
                table: "sales_invoice_lines",
                columns: new[] { "variant_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_lines_variant_unit_id",
                table: "sales_invoice_lines",
                column: "variant_unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_payments_business_id_tenant_id",
                table: "sales_invoice_payments",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_payments_invoice_id_business_id",
                table: "sales_invoice_payments",
                columns: new[] { "invoice_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_payments_invoice_id_payment_order",
                table: "sales_invoice_payments",
                columns: new[] { "invoice_id", "payment_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_payments_tenant_id",
                table: "sales_invoice_payments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_business_id_idempotency_key",
                table: "sales_invoices",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_business_id_number",
                table: "sales_invoices",
                columns: new[] { "business_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_business_id_tenant_id",
                table: "sales_invoices",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_counter_id_business_id",
                table: "sales_invoices",
                columns: new[] { "counter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_counter_id_number_prefix_sequence_number",
                table: "sales_invoices",
                columns: new[] { "counter_id", "number_prefix", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_store_id_business_date",
                table: "sales_invoices",
                columns: new[] { "store_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_store_id_business_id",
                table: "sales_invoices",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_tenant_id",
                table: "sales_invoices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_supervisor_approvals_business_id_tenant_id",
                table: "supervisor_approvals",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supervisor_approvals_counter_id_business_id",
                table: "supervisor_approvals",
                columns: new[] { "counter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_supervisor_approvals_tenant_id",
                table: "supervisor_approvals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_supervisor_approvals_token_hash",
                table: "supervisor_approvals",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supervisor_approvals_used_invoice_id",
                table: "supervisor_approvals",
                column: "used_invoice_id");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(SalesSql.Tables));
            foreach (var table in SalesSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Protect(table));
            }

            migrationBuilder.Sql(SalesSql.Guards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SalesSql.DropGuards);
            foreach (var table in SalesSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Unprotect(table));
            }

            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(SalesSql.Tables));

            migrationBuilder.DropTable(
                name: "counter_devices");

            migrationBuilder.DropTable(
                name: "sales_invoice_lines");

            migrationBuilder.DropTable(
                name: "sales_invoice_payments");

            migrationBuilder.DropTable(
                name: "supervisor_approvals");

            migrationBuilder.DropTable(
                name: "sales_invoices");

            migrationBuilder.DropTable(
                name: "counters");
        }
    }
}
