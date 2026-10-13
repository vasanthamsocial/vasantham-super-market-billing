using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Retention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "receipt_key_id",
                table: "archive_recipients",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_key_pem",
                table: "archive_recipients",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "receipt_key_set_at_utc",
                table: "archive_recipients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "receipt_key_set_by_user_id",
                table: "archive_recipients",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "month_removal_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    receipt = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    archive_approved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    registered_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_month_removal_receipts", x => x.id);
                    table.CheckConstraint("ck_month_removal_receipts_values", "extract(day FROM month) = 1 AND char_length(file_sha256) = 64");
                    table.ForeignKey(
                        name: "fk_month_removal_receipts_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_removal_receipts_month_packages_package_id",
                        column: x => x.package_id,
                        principalTable: "month_packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_removal_receipts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_removal_receipts_users_registered_by_user_id",
                        column: x => x.registered_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_month_removal_receipts_business_id_month",
                table: "month_removal_receipts",
                columns: new[] { "business_id", "month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_month_removal_receipts_business_id_tenant_id",
                table: "month_removal_receipts",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_month_removal_receipts_package_id",
                table: "month_removal_receipts",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "ix_month_removal_receipts_registered_by_user_id",
                table: "month_removal_receipts",
                column: "registered_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_month_removal_receipts_tenant_id",
                table: "month_removal_receipts",
                column: "tenant_id");

            // Tenant isolation, and receipts are kept as received (D-044).
            migrationBuilder.Sql(TenancySql.ProtectTenantTables("month_removal_receipts"));
            migrationBuilder.Sql(AppendOnlySql.Protect("month_removal_receipts"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AppendOnlySql.Unprotect("month_removal_receipts"));
            migrationBuilder.Sql(TenancySql.UnprotectTenantTables("month_removal_receipts"));

            migrationBuilder.DropTable(
                name: "month_removal_receipts");

            migrationBuilder.DropColumn(
                name: "receipt_key_id",
                table: "archive_recipients");

            migrationBuilder.DropColumn(
                name: "receipt_key_pem",
                table: "archive_recipients");

            migrationBuilder.DropColumn(
                name: "receipt_key_set_at_utc",
                table: "archive_recipients");

            migrationBuilder.DropColumn(
                name: "receipt_key_set_by_user_id",
                table: "archive_recipients");
        }
    }
}
