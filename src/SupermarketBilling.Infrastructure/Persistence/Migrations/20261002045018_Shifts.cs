using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Shifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals");

            migrationBuilder.AddColumn<Guid>(
                name: "shift_id",
                table: "sales_returns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "shift_id",
                table: "sales_invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "shifts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    opened_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    opening_float = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    closed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expected_cash = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    counted_cash = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    difference = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    close_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shifts", x => x.id);
                    table.CheckConstraint("ck_shifts_close", "(status = 'OPEN' AND closed_at_utc IS NULL AND expected_cash IS NULL AND counted_cash IS NULL AND difference IS NULL) OR (status = 'CLOSED' AND closed_at_utc IS NOT NULL AND expected_cash IS NOT NULL AND counted_cash >= 0 AND difference = counted_cash - expected_cash)");
                    table.CheckConstraint("ck_shifts_float", "opening_float >= 0");
                    table.CheckConstraint("ck_shifts_note", "difference IS NULL OR difference = 0 OR close_note IS NOT NULL");
                    table.CheckConstraint("ck_shifts_review", "(reviewed_by_user_id IS NULL) = (reviewed_at_utc IS NULL) AND (reviewed_by_user_id IS NULL OR (reviewed_by_user_id <> cashier_user_id AND reviewed_by_user_id <> closed_by_user_id))");
                    table.CheckConstraint("ck_shifts_status", "status IN ('OPEN', 'CLOSED')");
                    table.ForeignKey(
                        name: "fk_shifts_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shifts_counters_counter_id_business_id",
                        columns: x => new { x.counter_id, x.business_id },
                        principalTable: "counters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shifts_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shifts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_movements", x => x.id);
                    table.CheckConstraint("ck_cash_movements_amount", "amount > 0");
                    table.CheckConstraint("ck_cash_movements_kind", "kind IN ('PAY_IN', 'PAY_OUT', 'DROP')");
                    table.ForeignKey(
                        name: "fk_cash_movements_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cash_movements_shifts_shift_id",
                        column: x => x.shift_id,
                        principalTable: "shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cash_movements_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shift_counts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    denomination = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shift_counts", x => x.id);
                    table.CheckConstraint("ck_shift_counts_kind", "kind IN ('OPENING', 'CLOSING')");
                    table.CheckConstraint("ck_shift_counts_values", "denomination IN (2000, 500, 200, 100, 50, 20, 10, 5, 2, 1) AND count > 0");
                    table.ForeignKey(
                        name: "fk_shift_counts_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shift_counts_shifts_shift_id",
                        column: x => x.shift_id,
                        principalTable: "shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shift_counts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals",
                sql: "(kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_amount IS NULL) OR (kind IN ('DISCOUNT', 'RETURN', 'PAY_OUT') AND variant_unit_id IS NULL AND approved_price IS NULL AND max_amount > 0)");

            migrationBuilder.CreateIndex(
                name: "ix_sales_returns_shift_id",
                table: "sales_returns",
                column: "shift_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_shift_id",
                table: "sales_invoices",
                column: "shift_id");

            migrationBuilder.CreateIndex(
                name: "ix_cash_movements_business_id_tenant_id",
                table: "cash_movements",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_movements_shift_id",
                table: "cash_movements",
                column: "shift_id");

            migrationBuilder.CreateIndex(
                name: "ix_cash_movements_tenant_id",
                table: "cash_movements",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_shift_counts_business_id_tenant_id",
                table: "shift_counts",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shift_counts_shift_id_kind_denomination",
                table: "shift_counts",
                columns: new[] { "shift_id", "kind", "denomination" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shift_counts_tenant_id",
                table: "shift_counts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_shifts_business_id_tenant_id",
                table: "shifts",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shifts_counter_id_business_id",
                table: "shifts",
                columns: new[] { "counter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shifts_store_id_business_date",
                table: "shifts",
                columns: new[] { "store_id", "business_date" });

            migrationBuilder.CreateIndex(
                name: "ix_shifts_store_id_business_id",
                table: "shifts",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shifts_tenant_id",
                table: "shifts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_shifts_open_per_cashier",
                table: "shifts",
                column: "cashier_user_id",
                unique: true,
                filter: "status = 'OPEN'");

            migrationBuilder.CreateIndex(
                name: "ux_shifts_open_per_counter",
                table: "shifts",
                column: "counter_id",
                unique: true,
                filter: "status = 'OPEN'");

            migrationBuilder.AddForeignKey(
                name: "fk_sales_invoices_shifts_shift_id",
                table: "sales_invoices",
                column: "shift_id",
                principalTable: "shifts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_sales_returns_shifts_shift_id",
                table: "sales_returns",
                column: "shift_id",
                principalTable: "shifts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(ShiftsSql.Tables));
            foreach (var table in ShiftsSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Protect(table));
            }

            migrationBuilder.Sql(ShiftsSql.Guards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ShiftsSql.DropGuards);
            foreach (var table in ShiftsSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Unprotect(table));
            }

            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(ShiftsSql.Tables));

            migrationBuilder.DropForeignKey(
                name: "fk_sales_invoices_shifts_shift_id",
                table: "sales_invoices");

            migrationBuilder.DropForeignKey(
                name: "fk_sales_returns_shifts_shift_id",
                table: "sales_returns");

            migrationBuilder.DropTable(
                name: "cash_movements");

            migrationBuilder.DropTable(
                name: "shift_counts");

            migrationBuilder.DropTable(
                name: "shifts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals");

            migrationBuilder.DropIndex(
                name: "ix_sales_returns_shift_id",
                table: "sales_returns");

            migrationBuilder.DropIndex(
                name: "ix_sales_invoices_shift_id",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "shift_id",
                table: "sales_returns");

            migrationBuilder.DropColumn(
                name: "shift_id",
                table: "sales_invoices");

            migrationBuilder.AddCheckConstraint(
                name: "ck_supervisor_approvals_kind",
                table: "supervisor_approvals",
                sql: "(kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_amount IS NULL) OR (kind IN ('DISCOUNT', 'RETURN') AND variant_unit_id IS NULL AND approved_price IS NULL AND max_amount > 0)");
        }
    }
}
