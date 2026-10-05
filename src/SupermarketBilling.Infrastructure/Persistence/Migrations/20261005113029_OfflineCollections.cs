using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OfflineCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "collection_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collector_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    offline_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    max_offline_hours = table.Column<int>(type: "integer", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false),
                    enrolled_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enrolled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_synced_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_devices", x => x.id);
                    table.UniqueConstraint("ak_collection_devices_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_collection_devices_limits", "offline_limit >= 0 AND max_offline_hours BETWEEN 1 AND 168 AND last_sequence >= 0");
                    table.CheckConstraint("ck_collection_devices_revoked", "(revoked_at_utc IS NULL) = (revoked_by_user_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_collection_devices_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_devices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_devices_users_collector_user_id",
                        column: x => x.collector_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_devices_users_enrolled_by_user_id",
                        column: x => x.enrolled_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_devices_users_revoked_by_user_id",
                        column: x => x.revoked_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "offline_submissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    collector_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debtor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    bank_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    cheque_date = table.Column<DateOnly>(type: "date", nullable: true),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_offline_submissions", x => x.id);
                    table.CheckConstraint("ck_offline_submissions_outcome", "(status IN ('ACCEPTED', 'RESOLVED_ACCEPTED')) = (receipt_id IS NOT NULL) AND (status = 'ACCEPTED' OR reason IS NOT NULL) AND (status LIKE 'RESOLVED%') = (resolved_by_user_id IS NOT NULL AND resolved_at_utc IS NOT NULL AND resolution_note IS NOT NULL) AND (resolved_by_user_id IS NULL OR resolved_by_user_id <> collector_user_id)");
                    table.CheckConstraint("ck_offline_submissions_status", "status IN ('ACCEPTED', 'QUARANTINED', 'RESOLVED_ACCEPTED', 'RESOLVED_REJECTED')");
                    table.CheckConstraint("ck_offline_submissions_values", "amount > 0 AND sequence > 0");
                    table.ForeignKey(
                        name: "fk_offline_submissions_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_submissions_collection_devices_device_id_business_id",
                        columns: x => new { x.device_id, x.business_id },
                        principalTable: "collection_devices",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_submissions_debtor_receipts_receipt_id",
                        column: x => x.receipt_id,
                        principalTable: "debtor_receipts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_submissions_debtors_debtor_id_business_id",
                        columns: x => new { x.debtor_id, x.business_id },
                        principalTable: "debtors",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_submissions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_submissions_users_collector_user_id",
                        column: x => x.collector_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_offline_submissions_users_resolved_by_user_id",
                        column: x => x.resolved_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_collection_devices_business_id_tenant_id",
                table: "collection_devices",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_devices_collector_user_id",
                table: "collection_devices",
                column: "collector_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_collection_devices_enrolled_by_user_id",
                table: "collection_devices",
                column: "enrolled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_collection_devices_revoked_by_user_id",
                table: "collection_devices",
                column: "revoked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_collection_devices_tenant_id",
                table: "collection_devices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_collection_devices_token_hash",
                table: "collection_devices",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_offline_submissions_business_id_status",
                table: "offline_submissions",
                columns: new[] { "business_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_offline_submissions_business_id_tenant_id",
                table: "offline_submissions",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_offline_submissions_collector_user_id",
                table: "offline_submissions",
                column: "collector_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_offline_submissions_debtor_id_business_id",
                table: "offline_submissions",
                columns: new[] { "debtor_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_offline_submissions_device_id_business_id",
                table: "offline_submissions",
                columns: new[] { "device_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_offline_submissions_device_id_sequence",
                table: "offline_submissions",
                columns: new[] { "device_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_offline_submissions_receipt_id",
                table: "offline_submissions",
                column: "receipt_id",
                unique: true,
                filter: "receipt_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_offline_submissions_resolved_by_user_id",
                table: "offline_submissions",
                column: "resolved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_offline_submissions_tenant_id",
                table: "offline_submissions",
                column: "tenant_id");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(OfflineSql.Tables));
            migrationBuilder.Sql(OfflineSql.Guards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(OfflineSql.DropGuards);
            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(OfflineSql.Tables));

            migrationBuilder.DropTable(
                name: "offline_submissions");

            migrationBuilder.DropTable(
                name: "collection_devices");
        }
    }
}
