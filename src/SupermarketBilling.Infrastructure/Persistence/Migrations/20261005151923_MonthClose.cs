using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MonthClose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archive_recipients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_key_pem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    key_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    set_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archive_recipients", x => x.id);
                    table.ForeignKey(
                        name: "fk_archive_recipients_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_recipients_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_recipients_users_set_by_user_id",
                        column: x => x.set_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "month_locks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    locked_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    locked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    checks = table.Column<string>(type: "jsonb", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_month_locks", x => x.id);
                    table.UniqueConstraint("ak_month_locks_business_id_month", x => new { x.business_id, x.month });
                    table.CheckConstraint("ck_month_locks_first_day", "extract(day FROM month) = 1");
                    table.ForeignKey(
                        name: "fk_month_locks_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_locks_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_locks_users_locked_by_user_id",
                        column: x => x.locked_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "month_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    signer_key_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    recipient_key_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    manifest = table.Column<string>(type: "jsonb", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_month_packages", x => x.id);
                    table.CheckConstraint("ck_month_packages_values", "file_size > 0 AND char_length(file_sha256) = 64");
                    table.ForeignKey(
                        name: "fk_month_packages_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_packages_month_locks_business_id_month",
                        columns: x => new { x.business_id, x.month },
                        principalTable: "month_locks",
                        principalColumns: new[] { "business_id", "month" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_packages_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_month_packages_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_archive_recipients_business_id",
                table: "archive_recipients",
                column: "business_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_archive_recipients_business_id_tenant_id",
                table: "archive_recipients",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_archive_recipients_set_by_user_id",
                table: "archive_recipients",
                column: "set_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_recipients_tenant_id",
                table: "archive_recipients",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_month_locks_business_id_month",
                table: "month_locks",
                columns: new[] { "business_id", "month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_month_locks_business_id_tenant_id",
                table: "month_locks",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_month_locks_locked_by_user_id",
                table: "month_locks",
                column: "locked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_month_locks_tenant_id",
                table: "month_locks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_month_packages_business_id_month",
                table: "month_packages",
                columns: new[] { "business_id", "month" });

            migrationBuilder.CreateIndex(
                name: "ix_month_packages_business_id_tenant_id",
                table: "month_packages",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_month_packages_created_by_user_id",
                table: "month_packages",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_month_packages_tenant_id",
                table: "month_packages",
                column: "tenant_id");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(MonthCloseSql.Tables));
            foreach (var table in MonthCloseSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Protect(table));
            }

            migrationBuilder.Sql(MonthCloseSql.Guards());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(MonthCloseSql.DropGuards());
            foreach (var table in MonthCloseSql.AppendOnly)
            {
                migrationBuilder.Sql(AppendOnlySql.Unprotect(table));
            }

            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(MonthCloseSql.Tables));

            migrationBuilder.DropTable(
                name: "archive_recipients");

            migrationBuilder.DropTable(
                name: "month_packages");

            migrationBuilder.DropTable(
                name: "month_locks");
        }
    }
}
