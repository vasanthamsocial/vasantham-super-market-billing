using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupermarketBilling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Messaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    channel = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    provider_template_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    dlt_template_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    language_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_templates", x => x.id);
                    table.CheckConstraint("ck_message_templates_kind", "kind IN ('CREDIT_INVOICE', 'RECEIPT') AND channel IN ('WHATSAPP', 'SMS')");
                    table.ForeignKey(
                        name: "fk_message_templates_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_message_templates_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "messaging_settings",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    whatsapp_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    sms_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    send_invoices = table.Column<bool>(type: "boolean", nullable: false),
                    send_receipts = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messaging_settings", x => x.business_id);
                    table.ForeignKey(
                        name: "fk_messaging_settings_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_messaging_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbound_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debtor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    to_number = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    skip_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    provider_message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attachment_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    read_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_messages", x => x.id);
                    table.CheckConstraint("ck_outbound_messages_kind", "kind IN ('CREDIT_INVOICE', 'RECEIPT') AND channel IN ('WHATSAPP', 'SMS')");
                    table.CheckConstraint("ck_outbound_messages_status", "status IN ('QUEUED', 'SENT', 'DELIVERED', 'READ', 'FAILED', 'SKIPPED')");
                    table.CheckConstraint("ck_outbound_messages_steps", "(status = 'SKIPPED') = (skip_reason IS NOT NULL) AND (status <> 'QUEUED' OR next_attempt_at_utc IS NOT NULL) AND (status NOT IN ('SENT', 'DELIVERED', 'READ') OR (provider_message_id IS NOT NULL AND sent_at_utc IS NOT NULL)) AND attempts BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "fk_outbound_messages_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outbound_messages_debtors_debtor_id_business_id",
                        columns: x => new { x.debtor_id, x.business_id },
                        principalTable: "debtors",
                        principalColumns: new[] { "id", "business_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outbound_messages_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_message_refs",
                columns: table => new
                {
                    provider_message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_message_refs", x => x.provider_message_id);
                });

            migrationBuilder.CreateTable(
                name: "message_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_message_events_businesses_business_id_tenant_id",
                        columns: x => new { x.business_id, x.tenant_id },
                        principalTable: "businesses",
                        principalColumns: new[] { "id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_message_events_outbound_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "outbound_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_message_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_message_events_business_id_tenant_id",
                table: "message_events",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_message_events_message_id",
                table: "message_events",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_message_events_tenant_id",
                table: "message_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_message_templates_business_id_kind_channel",
                table: "message_templates",
                columns: new[] { "business_id", "kind", "channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_message_templates_business_id_tenant_id",
                table: "message_templates",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_message_templates_tenant_id",
                table: "message_templates",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_messaging_settings_business_id_tenant_id",
                table: "messaging_settings",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_messaging_settings_tenant_id",
                table: "messaging_settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_business_id_kind_channel_document_id",
                table: "outbound_messages",
                columns: new[] { "business_id", "kind", "channel", "document_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_business_id_tenant_id",
                table: "outbound_messages",
                columns: new[] { "business_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_debtor_id",
                table: "outbound_messages",
                column: "debtor_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_debtor_id_business_id",
                table: "outbound_messages",
                columns: new[] { "debtor_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_provider_message_id",
                table: "outbound_messages",
                column: "provider_message_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_status_next_attempt_at_utc",
                table: "outbound_messages",
                columns: new[] { "status", "next_attempt_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_messages_tenant_id",
                table: "outbound_messages",
                column: "tenant_id");

            migrationBuilder.Sql(TenancySql.ProtectTenantTables(MessagingSql.Tables));
            migrationBuilder.Sql(AppendOnlySql.Protect("message_events"));
            migrationBuilder.Sql(MessagingSql.MessageGuard);
            migrationBuilder.Sql(MessagingSql.ActiveTenantsFunction);
            migrationBuilder.Sql(MessagingSql.MessageRefFunction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(MessagingSql.DropMessageRefFunction);
            migrationBuilder.Sql(MessagingSql.DropActiveTenantsFunction);
            migrationBuilder.Sql(MessagingSql.DropMessageGuard);
            migrationBuilder.Sql(AppendOnlySql.Unprotect("message_events"));
            migrationBuilder.Sql(TenancySql.UnprotectTenantTables(MessagingSql.Tables));

            migrationBuilder.DropTable(
                name: "message_events");

            migrationBuilder.DropTable(
                name: "message_templates");

            migrationBuilder.DropTable(
                name: "messaging_settings");

            migrationBuilder.DropTable(
                name: "provider_message_refs");

            migrationBuilder.DropTable(
                name: "outbound_messages");
        }
    }
}
