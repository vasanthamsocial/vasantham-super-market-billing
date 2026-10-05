using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Dispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "transporters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    gstin = table.Column<string>(type: "character(15)", fixedLength: true, maxLength: 15, nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transporters", x => x.id);
                    table.UniqueConstraint("ak_transporters_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_transporters_code", "code ~ '^[A-Z0-9-]{1,20}$'");
                    table.ForeignKey(
                        name: "fk_transporters_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transporters_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transporter_branches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    city = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    is_booking_office = table.Column<bool>(type: "boolean", nullable: false),
                    is_destination = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transporter_branches", x => x.id);
                    table.UniqueConstraint("ak_transporter_branches_id_transporter_id_business_id", x => new { x.id, x.transporter_id, x.business_id });
                    table.CheckConstraint("ck_transporter_branches_role", "is_booking_office OR is_destination");
                    table.ForeignKey(
                        name: "fk_transporter_branches_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transporter_branches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transporter_branches_transporters_transporter_id_business_id",
                        columns: x => new { x.transporter_id, x.business_id },
                        principalTable: "transporters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    party_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    delivery_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    transporter_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transporter_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    transporter_gstin = table.Column<string>(type: "character(15)", fixedLength: true, maxLength: 15, nullable: true),
                    booking_branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    booking_office = table.Column<string>(type: "character varying(170)", maxLength: 170, nullable: true),
                    destination_branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination_branch = table.Column<string>(type: "character varying(170)", maxLength: 170, nullable: true),
                    vehicle_number = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    driver_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    driver_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    lr_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    lr_date = table.Column<DateOnly>(type: "date", nullable: true),
                    package_count = table.Column<int>(type: "integer", nullable: false),
                    weight_kg = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    freight_terms = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    freight_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    dispatch_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expected_delivery_date = table.Column<DateOnly>(type: "date", nullable: true),
                    eway_bill_number = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    goods_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    cancel_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consignments", x => x.id);
                    table.UniqueConstraint("ak_consignments_id_business_id", x => new { x.id, x.business_id });
                    table.CheckConstraint("ck_consignments_cancel", "(status = 'CANCELLED') = (cancel_reason IS NOT NULL AND cancelled_by_user_id IS NOT NULL AND cancelled_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_consignments_lorry", "(mode = 'LORRY') = (transporter_id IS NOT NULL) AND (mode <> 'LORRY' OR (booking_branch_id IS NOT NULL AND destination_branch_id IS NOT NULL AND lr_number IS NOT NULL AND lr_date IS NOT NULL AND lr_date <= dispatch_date AND freight_terms IN ('PAID', 'TO_PAY'))) AND (mode = 'LORRY' OR (lr_number IS NULL AND freight_terms IS NULL AND freight_amount = 0))");
                    table.CheckConstraint("ck_consignments_mode", "mode IN ('OWN_VEHICLE', 'LORRY', 'LOCAL_DELIVERY') AND status IN ('DISPATCHED', 'CANCELLED')");
                    table.CheckConstraint("ck_consignments_trip", "(mode <> 'OWN_VEHICLE' OR vehicle_number IS NOT NULL) AND (mode <> 'LOCAL_DELIVERY' OR driver_name IS NOT NULL)");
                    table.CheckConstraint("ck_consignments_values", "package_count BETWEEN 1 AND 9999 AND (weight_kg IS NULL OR weight_kg > 0) AND freight_amount >= 0 AND goods_value >= 0 AND (expected_delivery_date IS NULL OR expected_delivery_date >= dispatch_date) AND (eway_bill_number IS NULL OR eway_bill_number ~ '^[0-9]{12}$')");
                    table.ForeignKey(
                        name: "fk_consignments_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignments_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignments_transporter_branches_booking_branch_id_transpo",
                        columns: x => new { x.booking_branch_id, x.transporter_id, x.business_id },
                        principalTable: "transporter_branches",
                        principalColumns: new[] { "id", "transporter_id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignments_transporter_branches_destination_branch_id_tra",
                        columns: x => new { x.destination_branch_id, x.transporter_id, x.business_id },
                        principalTable: "transporter_branches",
                        principalColumns: new[] { "id", "transporter_id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignments_transporters_transporter_id_business_id",
                        columns: x => new { x.transporter_id, x.business_id },
                        principalTable: "transporters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignments_users_cancelled_by_user_id",
                        column: x => x.cancelled_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignments_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_preferences",
                columns: table => new
                {
                    debtor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    transporter_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination_branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delivery_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_preferences", x => x.debtor_id);
                    table.CheckConstraint("ck_delivery_preferences_lorry", "(mode = 'LORRY') = (transporter_id IS NOT NULL) AND (destination_branch_id IS NULL OR transporter_id IS NOT NULL)");
                    table.CheckConstraint("ck_delivery_preferences_mode", "mode IN ('PICKUP', 'OWN_VEHICLE', 'LORRY', 'LOCAL_DELIVERY')");
                    table.ForeignKey(
                        name: "fk_delivery_preferences_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_delivery_preferences_debtors_debtor_id_business_id",
                        columns: x => new { x.debtor_id, x.business_id },
                        principalTable: "debtors",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_delivery_preferences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_delivery_preferences_transporter_branches_destination_branc",
                        columns: x => new { x.destination_branch_id, x.transporter_id, x.business_id },
                        principalTable: "transporter_branches",
                        principalColumns: new[] { "id", "transporter_id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_delivery_preferences_transporters_transporter_id_business_id",
                        columns: x => new { x.transporter_id, x.business_id },
                        principalTable: "transporters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_fulfilments",
                columns: table => new
                {
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    delivery_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    contact_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    transporter_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination_branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    chosen_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_fulfilments", x => x.invoice_id);
                    table.UniqueConstraint("ak_invoice_fulfilments_invoice_id_business_id", x => new { x.invoice_id, x.business_id });
                    table.CheckConstraint("ck_invoice_fulfilments_details", "(mode = 'PICKUP' OR delivery_address IS NOT NULL) AND ((mode = 'LORRY') = (transporter_id IS NOT NULL)) AND (destination_branch_id IS NULL OR transporter_id IS NOT NULL)");
                    table.CheckConstraint("ck_invoice_fulfilments_mode", "mode IN ('PICKUP', 'OWN_VEHICLE', 'LORRY', 'LOCAL_DELIVERY')");
                    table.ForeignKey(
                        name: "fk_invoice_fulfilments_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_fulfilments_sales_invoices_invoice_id_business_id",
                        columns: x => new { x.invoice_id, x.business_id },
                        principalTable: "sales_invoices",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_fulfilments_stores_store_id_business_id",
                        columns: x => new { x.store_id, x.business_id },
                        principalTable: "stores",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_fulfilments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_fulfilments_transporter_branches_destination_branch",
                        columns: x => new { x.destination_branch_id, x.transporter_id, x.business_id },
                        principalTable: "transporter_branches",
                        principalColumns: new[] { "id", "transporter_id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_fulfilments_transporters_transporter_id_business_id",
                        columns: x => new { x.transporter_id, x.business_id },
                        principalTable: "transporters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoice_fulfilments_users_chosen_by_user_id",
                        column: x => x.chosen_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transporter_routes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transit_days = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transporter_routes", x => x.id);
                    table.CheckConstraint("ck_transporter_routes_branches", "from_branch_id <> to_branch_id");
                    table.CheckConstraint("ck_transporter_routes_transit", "transit_days BETWEEN 0 AND 60");
                    table.ForeignKey(
                        name: "fk_transporter_routes_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transporter_routes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transporter_routes_transporter_branches_from_branch_id_tran",
                        columns: x => new { x.from_branch_id, x.transporter_id, x.business_id },
                        principalTable: "transporter_branches",
                        principalColumns: new[] { "id", "transporter_id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transporter_routes_transporter_branches_to_branch_id_transp",
                        columns: x => new { x.to_branch_id, x.transporter_id, x.business_id },
                        principalTable: "transporter_branches",
                        principalColumns: new[] { "id", "transporter_id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transporter_routes_transporters_transporter_id_business_id",
                        columns: x => new { x.transporter_id, x.business_id },
                        principalTable: "transporters",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consignment_invoices",
                columns: table => new
                {
                    consignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consignment_invoices", x => new { x.consignment_id, x.invoice_id });
                    table.ForeignKey(
                        name: "fk_consignment_invoices_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignment_invoices_consignments_consignment_id_business_id",
                        columns: x => new { x.consignment_id, x.business_id },
                        principalTable: "consignments",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignment_invoices_invoice_fulfilments_invoice_id_busines",
                        columns: x => new { x.invoice_id, x.business_id },
                        principalTable: "invoice_fulfilments",
                        principalColumns: new[] { "invoice_id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consignment_invoices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_consignment_invoices_business_id_tenant_id",
                table: "consignment_invoices",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consignment_invoices_consignment_id_business_id",
                table: "consignment_invoices",
                columns: new[] { "consignment_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consignment_invoices_invoice_id",
                table: "consignment_invoices",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_consignment_invoices_invoice_id_business_id",
                table: "consignment_invoices",
                columns: new[] { "invoice_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consignment_invoices_tenant_id",
                table: "consignment_invoices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_consignments_booking_branch_id_transporter_id_business_id",
                table: "consignments",
                columns: new[] { "booking_branch_id", "transporter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consignments_business_id_dispatch_date",
                table: "consignments",
                columns: new[] { "business_id", "dispatch_date" });

            migrationBuilder.CreateIndex(
                name: "ix_consignments_business_id_idempotency_key",
                table: "consignments",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_consignments_business_id_number",
                table: "consignments",
                columns: new[] { "business_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_consignments_business_id_tenant_id",
                table: "consignments",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consignments_cancelled_by_user_id",
                table: "consignments",
                column: "cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_consignments_created_by_user_id",
                table: "consignments",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_consignments_destination_branch_id_transporter_id_business_",
                table: "consignments",
                columns: new[] { "destination_branch_id", "transporter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consignments_store_id_business_id",
                table: "consignments",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consignments_tenant_id",
                table: "consignments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_consignments_transporter_id_business_id",
                table: "consignments",
                columns: new[] { "transporter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ux_consignments_lr",
                table: "consignments",
                columns: new[] { "business_id", "transporter_id", "lr_number" },
                unique: true,
                filter: "lr_number IS NOT NULL AND status = 'DISPATCHED'");

            migrationBuilder.CreateIndex(
                name: "ix_delivery_preferences_business_id_tenant_id",
                table: "delivery_preferences",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_preferences_debtor_id_business_id",
                table: "delivery_preferences",
                columns: new[] { "debtor_id", "business_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_delivery_preferences_destination_branch_id_transporter_id_b",
                table: "delivery_preferences",
                columns: new[] { "destination_branch_id", "transporter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_preferences_tenant_id",
                table: "delivery_preferences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_delivery_preferences_transporter_id_business_id",
                table: "delivery_preferences",
                columns: new[] { "transporter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_fulfilments_business_id_mode",
                table: "invoice_fulfilments",
                columns: new[] { "business_id", "mode" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_fulfilments_business_id_tenant_id",
                table: "invoice_fulfilments",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_fulfilments_chosen_by_user_id",
                table: "invoice_fulfilments",
                column: "chosen_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_fulfilments_destination_branch_id_transporter_id_bu",
                table: "invoice_fulfilments",
                columns: new[] { "destination_branch_id", "transporter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_fulfilments_store_id_business_id",
                table: "invoice_fulfilments",
                columns: new[] { "store_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_fulfilments_tenant_id",
                table: "invoice_fulfilments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_fulfilments_transporter_id_business_id",
                table: "invoice_fulfilments",
                columns: new[] { "transporter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transporter_branches_business_id_tenant_id",
                table: "transporter_branches",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transporter_branches_tenant_id",
                table: "transporter_branches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_transporter_branches_transporter_id_business_id",
                table: "transporter_branches",
                columns: new[] { "transporter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transporter_branches_transporter_id_name_city",
                table: "transporter_branches",
                columns: new[] { "transporter_id", "name", "city" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transporter_routes_business_id_tenant_id",
                table: "transporter_routes",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transporter_routes_from_branch_id_transporter_id_business_id",
                table: "transporter_routes",
                columns: new[] { "from_branch_id", "transporter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transporter_routes_tenant_id",
                table: "transporter_routes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_transporter_routes_to_branch_id_transporter_id_business_id",
                table: "transporter_routes",
                columns: new[] { "to_branch_id", "transporter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transporter_routes_transporter_id_business_id",
                table: "transporter_routes",
                columns: new[] { "transporter_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transporter_routes_transporter_id_from_branch_id_to_branch_",
                table: "transporter_routes",
                columns: new[] { "transporter_id", "from_branch_id", "to_branch_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transporters_business_id_code",
                table: "transporters",
                columns: new[] { "business_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transporters_business_id_tenant_id",
                table: "transporters",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_transporters_tenant_id",
                table: "transporters",
                column: "tenant_id");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(DispatchSql.Tables));
            migrationBuilder.Sql(AppendOnlySql.Protect("consignment_invoices"));
            migrationBuilder.Sql(DispatchSql.Guards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DispatchSql.DropGuards);
            migrationBuilder.Sql(AppendOnlySql.Unprotect("consignment_invoices"));
            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(DispatchSql.Tables));

            migrationBuilder.DropTable(
                name: "consignment_invoices");

            migrationBuilder.DropTable(
                name: "delivery_preferences");

            migrationBuilder.DropTable(
                name: "transporter_routes");

            migrationBuilder.DropTable(
                name: "consignments");

            migrationBuilder.DropTable(
                name: "invoice_fulfilments");

            migrationBuilder.DropTable(
                name: "transporter_branches");

            migrationBuilder.DropTable(
                name: "transporters");
        }
    }
}
