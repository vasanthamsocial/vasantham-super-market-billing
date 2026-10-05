using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "cost_layers",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "OTHER");

            migrationBuilder.AddCheckConstraint(
                name: "ck_cost_layers_origin",
                table: "cost_layers",
                sql: "origin IN ('GST', 'NON_GST', 'OTHER')");

            migrationBuilder.Sql(StockOriginSql.Backfill);
            migrationBuilder.Sql(StockOriginSql.Guard(withOrigin: true));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(StockOriginSql.Guard(withOrigin: false));

            migrationBuilder.DropCheckConstraint(
                name: "ck_cost_layers_origin",
                table: "cost_layers");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "cost_layers");
        }
    }
}
