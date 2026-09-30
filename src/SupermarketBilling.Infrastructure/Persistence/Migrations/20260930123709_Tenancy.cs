using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Tenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_approval_requests_businesses_business_id",
                table: "approval_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_role_assignments_businesses_business_id",
                table: "role_assignments");

            migrationBuilder.DropForeignKey(
                name: "fk_role_assignments_users_user_id",
                table: "role_assignments");

            migrationBuilder.DropForeignKey(
                name: "fk_sessions_users_user_id",
                table: "sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_stores_businesses_business_id",
                table: "stores");

            migrationBuilder.DropIndex(
                name: "ix_users_username",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_businesses_code",
                table: "businesses");

            migrationBuilder.DropIndex(
                name: "ix_businesses_gstin",
                table: "businesses");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "stores",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "sessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "role_assignments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "password_reset_tokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "mfa_recovery_codes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "businesses",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "audit_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "approval_requests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddUniqueConstraint(
                name: "ak_users_id_tenant_id",
                table: "users",
                columns: new[] { "id", "tenant_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_businesses_id_tenant_id",
                table: "businesses",
                columns: new[] { "id", "tenant_id" });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                    table.CheckConstraint("ck_tenants_code", "code ~ '^[A-Z0-9]{3,20}$'");
                });

            migrationBuilder.CreateTable(
                name: "installation",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_installation", x => x.id);
                    table.CheckConstraint("ck_installation_singleton", "id = 1");
                    table.ForeignKey(
                        name: "fk_installation_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Existing single-tenant data becomes one tenant before keys and foreign keys are enforced.
            migrationBuilder.Sql(TenancySql.SeedInstallationAndBackfill());

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id",
                table: "users",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_users_tenant_username",
                table: "users",
                columns: new[] { "tenant_id", "username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stores_business_id_tenant_id",
                table: "stores",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stores_tenant_id",
                table: "stores",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_tenant_id",
                table: "sessions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_user_id_tenant_id",
                table: "sessions",
                columns: new[] { "user_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_business_id_tenant_id",
                table: "role_assignments",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_tenant_id",
                table: "role_assignments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_user_id_tenant_id",
                table: "role_assignments",
                columns: new[] { "user_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_tokens_tenant_id",
                table: "password_reset_tokens",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_mfa_recovery_codes_tenant_id",
                table: "mfa_recovery_codes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_businesses_tenant_id",
                table: "businesses",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_businesses_tenant_code",
                table: "businesses",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_businesses_tenant_gstin",
                table: "businesses",
                columns: new[] { "tenant_id", "gstin" },
                unique: true,
                filter: "gstin IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_id_sequence",
                table: "audit_events",
                columns: new[] { "tenant_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_business_id_tenant_id",
                table: "approval_requests",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_tenant_id",
                table: "approval_requests",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_installation_tenant_id",
                table: "installation",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_code",
                table: "tenants",
                column: "code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_approval_requests_businesses_business_id_tenant_id",
                table: "approval_requests",
                columns: new[] { "business_id", "tenant_id" },
                principalTable: "businesses",
                principalColumns: new[] { "id", "tenant_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_approval_requests_tenants_tenant_id",
                table: "approval_requests",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_audit_events_tenants_tenant_id",
                table: "audit_events",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_businesses_tenants_tenant_id",
                table: "businesses",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_mfa_recovery_codes_tenants_tenant_id",
                table: "mfa_recovery_codes",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_password_reset_tokens_tenants_tenant_id",
                table: "password_reset_tokens",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_role_assignments_businesses_business_id_tenant_id",
                table: "role_assignments",
                columns: new[] { "business_id", "tenant_id" },
                principalTable: "businesses",
                principalColumns: new[] { "id", "tenant_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_role_assignments_tenants_tenant_id",
                table: "role_assignments",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_role_assignments_users_user_id_tenant_id",
                table: "role_assignments",
                columns: new[] { "user_id", "tenant_id" },
                principalTable: "users",
                principalColumns: new[] { "id", "tenant_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_sessions_tenants_tenant_id",
                table: "sessions",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_sessions_users_user_id_tenant_id",
                table: "sessions",
                columns: new[] { "user_id", "tenant_id" },
                principalTable: "users",
                principalColumns: new[] { "id", "tenant_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stores_businesses_business_id_tenant_id",
                table: "stores",
                columns: new[] { "business_id", "tenant_id" },
                principalTable: "businesses",
                principalColumns: new[] { "id", "tenant_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stores_tenants_tenant_id",
                table: "stores",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_users_tenants_tenant_id",
                table: "users",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(TenancySql.EnableRowLevelSecurity());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TenancySql.DisableRowLevelSecurity());

            migrationBuilder.DropForeignKey(
                name: "fk_approval_requests_businesses_business_id_tenant_id",
                table: "approval_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_approval_requests_tenants_tenant_id",
                table: "approval_requests");

            migrationBuilder.DropForeignKey(
                name: "fk_audit_events_tenants_tenant_id",
                table: "audit_events");

            migrationBuilder.DropForeignKey(
                name: "fk_businesses_tenants_tenant_id",
                table: "businesses");

            migrationBuilder.DropForeignKey(
                name: "fk_mfa_recovery_codes_tenants_tenant_id",
                table: "mfa_recovery_codes");

            migrationBuilder.DropForeignKey(
                name: "fk_password_reset_tokens_tenants_tenant_id",
                table: "password_reset_tokens");

            migrationBuilder.DropForeignKey(
                name: "fk_role_assignments_businesses_business_id_tenant_id",
                table: "role_assignments");

            migrationBuilder.DropForeignKey(
                name: "fk_role_assignments_tenants_tenant_id",
                table: "role_assignments");

            migrationBuilder.DropForeignKey(
                name: "fk_role_assignments_users_user_id_tenant_id",
                table: "role_assignments");

            migrationBuilder.DropForeignKey(
                name: "fk_sessions_tenants_tenant_id",
                table: "sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_sessions_users_user_id_tenant_id",
                table: "sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_stores_businesses_business_id_tenant_id",
                table: "stores");

            migrationBuilder.DropForeignKey(
                name: "fk_stores_tenants_tenant_id",
                table: "stores");

            migrationBuilder.DropForeignKey(
                name: "fk_users_tenants_tenant_id",
                table: "users");

            migrationBuilder.DropTable(
                name: "installation");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_users_id_tenant_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_tenant_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ux_users_tenant_username",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_stores_business_id_tenant_id",
                table: "stores");

            migrationBuilder.DropIndex(
                name: "ix_stores_tenant_id",
                table: "stores");

            migrationBuilder.DropIndex(
                name: "ix_sessions_tenant_id",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "ix_sessions_user_id_tenant_id",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "ix_role_assignments_business_id_tenant_id",
                table: "role_assignments");

            migrationBuilder.DropIndex(
                name: "ix_role_assignments_tenant_id",
                table: "role_assignments");

            migrationBuilder.DropIndex(
                name: "ix_role_assignments_user_id_tenant_id",
                table: "role_assignments");

            migrationBuilder.DropIndex(
                name: "ix_password_reset_tokens_tenant_id",
                table: "password_reset_tokens");

            migrationBuilder.DropIndex(
                name: "ix_mfa_recovery_codes_tenant_id",
                table: "mfa_recovery_codes");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_businesses_id_tenant_id",
                table: "businesses");

            migrationBuilder.DropIndex(
                name: "ix_businesses_tenant_id",
                table: "businesses");

            migrationBuilder.DropIndex(
                name: "ux_businesses_tenant_code",
                table: "businesses");

            migrationBuilder.DropIndex(
                name: "ux_businesses_tenant_gstin",
                table: "businesses");

            migrationBuilder.DropIndex(
                name: "ix_audit_events_tenant_id_sequence",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "ix_approval_requests_business_id_tenant_id",
                table: "approval_requests");

            migrationBuilder.DropIndex(
                name: "ix_approval_requests_tenant_id",
                table: "approval_requests");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "role_assignments");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "password_reset_tokens");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "mfa_recovery_codes");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "approval_requests");

            migrationBuilder.CreateIndex(
                name: "ix_users_username",
                table: "users",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_businesses_code",
                table: "businesses",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_businesses_gstin",
                table: "businesses",
                column: "gstin",
                unique: true,
                filter: "gstin IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_approval_requests_businesses_business_id",
                table: "approval_requests",
                column: "business_id",
                principalTable: "businesses",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_role_assignments_businesses_business_id",
                table: "role_assignments",
                column: "business_id",
                principalTable: "businesses",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_role_assignments_users_user_id",
                table: "role_assignments",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_sessions_users_user_id",
                table: "sessions",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stores_businesses_business_id",
                table: "stores",
                column: "business_id",
                principalTable: "businesses",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
