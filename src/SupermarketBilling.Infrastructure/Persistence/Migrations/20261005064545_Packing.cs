using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Packing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "delivered_on",
                table: "consignments",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delivery_note",
                table: "consignments",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delivery_outcome",
                table: "consignments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivery_reported_at_utc",
                table: "consignments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "delivery_reported_by_user_id",
                table: "consignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "return_recorded_at_utc",
                table: "consignments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "return_recorded_by_user_id",
                table: "consignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "packing_challans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    party_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    picked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    picked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    checked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    checked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    packed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    package_count = table.Column<int>(type: "integer", nullable: false),
                    cancel_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_packing_challans", x => x.id);
                    table.UniqueConstraint("ak_packing_challans_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_packing_challans_status", "status IN ('OPEN', 'CANCELLED') AND (status = 'CANCELLED') = (cancel_reason IS NOT NULL)");
                    table.CheckConstraint("ck_packing_challans_steps", "(checked_by_user_id IS NULL OR (picked_by_user_id IS NOT NULL AND checked_by_user_id <> picked_by_user_id)) AND (packed_by_user_id IS NULL OR checked_by_user_id IS NOT NULL) AND package_count BETWEEN 0 AND 99999 AND (picked_by_user_id IS NULL) = (picked_at_utc IS NULL) AND (checked_by_user_id IS NULL) = (checked_at_utc IS NULL)");
                    table.ForeignKey(
                        name: "fk_packing_challans_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_challans_sales_invoices_invoice_id_business_id",
                        columns: x => new { x.invoice_id, x.business_id },
                        principalTable: "sales_invoices",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_challans_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_challans_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_challans_users_checked_by_user_id",
                        column: x => x.checked_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_challans_users_packed_by_user_id",
                        column: x => x.packed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_challans_users_picked_by_user_id",
                        column: x => x.picked_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "packing_challan_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    challan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    item_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    variant_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    unit_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    free_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    picked_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    checked_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    short_reason = table.Column<string>(type: "character varying(420)", maxLength: 420, nullable: true),
                    packed_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_packing_challan_lines", x => x.id);
                    table.UniqueConstraint("ak_packing_challan_lines_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_packing_challan_lines_quantities", "quantity > 0 AND free_quantity >= 0 AND (picked_quantity IS NULL OR picked_quantity BETWEEN 0 AND quantity) AND (checked_quantity IS NULL OR (picked_quantity IS NOT NULL AND checked_quantity BETWEEN 0 AND picked_quantity)) AND packed_quantity >= 0 AND packed_quantity <= coalesce(checked_quantity, 0) AND (short_reason IS NOT NULL OR ((picked_quantity IS NULL OR picked_quantity = quantity) AND (checked_quantity IS NULL OR checked_quantity = picked_quantity)))");
                    table.ForeignKey(
                        name: "fk_packing_challan_lines_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_challan_lines_packing_challans_challan_id_business_",
                        columns: x => new { x.challan_id, x.business_id },
                        principalTable: "packing_challans",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_challan_lines_sales_invoice_lines_invoice_line_id",
                        column: x => x.invoice_line_id,
                        principalTable: "sales_invoice_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_challan_lines_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "packing_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    challan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_packing_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_packing_events_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_events_packing_challans_challan_id_business_id",
                        columns: x => new { x.challan_id, x.business_id },
                        principalTable: "packing_challans",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_packing_events_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consignment_lines",
                columns: table => new
                {
                    consignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    challan_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    delivered_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    returned_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consignment_lines", x => new { x.consignment_id, x.challan_line_id });
                    table.CheckConstraint("ck_consignment_lines_quantities", "quantity > 0 AND (delivered_quantity IS NULL OR delivered_quantity BETWEEN 0 AND quantity) AND (returned_quantity IS NULL OR (delivered_quantity IS NOT NULL AND returned_quantity >= 0 AND delivered_quantity + returned_quantity <= quantity))");
                    table.ForeignKey(
                        name: "fk_consignment_lines_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignment_lines_consignments_consignment_id_business_id",
                        columns: x => new { x.consignment_id, x.business_id },
                        principalTable: "consignments",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignment_lines_packing_challan_lines_challan_line_id_bus",
                        columns: x => new { x.challan_line_id, x.business_id },
                        principalTable: "packing_challan_lines",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignment_lines_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_consignments_delivery",
                table: "consignments",
                sql: "(delivery_outcome IS NULL OR delivery_outcome IN ('DELIVERED', 'PARTLY_DELIVERED', 'FAILED')) AND ((delivery_outcome IS NULL) = (delivered_on IS NULL) AND (delivery_outcome IS NULL) = (delivery_reported_at_utc IS NULL)) AND (delivery_outcome IS NULL OR delivery_outcome = 'DELIVERED' OR delivery_note IS NOT NULL) AND (delivered_on IS NULL OR delivered_on >= dispatch_date) AND (return_recorded_at_utc IS NULL OR delivery_outcome IN ('PARTLY_DELIVERED', 'FAILED')) AND (status = 'DISPATCHED' OR delivery_outcome IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_consignment_lines_business_id_tenant_id",
                table: "consignment_lines",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consignment_lines_challan_line_id",
                table: "consignment_lines",
                column: "challan_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_consignment_lines_challan_line_id_business_id",
                table: "consignment_lines",
                columns: new[] { "challan_line_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consignment_lines_consignment_id_business_id",
                table: "consignment_lines",
                columns: new[] { "consignment_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consignment_lines_tenant_id",
                table: "consignment_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_packing_challan_lines_business_id_tenant_id",
                table: "packing_challan_lines",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_packing_challan_lines_challan_id_business_id",
                table: "packing_challan_lines",
                columns: new[] { "challan_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_packing_challan_lines_challan_id_line_number",
                table: "packing_challan_lines",
                columns: new[] { "challan_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_packing_challan_lines_invoice_line_id",
                table: "packing_challan_lines",
                column: "invoice_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_packing_challan_lines_tenant_id",
                table: "packing_challan_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_packing_challans_business_id_number",
                table: "packing_challans",
                columns: new[] { "business_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_packing_challans_business_id_tenant_id",
                table: "packing_challans",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_packing_challans_checked_by_user_id",
                table: "packing_challans",
                column: "checked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_packing_challans_invoice_id_business_id",
                table: "packing_challans",
                columns: new[] { "invoice_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_packing_challans_packed_by_user_id",
                table: "packing_challans",
                column: "packed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_packing_challans_picked_by_user_id",
                table: "packing_challans",
                column: "picked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_packing_challans_store_id_business_id",
                table: "packing_challans",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_packing_challans_tenant_id",
                table: "packing_challans",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_packing_challans_open_invoice",
                table: "packing_challans",
                column: "invoice_id",
                unique: true,
                filter: "status = 'OPEN'");

            migrationBuilder.CreateIndex(
                name: "ix_packing_events_business_id_tenant_id",
                table: "packing_events",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_packing_events_challan_id",
                table: "packing_events",
                column: "challan_id");

            migrationBuilder.CreateIndex(
                name: "ix_packing_events_challan_id_business_id",
                table: "packing_events",
                columns: new[] { "challan_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_packing_events_tenant_id",
                table: "packing_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_packing_events_user_id",
                table: "packing_events",
                column: "user_id");

            migrationBuilder.Sql(PackingSql.Backfill);
            migrationBuilder.Sql(TenancySql.ProtectTenantTables(PackingSql.Tables));
            migrationBuilder.Sql(AppendOnlySql.Protect("packing_events"));
            migrationBuilder.Sql(PackingSql.Guards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PackingSql.DropGuards);
            migrationBuilder.Sql(DispatchSql.DropGuards);
            migrationBuilder.Sql(DispatchSql.Guards);
            migrationBuilder.Sql(AppendOnlySql.Unprotect("packing_events"));
            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(PackingSql.Tables));

            migrationBuilder.DropTable(
                name: "consignment_lines");

            migrationBuilder.DropTable(
                name: "packing_events");

            migrationBuilder.DropTable(
                name: "packing_challan_lines");

            migrationBuilder.DropTable(
                name: "packing_challans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_consignments_delivery",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "delivered_on",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "delivery_note",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "delivery_outcome",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "delivery_reported_at_utc",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "delivery_reported_by_user_id",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "return_recorded_at_utc",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "return_recorded_by_user_id",
                table: "consignments");
        }
    }
}
