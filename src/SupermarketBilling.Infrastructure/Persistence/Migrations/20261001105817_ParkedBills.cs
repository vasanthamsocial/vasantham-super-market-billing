using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ParkedBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "parked_bills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parked_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    cart_json = table.Column<string>(type: "jsonb", nullable: false),
                    parked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parked_bills", x => x.id);
                    table.CheckConstraint("ck_parked_bills_items", "item_count BETWEEN 1 AND 300");
                    table.ForeignKey(
                        name: "fk_parked_bills_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parked_bills_counters_counter_id_business_id",
                        columns: x => new { x.counter_id, x.business_id },
                        principalTable: "counters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parked_bills_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_parked_bills_business_id_tenant_id",
                table: "parked_bills",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_parked_bills_counter_id_business_id",
                table: "parked_bills",
                columns: new[] { "counter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_parked_bills_counter_id_parked_at_utc",
                table: "parked_bills",
                columns: new[] { "counter_id", "parked_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_parked_bills_tenant_id",
                table: "parked_bills",
                column: "tenant_id");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables("parked_bills"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TenancySql.UnprotectTenantTables("parked_bills"));

            migrationBuilder.DropTable(
                name: "parked_bills");
        }
    }
}
