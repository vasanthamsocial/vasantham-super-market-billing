using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetentionRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_ledger_direction",
                table: "stock_ledger");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_ledger_value",
                table: "stock_ledger");

            migrationBuilder.CreateTable(
                name: "month_removals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    removed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    removed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    counts = table.Column<string>(type: "jsonb", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_month_removals", x => x.id);
                    table.CheckConstraint("ck_month_removals_first_day", "extract(day FROM month) = 1");
                    table.ForeignKey(
                        name: "fk_month_removals_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_removals_month_removal_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalTable: "month_removal_receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_removals_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_removals_users_removed_by_user_id",
                        column: x => x.removed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "month_removed_numbers",
                columns: table => new
                {
                    removal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    series = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_month_removed_numbers", x => new { x.removal_id, x.store_id, x.series });
                    table.CheckConstraint("ck_month_removed_numbers_values", "count > 0 AND last_number > 0");
                    table.ForeignKey(
                        name: "fk_month_removed_numbers_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_removed_numbers_month_removals_removal_id",
                        column: x => x.removal_id,
                        principalTable: "month_removals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_removed_numbers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_ledger_direction",
                table: "stock_ledger",
                sql: "(movement_type IN ('OPENING', 'ADJUSTMENT_IN', 'TRANSFER_IN', 'COUNT_GAIN', 'RECEIPT', 'SALE_RETURN') AND quantity > 0) OR (movement_type IN ('ADJUSTMENT_OUT', 'DAMAGE', 'WASTAGE', 'TRANSFER_OUT', 'COUNT_LOSS', 'PURCHASE_RETURN', 'SALE') AND quantity < 0) OR (movement_type = 'CARRIED_FORWARD' AND document_type = 'RETENTION')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_ledger_value",
                table: "stock_ledger",
                sql: "value = round(quantity * unit_cost, 4) OR (movement_type = 'CARRIED_FORWARD' AND unit_cost = 0)");

            migrationBuilder.CreateIndex(
                name: "ix_month_removals_business_id_month",
                table: "month_removals",
                columns: new[] { "business_id", "month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_month_removals_business_id_tenant_id",
                table: "month_removals",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_month_removals_receipt_id",
                table: "month_removals",
                column: "receipt_id");

            migrationBuilder.CreateIndex(
                name: "ix_month_removals_removed_by_user_id",
                table: "month_removals",
                column: "removed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_month_removals_tenant_id",
                table: "month_removals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_month_removed_numbers_business_id_tenant_id",
                table: "month_removed_numbers",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_month_removed_numbers_store_id_series",
                table: "month_removed_numbers",
                columns: new[] { "store_id", "series" });

            migrationBuilder.CreateIndex(
                name: "ix_month_removed_numbers_tenant_id",
                table: "month_removed_numbers",
                column: "tenant_id");

            // Tenant isolation; removals are kept for good; the database-side gates and the removal itself (D-044).
            migrationBuilder.Sql(TenancySql.ProtectTenantTables(RetentionSql.Tables));
            foreach (var table in RetentionSql.Tables)
            {
                migrationBuilder.Sql(AppendOnlySql.Protect(table));
            }

            migrationBuilder.Sql(RetentionSql.Blockers);
            migrationBuilder.Sql(RetentionSql.Remove());
            migrationBuilder.Sql(RetentionSql.RemovedNumbersView);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RetentionSql.Drop);
            foreach (var table in RetentionSql.Tables)
            {
                migrationBuilder.Sql(AppendOnlySql.Unprotect(table));
            }

            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(RetentionSql.Tables));

            migrationBuilder.DropTable(
                name: "month_removed_numbers");

            migrationBuilder.DropTable(
                name: "month_removals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_ledger_direction",
                table: "stock_ledger");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_ledger_value",
                table: "stock_ledger");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_ledger_direction",
                table: "stock_ledger",
                sql: "(movement_type IN ('OPENING', 'ADJUSTMENT_IN', 'TRANSFER_IN', 'COUNT_GAIN', 'RECEIPT', 'SALE_RETURN') AND quantity > 0) OR (movement_type IN ('ADJUSTMENT_OUT', 'DAMAGE', 'WASTAGE', 'TRANSFER_OUT', 'COUNT_LOSS', 'PURCHASE_RETURN', 'SALE') AND quantity < 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_ledger_value",
                table: "stock_ledger",
                sql: "value = round(quantity * unit_cost, 4)");
        }
    }
}
