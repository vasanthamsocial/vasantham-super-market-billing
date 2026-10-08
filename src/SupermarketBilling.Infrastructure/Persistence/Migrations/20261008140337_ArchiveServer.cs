using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archive_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: true),
                    store_id = table.Column<Guid>(type: "uuid", nullable: true),
                    financial_year = table.Column<int>(type: "integer", nullable: true),
                    reports = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    granted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archive_grants", x => x.id);
                    table.CheckConstraint("ck_archive_grants_revoked", "(revoked_at_utc IS NULL) = (revoked_by_user_id IS NULL)");
                    table.CheckConstraint("ck_archive_grants_role", "role_code IN ('archive_owner', 'archive_manager', 'archive_accountant', 'archive_auditor', 'archive_report_user', 'archive_support')");
                    table.CheckConstraint("ck_archive_grants_scope", "store_id IS NULL OR business_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_archive_grants_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_grants_users_granted_by_user_id",
                        column: x => x.granted_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_grants_users_revoked_by_user_id",
                        column: x => x.revoked_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_grants_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "archive_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    key_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    public_key_pem = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    registered_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archive_sources", x => x.id);
                    table.ForeignKey(
                        name: "fk_archive_sources_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_sources_users_registered_by_user_id",
                        column: x => x.registered_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_sources_users_revoked_by_user_id",
                        column: x => x.revoked_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "archive_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    business_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    manifest = table.Column<string>(type: "jsonb", nullable: false),
                    verification = table.Column<string>(type: "jsonb", nullable: false),
                    imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    imported_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    accountant_approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    accountant_approved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accountant_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    owner_approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_approved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    owner_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archive_imports", x => x.id);
                    table.CheckConstraint("ck_archive_imports_approvals", "(status = 'VERIFIED') = (accountant_approved_by_user_id IS NULL) AND (status = 'APPROVED') = (owner_approved_by_user_id IS NOT NULL) AND (owner_approved_by_user_id IS NULL OR owner_approved_by_user_id <> accountant_approved_by_user_id)");
                    table.CheckConstraint("ck_archive_imports_month", "extract(day FROM month) = 1");
                    table.CheckConstraint("ck_archive_imports_status", "status IN ('VERIFIED', 'ACCOUNTANT_APPROVED', 'APPROVED')");
                    table.ForeignKey(
                        name: "fk_archive_imports_archive_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "archive_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_imports_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_imports_users_accountant_approved_by_user_id",
                        column: x => x.accountant_approved_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_imports_users_imported_by_user_id",
                        column: x => x.imported_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_imports_users_owner_approved_by_user_id",
                        column: x => x.owner_approved_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "archive_masters",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    record_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archive_masters", x => new { x.business_id, x.dataset, x.record_id });
                    table.ForeignKey(
                        name: "fk_archive_masters_archive_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "archive_imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_masters_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "archive_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    dataset = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    record_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archive_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_archive_records_archive_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "archive_imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_archive_records_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_archive_grants_granted_by_user_id",
                table: "archive_grants",
                column: "granted_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_grants_revoked_by_user_id",
                table: "archive_grants",
                column: "revoked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_grants_tenant_id",
                table: "archive_grants",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_grants_user_id",
                table: "archive_grants",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_imports_accountant_approved_by_user_id",
                table: "archive_imports",
                column: "accountant_approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_imports_business_id_month",
                table: "archive_imports",
                columns: new[] { "business_id", "month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_archive_imports_imported_by_user_id",
                table: "archive_imports",
                column: "imported_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_imports_owner_approved_by_user_id",
                table: "archive_imports",
                column: "owner_approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_imports_source_id",
                table: "archive_imports",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_imports_tenant_id",
                table: "archive_imports",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_masters_import_id",
                table: "archive_masters",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_masters_tenant_id",
                table: "archive_masters",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_records_business_id_dataset_month",
                table: "archive_records",
                columns: new[] { "business_id", "dataset", "month" });

            migrationBuilder.CreateIndex(
                name: "ix_archive_records_business_id_dataset_record_id",
                table: "archive_records",
                columns: new[] { "business_id", "dataset", "record_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_archive_records_import_id",
                table: "archive_records",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_records_tenant_id",
                table: "archive_records",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_sources_key_id",
                table: "archive_sources",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_archive_sources_registered_by_user_id",
                table: "archive_sources",
                column: "registered_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_sources_revoked_by_user_id",
                table: "archive_sources",
                column: "revoked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_archive_sources_tenant_id",
                table: "archive_sources",
                column: "tenant_id");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(ArchiveServerSql.Tables));
            migrationBuilder.Sql(AppendOnlySql.Protect("archive_records"));
            migrationBuilder.Sql(ArchiveServerSql.Guards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ArchiveServerSql.DropGuards);
            migrationBuilder.Sql(AppendOnlySql.Unprotect("archive_records"));
            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(ArchiveServerSql.Tables));

            migrationBuilder.DropTable(
                name: "archive_grants");

            migrationBuilder.DropTable(
                name: "archive_masters");

            migrationBuilder.DropTable(
                name: "archive_records");

            migrationBuilder.DropTable(
                name: "archive_imports");

            migrationBuilder.DropTable(
                name: "archive_sources");
        }
    }
}
