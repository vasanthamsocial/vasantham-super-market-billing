using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OfflineBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_invoices_number",
                table: "sales_invoices");

            migrationBuilder.AlterColumn<string>(
                name: "number_prefix",
                table: "sales_invoices",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(7)",
                oldMaxLength: 7);

            migrationBuilder.AddColumn<decimal>(
                name: "offline_max_amount",
                table: "counter_devices",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "offline_max_bills",
                table: "counter_devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "offline_max_hours",
                table: "counter_devices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "offline_set_at_utc",
                table: "counter_devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "offline_set_by_user_id",
                table: "counter_devices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "offline_bills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number_prefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    cashier_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    review = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_offline_bills", x => x.id);
                    table.CheckConstraint("ck_offline_bills_invoice", "invoice_id IS NULL OR invoice_id = id");
                    table.CheckConstraint("ck_offline_bills_number", "number_prefix ~ '^[A-Z0-9]{1,7}/OF$' AND sequence > 0 AND number = number_prefix || '-' || CASE WHEN sequence < 1000000 THEN lpad(sequence::text, 6, '0') ELSE sequence::text END");
                    table.CheckConstraint("ck_offline_bills_outcome", "(status IN ('POSTED', 'RESOLVED_POSTED')) = (invoice_id IS NOT NULL) AND (status = 'POSTED' OR reason IS NOT NULL) AND (status LIKE 'RESOLVED%') = (resolved_by_user_id IS NOT NULL AND resolved_at_utc IS NOT NULL AND resolution_note IS NOT NULL) AND (resolved_by_user_id IS NULL OR resolved_by_user_id <> cashier_user_id) AND (reviewed_at_utc IS NULL OR (review IS NOT NULL AND reviewed_by_user_id IS NOT NULL AND resolution_note IS NOT NULL))");
                    table.CheckConstraint("ck_offline_bills_status", "status IN ('POSTED', 'QUARANTINED', 'RESOLVED_POSTED', 'RESOLVED_VOID')");
                    table.CheckConstraint("ck_offline_bills_total", "grand_total >= 0");
                    table.ForeignKey(
                        name: "fk_offline_bills_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_bills_counter_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "counter_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_bills_counters_counter_id_business_id",
                        columns: x => new { x.counter_id, x.business_id },
                        principalTable: "counters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_bills_sales_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "sales_invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_bills_shifts_shift_id",
                        column: x => x.shift_id,
                        principalTable: "shifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_bills_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_bills_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_bills_users_cashier_user_id",
                        column: x => x.cashier_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_bills_users_received_by_user_id",
                        column: x => x.received_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_bills_users_resolved_by_user_id",
                        column: x => x.resolved_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_bills_users_reviewed_by_user_id",
                        column: x => x.reviewed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_invoices_number",
                table: "sales_invoices",
                sql: "char_length(number) <= 16 AND number_prefix ~ '^[A-Z0-9]{1,7}(/OF)?$' AND sequence_number > 0 AND number = number_prefix || '-' || CASE WHEN sequence_number < 1000000 THEN lpad(sequence_number::text, 6, '0') ELSE sequence_number::text END");

            migrationBuilder.CreateIndex(
                name: "ix_counter_devices_offline_set_by_user_id",
                table: "counter_devices",
                column: "offline_set_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_counter_devices_one_offline",
                table: "counter_devices",
                column: "counter_id",
                unique: true,
                filter: "offline_max_bills IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_counter_devices_offline",
                table: "counter_devices",
                sql: "(offline_max_bills IS NULL AND offline_max_amount IS NULL AND offline_max_hours IS NULL) OR (offline_max_bills BETWEEN 1 AND 2000 AND offline_max_amount > 0 AND offline_max_amount <= 10000000 AND offline_max_hours BETWEEN 1 AND 72 AND revoked_at_utc IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_business_id_number",
                table: "offline_bills",
                columns: new[] { "business_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_business_id_status",
                table: "offline_bills",
                columns: new[] { "business_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_business_id_tenant_id",
                table: "offline_bills",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_cashier_user_id",
                table: "offline_bills",
                column: "cashier_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_counter_id_business_id",
                table: "offline_bills",
                columns: new[] { "counter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_counter_id_number_prefix_sequence",
                table: "offline_bills",
                columns: new[] { "counter_id", "number_prefix", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_device_id",
                table: "offline_bills",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_invoice_id",
                table: "offline_bills",
                column: "invoice_id",
                unique: true,
                filter: "invoice_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_received_by_user_id",
                table: "offline_bills",
                column: "received_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_resolved_by_user_id",
                table: "offline_bills",
                column: "resolved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_reviewed_by_user_id",
                table: "offline_bills",
                column: "reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_shift_id",
                table: "offline_bills",
                column: "shift_id");

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_store_id_business_id",
                table: "offline_bills",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_offline_bills_tenant_id",
                table: "offline_bills",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "fk_counter_devices_users_offline_set_by_user_id",
                table: "counter_devices",
                column: "offline_set_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(OfflineBillingSql.Tables));
            migrationBuilder.Sql(OfflineBillingSql.Guards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(OfflineBillingSql.DropGuards);
            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(OfflineBillingSql.Tables));

            migrationBuilder.DropForeignKey(
                name: "fk_counter_devices_users_offline_set_by_user_id",
                table: "counter_devices");

            migrationBuilder.DropTable(
                name: "offline_bills");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sales_invoices_number",
                table: "sales_invoices");

            migrationBuilder.DropIndex(
                name: "ix_counter_devices_offline_set_by_user_id",
                table: "counter_devices");

            migrationBuilder.DropIndex(
                name: "ux_counter_devices_one_offline",
                table: "counter_devices");

            migrationBuilder.DropCheckConstraint(
                name: "ck_counter_devices_offline",
                table: "counter_devices");

            migrationBuilder.DropColumn(
                name: "offline_max_amount",
                table: "counter_devices");

            migrationBuilder.DropColumn(
                name: "offline_max_bills",
                table: "counter_devices");

            migrationBuilder.DropColumn(
                name: "offline_max_hours",
                table: "counter_devices");

            migrationBuilder.DropColumn(
                name: "offline_set_at_utc",
                table: "counter_devices");

            migrationBuilder.DropColumn(
                name: "offline_set_by_user_id",
                table: "counter_devices");

            migrationBuilder.AlterColumn<string>(
                name: "number_prefix",
                table: "sales_invoices",
                type: "character varying(7)",
                maxLength: 7,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AddCheckConstraint(
                name: "ck_sales_invoices_number",
                table: "sales_invoices",
                sql: "char_length(number) <= 16 AND number_prefix ~ '^[A-Z0-9]{1,7}$' AND sequence_number > 0 AND number = number_prefix || '-' || CASE WHEN sequence_number < 1000000 THEN lpad(sequence_number::text, 6, '0') ELSE sequence_number::text END");
        }
    }
}
