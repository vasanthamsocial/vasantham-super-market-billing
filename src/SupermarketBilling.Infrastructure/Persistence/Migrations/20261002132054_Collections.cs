using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Collections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "collection_visits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debtor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collector_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_date = table.Column<DateOnly>(type: "date", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    is_cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_visits", x => x.id);
                    table.ForeignKey(
                        name: "fk_collection_visits_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_visits_debtors_debtor_id_business_id",
                        columns: x => new { x.debtor_id, x.business_id },
                        principalTable: "debtors",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_visits_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_visits_users_collector_user_id",
                        column: x => x.collector_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "collector_absences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collector_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    absent_on = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collector_absences", x => x.id);
                    table.ForeignKey(
                        name: "fk_collector_absences_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collector_absences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collector_absences_users_collector_user_id",
                        column: x => x.collector_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_promises",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debtor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    promised_date = table.Column<DateOnly>(type: "date", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    is_cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_promises", x => x.id);
                    table.CheckConstraint("ck_payment_promises_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_payment_promises_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_promises_debtors_debtor_id_business_id",
                        columns: x => new { x.debtor_id, x.business_id },
                        principalTable: "debtors",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_promises_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "routes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_routes", x => x.id);
                    table.UniqueConstraint("ak_routes_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_routes_code", "code ~ '^[A-Z0-9-]{1,20}$'");
                    table.ForeignKey(
                        name: "fk_routes_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_routes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "collection_plans",
                columns: table => new
                {
                    debtor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route_id = table.Column<Guid>(type: "uuid", nullable: true),
                    visit_sequence = table.Column<int>(type: "integer", nullable: true),
                    primary_collector_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    backup_collector_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    preferred_from = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    preferred_to = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    schedule_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    weekday_mask = table.Column<int>(type: "integer", nullable: false),
                    anchor_date = table.Column<DateOnly>(type: "date", nullable: true),
                    month_day = table.Column<int>(type: "integer", nullable: true),
                    due_offset_days = table.Column<int>(type: "integer", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_plans", x => x.debtor_id);
                    table.CheckConstraint("ck_collection_plans_backup", "backup_collector_user_id IS NULL OR backup_collector_user_id <> primary_collector_user_id");
                    table.CheckConstraint("ck_collection_plans_schedule", "schedule_type IN ('MANUAL', 'WEEKDAYS', 'FORTNIGHTLY', 'MONTHLY', 'DUE_DATE', 'SPECIFIC_DATE')");
                    table.CheckConstraint("ck_collection_plans_values", "weekday_mask BETWEEN 0 AND 127 AND (month_day IS NULL OR month_day BETWEEN 1 AND 31) AND (due_offset_days IS NULL OR due_offset_days BETWEEN -60 AND 60) AND (visit_sequence IS NULL OR visit_sequence BETWEEN 1 AND 9999)");
                    table.ForeignKey(
                        name: "fk_collection_plans_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_plans_debtors_debtor_id_business_id",
                        columns: x => new { x.debtor_id, x.business_id },
                        principalTable: "debtors",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_plans_routes_route_id_business_id",
                        columns: x => new { x.route_id, x.business_id },
                        principalTable: "routes",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_plans_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_plans_users_backup_collector_user_id",
                        column: x => x.backup_collector_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_collection_plans_users_primary_collector_user_id",
                        column: x => x.primary_collector_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_collection_plans_backup_collector_user_id",
                table: "collection_plans",
                column: "backup_collector_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_collection_plans_business_id_tenant_id",
                table: "collection_plans",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_plans_debtor_id_business_id",
                table: "collection_plans",
                columns: new[] { "debtor_id", "business_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_collection_plans_primary_collector_user_id",
                table: "collection_plans",
                column: "primary_collector_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_collection_plans_route_id_business_id",
                table: "collection_plans",
                columns: new[] { "route_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_plans_route_id_visit_sequence",
                table: "collection_plans",
                columns: new[] { "route_id", "visit_sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_plans_tenant_id",
                table: "collection_plans",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_collection_visits_business_id_tenant_id",
                table: "collection_visits",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_visits_collector_user_id_visit_date",
                table: "collection_visits",
                columns: new[] { "collector_user_id", "visit_date" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_visits_debtor_id",
                table: "collection_visits",
                column: "debtor_id");

            migrationBuilder.CreateIndex(
                name: "ix_collection_visits_debtor_id_business_id",
                table: "collection_visits",
                columns: new[] { "debtor_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_visits_tenant_id",
                table: "collection_visits",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_collector_absences_business_id_collector_user_id_absent_on",
                table: "collector_absences",
                columns: new[] { "business_id", "collector_user_id", "absent_on" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_collector_absences_business_id_tenant_id",
                table: "collector_absences",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collector_absences_collector_user_id",
                table: "collector_absences",
                column: "collector_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_collector_absences_tenant_id",
                table: "collector_absences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_promises_business_id_tenant_id",
                table: "payment_promises",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_promises_debtor_id_business_id",
                table: "payment_promises",
                columns: new[] { "debtor_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_promises_debtor_id_promised_date",
                table: "payment_promises",
                columns: new[] { "debtor_id", "promised_date" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_promises_tenant_id",
                table: "payment_promises",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_routes_business_id_code",
                table: "routes",
                columns: new[] { "business_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_routes_business_id_tenant_id",
                table: "routes",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_routes_tenant_id",
                table: "routes",
                column: "tenant_id");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(CollectionsSql.Tables));
            foreach (var table in CollectionsSql.CancelOnly)
            {
                migrationBuilder.Sql(CollectionsSql.CancelOnlyGuard(table));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in CollectionsSql.CancelOnly)
            {
                migrationBuilder.Sql(CollectionsSql.DropCancelOnlyGuard(table));
            }

            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(CollectionsSql.Tables));

            migrationBuilder.DropTable(
                name: "collection_plans");

            migrationBuilder.DropTable(
                name: "collection_visits");

            migrationBuilder.DropTable(
                name: "collector_absences");

            migrationBuilder.DropTable(
                name: "payment_promises");

            migrationBuilder.DropTable(
                name: "routes");
        }
    }
}
