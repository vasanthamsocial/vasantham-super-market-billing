using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Catalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "require_price_approval",
                table: "businesses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "brands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brands", x => x.id);
                    table.UniqueConstraint("ak_brands_id_business_id", x => new { x.id, x.business_id });
                    table.ForeignKey(
                        name: "fk_brands_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_brands_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.UniqueConstraint("ak_categories_id_business_id", x => new { x.id, x.business_id });
                    table.ForeignKey(
                        name: "fk_categories_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_categories_categories_parent_id_business_id",
                        columns: x => new { x.parent_id, x.business_id },
                        principalTable: "categories",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_categories_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_groups", x => x.id);
                    table.UniqueConstraint("ak_customer_groups_id_business_id", x => new { x.id, x.business_id });
                    table.ForeignKey(
                        name: "fk_customer_groups_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_groups_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tax_registrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    gstin = table.Column<string>(type: "character(15)", fixedLength: true, maxLength: 15, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    evidence_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_registrations", x => x.id);
                    table.CheckConstraint("ck_tax_registrations_gstin", "(mode = 'NOT_GST_REGISTERED') = (gstin IS NULL)");
                    table.CheckConstraint("ck_tax_registrations_mode", "mode IN ('GST_REGULAR', 'GST_COMPOSITION', 'NOT_GST_REGISTERED')");
                    table.ForeignKey(
                        name: "fk_tax_registrations_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tax_registrations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "units",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    decimal_places = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_units", x => x.id);
                    table.UniqueConstraint("ak_units_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_units_decimal_places", "decimal_places BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "fk_units_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_units_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    print_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: true),
                    base_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hsn_sac = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    supply_type = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    gst_rate_percent = table.Column<decimal>(type: "numeric(7,3)", precision: 7, scale: 3, nullable: false),
                    cess_rate_percent = table.Column<decimal>(type: "numeric(7,3)", precision: 7, scale: 3, nullable: false),
                    is_weighed = table.Column<bool>(type: "boolean", nullable: false),
                    tracks_batches = table.Column<bool>(type: "boolean", nullable: false),
                    tracks_expiry = table.Column<bool>(type: "boolean", nullable: false),
                    tracks_serials = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.UniqueConstraint("ak_products_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_products_expiry_needs_batches", "NOT tracks_expiry OR tracks_batches");
                    table.CheckConstraint("ck_products_hsn", "hsn_sac ~ '^([0-9]{4}|[0-9]{6}|[0-9]{8})$'");
                    table.CheckConstraint("ck_products_rates", "(supply_type = 'TAXABLE' AND gst_rate_percent > 0 AND gst_rate_percent <= 100 AND cess_rate_percent >= 0) OR (supply_type <> 'TAXABLE' AND gst_rate_percent = 0 AND cess_rate_percent = 0)");
                    table.CheckConstraint("ck_products_supply_type", "supply_type IN ('TAXABLE', 'EXEMPT', 'NIL_RATED', 'NON_GST')");
                    table.ForeignKey(
                        name: "fk_products_brands_brand_id_business_id",
                        columns: x => new { x.brand_id, x.business_id },
                        principalTable: "brands",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_products_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_products_categories_category_id_business_id",
                        columns: x => new { x.category_id, x.business_id },
                        principalTable: "categories",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_products_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_products_units_base_unit_id_business_id",
                        columns: x => new { x.base_unit_id, x.business_id },
                        principalTable: "units",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_variants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_variants", x => x.id);
                    table.UniqueConstraint("ak_product_variants_id_business_id", x => new { x.id, x.business_id });
                    table.ForeignKey(
                        name: "fk_product_variants_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_variants_products_product_id_business_id",
                        columns: x => new { x.product_id, x.business_id },
                        principalTable: "products",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_variants_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "variant_units",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    factor_to_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    is_base = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_variant_units", x => x.id);
                    table.UniqueConstraint("ak_variant_units_id_variant_id", x => new { x.id, x.variant_id });
                    table.CheckConstraint("ck_variant_units_factor", "factor_to_base > 0 AND (NOT is_base OR factor_to_base = 1)");
                    table.ForeignKey(
                        name: "fk_variant_units_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_variant_units_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_variant_units_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_variant_units_units_unit_id_business_id",
                        columns: x => new { x.unit_id, x.business_id },
                        principalTable: "units",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "price_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rate_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    channel = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    tax_inclusive = table.Column<bool>(type: "boolean", nullable: false),
                    mrp = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    store_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    members_only = table.Column<bool>(type: "boolean", nullable: false),
                    min_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    max_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    valid_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retired_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retired_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_rules", x => x.id);
                    table.CheckConstraint("ck_price_rules_channel", "channel IN ('RETAIL', 'WHOLESALE', 'ANY')");
                    table.CheckConstraint("ck_price_rules_price", "price > 0 AND (mrp IS NULL OR mrp > 0)");
                    table.CheckConstraint("ck_price_rules_quantity", "min_quantity >= 0 AND (max_quantity IS NULL OR max_quantity > min_quantity)");
                    table.CheckConstraint("ck_price_rules_rate_type", "rate_type IN ('STANDARD', 'STORE', 'QUANTITY_SLAB', 'MEMBER', 'CUSTOMER_GROUP', 'PROMOTIONAL', 'MINIMUM')");
                    table.CheckConstraint("ck_price_rules_retirement", "(status = 'RETIRED') = (retired_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_price_rules_status", "status IN ('PENDING_APPROVAL', 'ACTIVE', 'REJECTED', 'RETIRED')");
                    table.CheckConstraint("ck_price_rules_validity", "valid_to_utc IS NULL OR valid_to_utc > valid_from_utc");
                    table.ForeignKey(
                        name: "fk_price_rules_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_price_rules_customer_groups_customer_group_id_business_id",
                        columns: x => new { x.customer_group_id, x.business_id },
                        principalTable: "customer_groups",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_price_rules_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_price_rules_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_price_rules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_price_rules_variant_units_variant_unit_id_variant_id",
                        columns: x => new { x.variant_unit_id, x.variant_id },
                        principalTable: "variant_units",
                        principalColumns: new[] { "id", "variant_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "variant_barcodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_variant_barcodes", x => x.id);
                    table.CheckConstraint("ck_variant_barcodes_type", "type IN ('GS1', 'INTERNAL')");
                    table.ForeignKey(
                        name: "fk_variant_barcodes_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_variant_barcodes_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_variant_barcodes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_variant_barcodes_variant_units_variant_unit_id_variant_id",
                        columns: x => new { x.variant_unit_id, x.variant_id },
                        principalTable: "variant_units",
                        principalColumns: new[] { "id", "variant_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "variant_mrps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mrp = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_variant_mrps", x => x.id);
                    table.CheckConstraint("ck_variant_mrps_positive", "mrp > 0");
                    table.ForeignKey(
                        name: "fk_variant_mrps_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_variant_mrps_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_variant_mrps_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_variant_mrps_variant_units_variant_unit_id_variant_id",
                        columns: x => new { x.variant_unit_id, x.variant_id },
                        principalTable: "variant_units",
                        principalColumns: new[] { "id", "variant_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_brands_business_id_name",
                table: "brands",
                columns: new[] { "business_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_brands_business_id_tenant_id",
                table: "brands",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_brands_tenant_id",
                table: "brands",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_categories_business_id_parent_id_name",
                table: "categories",
                columns: new[] { "business_id", "parent_id", "name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_categories_business_id_tenant_id",
                table: "categories",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_categories_parent_id_business_id",
                table: "categories",
                columns: new[] { "parent_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_categories_tenant_id",
                table: "categories",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_groups_business_id_code",
                table: "customer_groups",
                columns: new[] { "business_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customer_groups_business_id_tenant_id",
                table: "customer_groups",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_groups_tenant_id",
                table: "customer_groups",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_rules_business_id_status",
                table: "price_rules",
                columns: new[] { "business_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_price_rules_business_id_tenant_id",
                table: "price_rules",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_price_rules_customer_group_id_business_id",
                table: "price_rules",
                columns: new[] { "customer_group_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_price_rules_store_id_business_id",
                table: "price_rules",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_price_rules_tenant_id",
                table: "price_rules",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_rules_variant_id_business_id",
                table: "price_rules",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_price_rules_variant_unit_id_status",
                table: "price_rules",
                columns: new[] { "variant_unit_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_price_rules_variant_unit_id_variant_id",
                table: "price_rules",
                columns: new[] { "variant_unit_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_business_id_code",
                table: "product_variants",
                columns: new[] { "business_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_business_id_tenant_id",
                table: "product_variants",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_product_id_business_id",
                table: "product_variants",
                columns: new[] { "product_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_tenant_id",
                table: "product_variants",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_base_unit_id_business_id",
                table: "products",
                columns: new[] { "base_unit_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_brand_id_business_id",
                table: "products",
                columns: new[] { "brand_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_business_id_code",
                table: "products",
                columns: new[] { "business_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_business_id_name",
                table: "products",
                columns: new[] { "business_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_products_business_id_tenant_id",
                table: "products",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_category_id_business_id",
                table: "products",
                columns: new[] { "category_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id",
                table: "products",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_tax_registrations_business_id_effective_from",
                table: "tax_registrations",
                columns: new[] { "business_id", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tax_registrations_business_id_tenant_id",
                table: "tax_registrations",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tax_registrations_tenant_id",
                table: "tax_registrations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_units_business_id_code",
                table: "units",
                columns: new[] { "business_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_units_business_id_tenant_id",
                table: "units",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_units_tenant_id",
                table: "units",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_variant_barcodes_business_id_tenant_id",
                table: "variant_barcodes",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_variant_barcodes_tenant_id",
                table: "variant_barcodes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_variant_barcodes_variant_id_business_id",
                table: "variant_barcodes",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_variant_barcodes_variant_unit_id_variant_id",
                table: "variant_barcodes",
                columns: new[] { "variant_unit_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "ux_variant_barcodes_active_code",
                table: "variant_barcodes",
                columns: new[] { "business_id", "code" },
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_variant_mrps_business_id_tenant_id",
                table: "variant_mrps",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_variant_mrps_tenant_id",
                table: "variant_mrps",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_variant_mrps_variant_id_business_id",
                table: "variant_mrps",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_variant_mrps_variant_unit_id_variant_id",
                table: "variant_mrps",
                columns: new[] { "variant_unit_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "ux_variant_mrps_active",
                table: "variant_mrps",
                columns: new[] { "variant_unit_id", "mrp" },
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_variant_units_business_id_tenant_id",
                table: "variant_units",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_variant_units_tenant_id",
                table: "variant_units",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_variant_units_unit_id_business_id",
                table: "variant_units",
                columns: new[] { "unit_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_variant_units_variant_id_business_id",
                table: "variant_units",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_variant_units_variant_id_unit_id",
                table: "variant_units",
                columns: new[] { "variant_id", "unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_variant_units_one_base",
                table: "variant_units",
                column: "variant_id",
                unique: true,
                filter: "is_base");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(CatalogSql.Tables));
            migrationBuilder.Sql(AppendOnlySql.Protect("tax_registrations"));
            migrationBuilder.Sql(CatalogSql.PriceRuleGuard);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CatalogSql.DropPriceRuleGuard);
            migrationBuilder.Sql(AppendOnlySql.Unprotect("tax_registrations"));
            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(CatalogSql.Tables));

            migrationBuilder.DropTable(
                name: "price_rules");

            migrationBuilder.DropTable(
                name: "tax_registrations");

            migrationBuilder.DropTable(
                name: "variant_barcodes");

            migrationBuilder.DropTable(
                name: "variant_mrps");

            migrationBuilder.DropTable(
                name: "customer_groups");

            migrationBuilder.DropTable(
                name: "variant_units");

            migrationBuilder.DropTable(
                name: "product_variants");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "brands");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "units");

            migrationBuilder.DropColumn(
                name: "require_price_approval",
                table: "businesses");
        }
    }
}
