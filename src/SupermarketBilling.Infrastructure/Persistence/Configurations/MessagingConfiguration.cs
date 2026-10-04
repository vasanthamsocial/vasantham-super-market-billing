using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Messaging;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal sealed class MessageTemplateConfiguration : IEntityTypeConfiguration<MessageTemplate>
{
    public void Configure(EntityTypeBuilder<MessageTemplate> builder)
    {
        builder.ToTable("message_templates", t =>
        {
            t.HasCheckConstraint("ck_message_templates_kind", "kind IN ('CREDIT_INVOICE', 'RECEIPT') AND channel IN ('WHATSAPP', 'SMS')");
        });
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Kind).HasMaxLength(20).IsRequired();
        builder.Property(t => t.Channel).HasMaxLength(10).IsRequired();
        builder.Property(t => t.ProviderTemplateName).HasMaxLength(100);
        builder.Property(t => t.DltTemplateId).HasMaxLength(30);
        builder.Property(t => t.LanguageCode).HasMaxLength(10).IsRequired();
        builder.Property(t => t.Body).HasMaxLength(1000).IsRequired();
        builder.Property(t => t.RowVersion).IsRowVersion();
        builder.HasIndex(t => new { t.BusinessId, t.Kind, t.Channel }).IsUnique();
        builder.BelongsToBusinessInTenant();
    }
}

internal sealed class OutboundMessageConfiguration : IEntityTypeConfiguration<OutboundMessage>
{
    public void Configure(EntityTypeBuilder<OutboundMessage> builder)
    {
        builder.ToTable("outbound_messages", t =>
        {
            t.HasCheckConstraint("ck_outbound_messages_kind", "kind IN ('CREDIT_INVOICE', 'RECEIPT') AND channel IN ('WHATSAPP', 'SMS')");
            t.HasCheckConstraint("ck_outbound_messages_status", "status IN ('QUEUED', 'SENT', 'DELIVERED', 'READ', 'FAILED', 'SKIPPED')");
            t.HasCheckConstraint("ck_outbound_messages_steps",
                "(status = 'SKIPPED') = (skip_reason IS NOT NULL) AND (status <> 'QUEUED' OR next_attempt_at_utc IS NOT NULL) " +
                "AND (status NOT IN ('SENT', 'DELIVERED', 'READ') OR (provider_message_id IS NOT NULL AND sent_at_utc IS NOT NULL)) AND attempts BETWEEN 0 AND 5");
        });
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Channel).HasMaxLength(10).IsRequired();
        builder.Property(m => m.Kind).HasMaxLength(20).IsRequired();
        builder.Property(m => m.DocumentNumber).HasMaxLength(40).IsRequired();
        builder.Property(m => m.ToNumber).HasMaxLength(13);
        builder.Property(m => m.ParametersJson).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.Body).HasMaxLength(1000).IsRequired();
        builder.Property(m => m.Status).HasMaxLength(10).IsRequired();
        builder.Property(m => m.SkipReason).HasMaxLength(100);
        builder.Property(m => m.LastError).HasMaxLength(500);
        builder.Property(m => m.ProviderMessageId).HasMaxLength(200);
        builder.Property(m => m.AttachmentSha256).HasMaxLength(64);
        builder.Property(m => m.RowVersion).IsRowVersion();
        builder.HasIndex(m => new { m.BusinessId, m.Kind, m.Channel, m.DocumentId }).IsUnique();
        builder.HasIndex(m => new { m.Status, m.NextAttemptAtUtc });
        builder.HasIndex(m => m.DebtorId);
        builder.HasIndex(m => m.ProviderMessageId);
        builder.BelongsToBusinessInTenant();
        builder.HasOne<Debtor>().WithMany().HasForeignKey(m => new { m.DebtorId, m.BusinessId })
            .HasPrincipalKey(d => new { d.Id, d.BusinessId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MessageEventConfiguration : IEntityTypeConfiguration<MessageEvent>
{
    public void Configure(EntityTypeBuilder<MessageEvent> builder)
    {
        builder.ToTable("message_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Detail).HasMaxLength(500);
        builder.HasIndex(e => e.MessageId);
        builder.BelongsToBusinessInTenant();
        builder.HasOne<OutboundMessage>().WithMany().HasForeignKey(e => e.MessageId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MessagingSettingsConfiguration : IEntityTypeConfiguration<MessagingSettings>
{
    public void Configure(EntityTypeBuilder<MessagingSettings> builder)
    {
        builder.ToTable("messaging_settings");
        builder.HasKey(s => s.BusinessId);
        builder.Property(s => s.WhatsAppEnabled).HasColumnName("whatsapp_enabled");
        builder.Property(s => s.RowVersion).IsRowVersion();
        builder.BelongsToBusinessInTenant();
    }
}

/// <summary>
/// Which tenant a provider's message id belongs to, so a delivery report (which arrives without a signed-in user or tenant)
/// can find its message. Holds no message content. Protected by row-level security; the webhook reads it only through
/// <c>sb_provider_message_ref</c>.
/// </summary>
public sealed class ProviderMessageRef
{
    public string ProviderMessageId { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    public Guid MessageId { get; set; }
}

internal sealed class ProviderMessageRefConfiguration : IEntityTypeConfiguration<ProviderMessageRef>
{
    public void Configure(EntityTypeBuilder<ProviderMessageRef> builder)
    {
        builder.ToTable("provider_message_refs");
        builder.HasKey(r => r.ProviderMessageId);
        builder.Property(r => r.ProviderMessageId).HasMaxLength(200);
    }
}
