using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Inventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    manufactured_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_batches", x => x.id);
                    table.UniqueConstraint("ak_batches_id_variant_id", x => new { x.id, x.variant_id });
                    table.CheckConstraint("ck_batches_dates", "manufactured_on IS NULL OR expires_on IS NULL OR expires_on > manufactured_on");
                    table.ForeignKey(
                        name: "fk_batches_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_batches_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_batches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_sequences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    next_number = table.Column<long>(type: "bigint", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_sequences", x => x.id);
                    table.CheckConstraint("ck_document_sequences_positive", "next_number > 0");
                    table.ForeignKey(
                        name: "fk_document_sequences_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_document_sequences_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_document_sequences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_settings",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valuation_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_settings", x => x.business_id);
                    table.CheckConstraint("ck_inventory_settings_method", "valuation_method IN ('FIFO', 'FEFO', 'WEIGHTED_AVERAGE')");
                    table.ForeignKey(
                        name: "fk_inventory_settings_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inventory_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "negative_stock_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    limit_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    superseded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_negative_stock_rules", x => x.id);
                    table.CheckConstraint("ck_negative_stock_rules_limit", "(mode = 'ENABLED_WITH_LIMIT') = (limit_quantity IS NOT NULL AND limit_quantity > 0)");
                    table.CheckConstraint("ck_negative_stock_rules_mode", "mode IN ('DISABLED', 'WARN_OVERRIDE', 'ENABLED_WITH_LIMIT')");
                    table.ForeignKey(
                        name: "fk_negative_stock_rules_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_negative_stock_rules_products_product_id_business_id",
                        columns: x => new { x.product_id, x.business_id },
                        principalTable: "products",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_negative_stock_rules_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_negative_stock_rules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reorder_levels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    minimum_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    reorder_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reorder_levels", x => x.id);
                    table.CheckConstraint("ck_reorder_levels_positive", "minimum_quantity >= 0 AND reorder_quantity >= 0");
                    table.ForeignKey(
                        name: "fk_reorder_levels_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reorder_levels_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reorder_levels_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reorder_levels_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    average_cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    last_cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_balances", x => x.id);
                    table.CheckConstraint("ck_stock_balances_costs", "average_cost >= 0 AND last_cost >= 0");
                    table.ForeignKey(
                        name: "fk_stock_balances_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_balances_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_balances_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_balances_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_store_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    negative_stock_override = table.Column<bool>(type: "boolean", nullable: false),
                    posted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    posted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_documents", x => x.id);
                    table.CheckConstraint("ck_stock_documents_transfer", "(type = 'TRANSFER') = (target_store_id IS NOT NULL) AND (target_store_id IS NULL OR target_store_id <> store_id)");
                    table.CheckConstraint("ck_stock_documents_type", "type IN ('OPENING', 'ADJUSTMENT', 'DAMAGE', 'WASTAGE', 'TRANSFER', 'COUNT')");
                    table.ForeignKey(
                        name: "fk_stock_documents_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_documents_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_documents_stores_target_store_id_business_id",
                        columns: x => new { x.target_store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_documents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cost_layers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    unit_cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    original_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    remaining_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    settled_shortfall = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cost_layers", x => x.id);
                    table.CheckConstraint("ck_cost_layers_cost", "unit_cost >= 0");
                    table.CheckConstraint("ck_cost_layers_quantities", "original_quantity > 0 AND remaining_quantity >= 0 AND settled_shortfall >= 0 AND remaining_quantity + settled_shortfall <= original_quantity");
                    table.ForeignKey(
                        name: "fk_cost_layers_batches_batch_id_variant_id",
                        columns: x => new { x.batch_id, x.variant_id },
                        principalTable: "batches",
                        principalColumns: new[] { "id", "variant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cost_layers_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cost_layers_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cost_layers_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cost_layers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    layer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    movement_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    value = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    document_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_ledger", x => x.id);
                    table.CheckConstraint("ck_stock_ledger_cost", "unit_cost >= 0");
                    table.CheckConstraint("ck_stock_ledger_direction", "(movement_type IN ('OPENING', 'ADJUSTMENT_IN', 'TRANSFER_IN', 'COUNT_GAIN', 'RECEIPT', 'SALE_RETURN') AND quantity > 0) OR (movement_type IN ('ADJUSTMENT_OUT', 'DAMAGE', 'WASTAGE', 'TRANSFER_OUT', 'COUNT_LOSS', 'PURCHASE_RETURN', 'SALE') AND quantity < 0)");
                    table.CheckConstraint("ck_stock_ledger_quantity", "quantity <> 0");
                    table.CheckConstraint("ck_stock_ledger_value", "value = round(quantity * unit_cost, 4)");
                    table.ForeignKey(
                        name: "fk_stock_ledger_batches_batch_id_variant_id",
                        columns: x => new { x.batch_id, x.variant_id },
                        principalTable: "batches",
                        principalColumns: new[] { "id", "variant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_ledger_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_ledger_cost_layers_layer_id",
                        column: x => x.layer_id,
                        principalTable: "cost_layers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_ledger_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_ledger_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_ledger_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_batches_business_id_expires_on",
                table: "batches",
                columns: new[] { "business_id", "expires_on" });

            migrationBuilder.CreateIndex(
                name: "ix_batches_business_id_tenant_id",
                table: "batches",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_batches_tenant_id",
                table: "batches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_batches_variant_id_batch_number",
                table: "batches",
                columns: new[] { "variant_id", "batch_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_batches_variant_id_business_id",
                table: "batches",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cost_layers_batch_id_variant_id",
                table: "cost_layers",
                columns: new[] { "batch_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cost_layers_business_id_tenant_id",
                table: "cost_layers",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cost_layers_open",
                table: "cost_layers",
                columns: new[] { "store_id", "variant_id", "sequence" },
                filter: "remaining_quantity > 0");

            migrationBuilder.CreateIndex(
                name: "ix_cost_layers_sequence",
                table: "cost_layers",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cost_layers_store_id_business_id",
                table: "cost_layers",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cost_layers_tenant_id",
                table: "cost_layers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_cost_layers_variant_id_business_id",
                table: "cost_layers",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_document_sequences_business_id_tenant_id",
                table: "document_sequences",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_document_sequences_store_id_business_id",
                table: "document_sequences",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_document_sequences_store_id_series",
                table: "document_sequences",
                columns: new[] { "store_id", "series" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_sequences_tenant_id",
                table: "document_sequences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_inventory_settings_business_id_tenant_id",
                table: "inventory_settings",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_settings_tenant_id",
                table: "inventory_settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_negative_stock_rules_business_id_tenant_id",
                table: "negative_stock_rules",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_negative_stock_rules_product_id_business_id",
                table: "negative_stock_rules",
                columns: new[] { "product_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_negative_stock_rules_store_id_business_id",
                table: "negative_stock_rules",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_negative_stock_rules_tenant_id",
                table: "negative_stock_rules",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_negative_stock_rules_active_scope",
                table: "negative_stock_rules",
                columns: new[] { "business_id", "store_id", "product_id" },
                unique: true,
                filter: "is_active")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_reorder_levels_business_id_tenant_id",
                table: "reorder_levels",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reorder_levels_store_id_business_id",
                table: "reorder_levels",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reorder_levels_store_id_variant_id",
                table: "reorder_levels",
                columns: new[] { "store_id", "variant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reorder_levels_tenant_id",
                table: "reorder_levels",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_reorder_levels_variant_id_business_id",
                table: "reorder_levels",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_balances_business_id_tenant_id",
                table: "stock_balances",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_balances_store_id_business_id",
                table: "stock_balances",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_balances_store_id_variant_id",
                table: "stock_balances",
                columns: new[] { "store_id", "variant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_balances_tenant_id",
                table: "stock_balances",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_balances_variant_id_business_id",
                table: "stock_balances",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_business_id_idempotency_key",
                table: "stock_documents",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_business_id_tenant_id",
                table: "stock_documents",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_store_id_business_id",
                table: "stock_documents",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_store_id_number",
                table: "stock_documents",
                columns: new[] { "store_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_store_id_posted_at_utc",
                table: "stock_documents",
                columns: new[] { "store_id", "posted_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_target_store_id_business_id",
                table: "stock_documents",
                columns: new[] { "target_store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_documents_tenant_id",
                table: "stock_documents",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_batch_id_variant_id",
                table: "stock_ledger",
                columns: new[] { "batch_id", "variant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_business_id_tenant_id",
                table: "stock_ledger",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_document_type_document_id",
                table: "stock_ledger",
                columns: new[] { "document_type", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_layer_id",
                table: "stock_ledger",
                column: "layer_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_sequence",
                table: "stock_ledger",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_store_id_business_id",
                table: "stock_ledger",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_store_id_variant_id_sequence",
                table: "stock_ledger",
                columns: new[] { "store_id", "variant_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_tenant_id",
                table: "stock_ledger",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_variant_id_business_id",
                table: "stock_ledger",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(InventorySql.Tables));
            migrationBuilder.Sql(AppendOnlySql.Protect("stock_ledger"));
            migrationBuilder.Sql(AppendOnlySql.Protect("stock_documents"));
            migrationBuilder.Sql(InventorySql.CostLayerGuard);
            migrationBuilder.Sql(InventorySql.SeedSettings);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(InventorySql.DropCostLayerGuard);
            migrationBuilder.Sql(AppendOnlySql.Unprotect("stock_documents"));
            migrationBuilder.Sql(AppendOnlySql.Unprotect("stock_ledger"));
            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(InventorySql.Tables));

            migrationBuilder.DropTable(
                name: "document_sequences");

            migrationBuilder.DropTable(
                name: "inventory_settings");

            migrationBuilder.DropTable(
                name: "negative_stock_rules");

            migrationBuilder.DropTable(
                name: "reorder_levels");

            migrationBuilder.DropTable(
                name: "stock_balances");

            migrationBuilder.DropTable(
                name: "stock_documents");

            migrationBuilder.DropTable(
                name: "stock_ledger");

            migrationBuilder.DropTable(
                name: "cost_layers");

            migrationBuilder.DropTable(
                name: "batches");
        }
    }
}
