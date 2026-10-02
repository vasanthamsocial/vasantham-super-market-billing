using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "purchase_returns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grn_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_inter_state = table.Column<bool>(type: "boolean", nullable: false),
                    tax_recoverable = table.Column<bool>(type: "boolean", nullable: false),
                    taxable = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cess = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    round_off = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    stock_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_returns", x => x.id);
                    table.UniqueConstraint("ak_purchase_returns_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_purchase_returns_amounts", "taxable >= 0 AND cgst >= 0 AND sgst >= 0 AND igst >= 0 AND cess >= 0 AND stock_value >= 0 AND total = taxable + cgst + sgst + igst + cess + round_off");
                    table.CheckConstraint("ck_purchase_returns_tax_kind", "(is_inter_state AND cgst = 0 AND sgst = 0) OR (NOT is_inter_state AND igst = 0)");
                    table.ForeignKey(
                        name: "fk_purchase_returns_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_returns_grns_grn_id_business_id",
                        columns: x => new { x.grn_id, x.business_id },
                        principalTable: "grns",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_returns_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_returns_suppliers_supplier_id_business_id",
                        columns: x => new { x.supplier_id, x.business_id },
                        principalTable: "suppliers",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_returns_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_return_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_return_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grn_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    unit_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    base_quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    taxable = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cgst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    sgst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    igst = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cess = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    stock_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_return_lines", x => x.id);
                    table.CheckConstraint("ck_purchase_return_lines_amounts", "quantity > 0 AND base_quantity > 0 AND taxable >= 0 AND cgst >= 0 AND sgst >= 0 AND igst >= 0 AND cess >= 0 AND stock_value >= 0 AND total = taxable + cgst + sgst + igst + cess");
                    table.ForeignKey(
                        name: "fk_purchase_return_lines_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_return_lines_grn_lines_grn_line_id",
                        column: x => x.grn_line_id,
                        principalTable: "grn_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_return_lines_product_variants_variant_id_business_",
                        columns: x => new { x.variant_id, x.business_id },
                        principalTable: "product_variants",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_return_lines_purchase_returns_purchase_return_id_b",
                        columns: x => new { x.purchase_return_id, x.business_id },
                        principalTable: "purchase_returns",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchase_return_lines_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_return_lines_business_id_tenant_id",
                table: "purchase_return_lines",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_return_lines_grn_line_id",
                table: "purchase_return_lines",
                column: "grn_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_return_lines_purchase_return_id_business_id",
                table: "purchase_return_lines",
                columns: new[] { "purchase_return_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_return_lines_purchase_return_id_grn_line_id",
                table: "purchase_return_lines",
                columns: new[] { "purchase_return_id", "grn_line_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchase_return_lines_purchase_return_id_line_number",
                table: "purchase_return_lines",
                columns: new[] { "purchase_return_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchase_return_lines_tenant_id",
                table: "purchase_return_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_return_lines_variant_id_business_id",
                table: "purchase_return_lines",
                columns: new[] { "variant_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_returns_business_id_idempotency_key",
                table: "purchase_returns",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchase_returns_business_id_tenant_id",
                table: "purchase_returns",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_returns_grn_id",
                table: "purchase_returns",
                column: "grn_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_returns_grn_id_business_id",
                table: "purchase_returns",
                columns: new[] { "grn_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_returns_store_id_business_id",
                table: "purchase_returns",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_returns_store_id_sequence_number",
                table: "purchase_returns",
                columns: new[] { "store_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchase_returns_supplier_id_business_date",
                table: "purchase_returns",
                columns: new[] { "supplier_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_returns_supplier_id_business_id",
                table: "purchase_returns",
                columns: new[] { "supplier_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_returns_tenant_id",
                table: "purchase_returns",
                column: "tenant_id");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(PurchaseReturnsSql.Tables));
            foreach (var table in PurchaseReturnsSql.Tables)
            {
                migrationBuilder.Sql(AppendOnlySql.Protect(table));
            }

            migrationBuilder.Sql(PurchaseReturnsSql.ReturnLineGuard);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PurchaseReturnsSql.DropReturnLineGuard);
            foreach (var table in PurchaseReturnsSql.Tables)
            {
                migrationBuilder.Sql(AppendOnlySql.Unprotect(table));
            }

            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(PurchaseReturnsSql.Tables));

            migrationBuilder.DropTable(
                name: "purchase_return_lines");

            migrationBuilder.DropTable(
                name: "purchase_returns");
        }
    }
}
