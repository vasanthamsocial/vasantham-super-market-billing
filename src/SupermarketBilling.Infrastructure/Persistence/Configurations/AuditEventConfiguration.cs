using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupermarketBilling.Domain.Auditing;

namespace SupermarketBilling.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Sequence).UseIdentityAlwaysColumn();
        builder.HasIndex(e => e.Sequence).IsUnique();
        builder.Property(e => e.OccurredAtUtc).IsRequired();
        builder.Property(e => e.EventType).HasMaxLength(AuditEvent.MaxEventTypeLength).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(AuditEvent.MaxEntityTypeLength);
        builder.Property(e => e.EntityId).HasMaxLength(AuditEvent.MaxEntityIdLength);
        builder.Property(e => e.CorrelationId).HasMaxLength(100);
        builder.Property(e => e.PayloadJson).HasColumnType("jsonb").IsRequired();

        builder.HasIndex(e => e.OccurredAtUtc);
        builder.HasIndex(e => new { e.EntityType, e.EntityId });
        builder.HasIndex(e => new { e.BusinessId, e.StoreId, e.OccurredAtUtc });
    }
}
