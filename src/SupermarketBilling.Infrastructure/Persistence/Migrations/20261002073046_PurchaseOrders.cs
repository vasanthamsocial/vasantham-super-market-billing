using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "purchase_order_id",
                table: "grns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "update_selling_price",
                table: "grn_lines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachments", x => x.id);
                    table.CheckConstraint("ck_attachments_owner", "owner_type IN ('GRN')");
                    table.CheckConstraint("ck_attachments_size", "size > 0 AND size <= 10485760 AND size = octet_length(content)");
                    table.CheckConstraint("ck_attachments_type", "content_type IN ('application/pdf', 'image/jpeg', 'image/png')");
                    table.ForeignKey(
                        name: "fk_attachments_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attachments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    order_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expected_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_orders", x => x.id);
                    table.UniqueConstraint("ak_purchase_orders_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_purchase_orders_closed", "(status = 'OPEN') = (closed_at_utc IS NULL)");
                    table.CheckConstraint("ck_purchase_orders_dates", "expected_date IS NULL OR expected_date >= order_date");
                    table.CheckConstraint("ck_purchase_orders_status", "status IN ('OPEN', 'CLOSED', 'CANCELLED')");
                    table.ForeignKey(
                        name: "fk_purchase_orders_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_orders_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_orders_suppliers_supplier_id_business_id",
                        columns: x => new { x.supplier_id, x.business_id },
                        principalTable: "suppliers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_orders_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_order_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_order_lines", x => x.id);
                    table.CheckConstraint("ck_purchase_order_lines_quantity", "quantity > 0 AND (rate IS NULL OR rate >= 0)");
                    table.ForeignKey(
                        name: "fk_purchase_order_lines_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_order_lines_product_variants_variant_id_business_id",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_order_lines_purchase_orders_purchase_order_id_busi",
                        columns: x => new { x.purchase_order_id, x.business_id },
                        principalTable: "purchase_orders",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_order_lines_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_order_lines_variant_units_variant_unit_id",
                        column: x => x.variant_unit_id,
                        principalTable: "variant_units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_grns_purchase_order_id_business_id",
                table: "grns",
                columns: new[] { "purchase_order_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_business_id_tenant_id",
                table: "attachments",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_owner_type_owner_id",
                table: "attachments",
                columns: new[] { "owner_type", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_tenant_id",
                table: "attachments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_lines_business_id_tenant_id",
                table: "purchase_order_lines",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_lines_purchase_order_id_business_id",
                table: "purchase_order_lines",
                columns: new[] { "purchase_order_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_lines_purchase_order_id_line_number",
                table: "purchase_order_lines",
                columns: new[] { "purchase_order_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_lines_purchase_order_id_variant_unit_id",
                table: "purchase_order_lines",
                columns: new[] { "purchase_order_id", "variant_unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_lines_tenant_id",
                table: "purchase_order_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_lines_variant_id_business_id",
                table: "purchase_order_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_lines_variant_unit_id",
                table: "purchase_order_lines",
                column: "variant_unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_business_id_tenant_id",
                table: "purchase_orders",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_store_id_business_id",
                table: "purchase_orders",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_store_id_sequence_number",
                table: "purchase_orders",
                columns: new[] { "store_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_store_id_status",
                table: "purchase_orders",
                columns: new[] { "store_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_supplier_id_business_id",
                table: "purchase_orders",
                columns: new[] { "supplier_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_tenant_id",
                table: "purchase_orders",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "fk_grns_purchase_orders_purchase_order_id_business_id",
                table: "grns",
                columns: new[] { "purchase_order_id", "business_id" },
                principalTable: "purchase_orders",
                principalColumns: new[] { "id", "business_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(PurchaseOrdersSql.Tables));
            foreach (var table in PurchaseOrdersSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Protect(table));
            }

            migrationBuilder.Sql(PurchaseOrdersSql.OrderGuard);
            migrationBuilder.Sql(PurchaseOrdersSql.GrnGuardWithOrder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PurchaseOrdersSql.GrnGuardWithoutOrder);
            migrationBuilder.Sql(PurchaseOrdersSql.DropOrderGuard);
            foreach (var table in PurchaseOrdersSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Unprotect(table));
            }

            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(PurchaseOrdersSql.Tables));

            migrationBuilder.DropForeignKey(
                name: "fk_grns_purchase_orders_purchase_order_id_business_id",
                table: "grns");

            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.DropTable(
                name: "purchase_order_lines");

            migrationBuilder.DropTable(
                name: "purchase_orders");

            migrationBuilder.DropIndex(
                name: "ix_grns_purchase_order_id_business_id",
                table: "grns");

            migrationBuilder.DropColumn(
                name: "purchase_order_id",
                table: "grns");

            migrationBuilder.DropColumn(
                name: "update_selling_price",
                table: "grn_lines");
        }
    }
}
