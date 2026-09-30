using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Tax;

/// <summary>A business's legal GST registration status. This is a legal fact, not a setting to switch freely.</summary>
public static class TaxRegistrationModes
{
    /// <summary>Regular GST taxpayer: tax invoices, CGST/SGST or IGST, input tax credit.</summary>
    public const string GstRegular = "GST_REGULAR";

    /// <summary>Composition scheme: bill of supply, no tax collected from customers, no input tax credit.</summary>
    public const string GstComposition = "GST_COMPOSITION";

    /// <summary>Not registered: commercial invoices, no GST collected, no GSTIN printed.</summary>
    public const string NotGstRegistered = "NOT_GST_REGISTERED";

    public static readonly IReadOnlyList<string> All = [GstRegular, GstComposition, NotGstRegistered];

    public static string Validate(string mode) =>
        All.Contains(mode) ? mode : throw new DomainException("tax_mode.unknown", $"Unknown tax registration mode '{mode}'.");

    public static bool RequiresGstin(string mode) => mode is GstRegular or GstComposition;
}

/// <summary>
/// One entry in a business's tax-registration history: the mode that applies from <see cref="EffectiveFrom"/>
/// until the next entry. Entries are append-only; a change is a new entry, so invoices issued under an earlier mode
/// are never reinterpreted.
/// </summary>
public sealed class TaxRegistration : ITenantOwned
{
    private TaxRegistration()
    {
        Mode = Reason = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string Mode { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    /// <summary>GSTIN in force for this entry (snapshot; required for GST modes).</summary>
    public string? Gstin { get; private set; }

    public string Reason { get; private set; }

    /// <summary>Where the supporting registration evidence is kept (certificate number, file reference).</summary>
    public string? EvidenceReference { get; private set; }

    public Guid RecordedByUserId { get; private set; }

    /// <summary>The approval that authorised this change; null only for the initial entry made at setup.</summary>
    public Guid? ApprovalRequestId { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public static TaxRegistration Initial(Guid businessId, string mode, string? gstin, DateOnly effectiveFrom, Guid recordedBy, DateTimeOffset now) =>
        Create(businessId, mode, gstin, effectiveFrom, "Initial registration recorded at setup", evidenceReference: null, recordedBy, approvalRequestId: null, now);

    /// <summary>A change of mode. Must start after the current entry and not in the past (history is never rewritten).</summary>
    public static TaxRegistration Change(
        TaxRegistration current, string mode, string? gstin, DateOnly effectiveFrom, DateOnly today, string reason, string? evidenceReference,
        Guid recordedBy, Guid approvalRequestId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidateChange(current, mode, effectiveFrom, today, reason);
        return Create(current.BusinessId, mode, gstin, effectiveFrom, reason, evidenceReference, recordedBy, approvalRequestId, now);
    }

    public static void ValidateChange(TaxRegistration current, string mode, DateOnly effectiveFrom, DateOnly today, string reason)
    {
        ArgumentNullException.ThrowIfNull(current);
        TaxRegistrationModes.Validate(mode);
        if (effectiveFrom < today)
        {
            throw new DomainException("tax_mode.backdated", "A tax registration change cannot take effect in the past; issued invoices must keep the mode they were issued under.");
        }

        if (effectiveFrom <= current.EffectiveFrom)
        {
            throw new DomainException("tax_mode.not_after_current", $"The change must take effect after {current.EffectiveFrom:yyyy-MM-dd}, when the current entry started.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
        {
            throw new DomainException("tax_mode.reason_required", "Explain the reason for the change (at least 10 characters).");
        }
    }

    /// <summary>The entry in force on <paramref name="date"/>, or null if the date is before the first entry.</summary>
    public static TaxRegistration? InForce(IEnumerable<TaxRegistration> history, DateOnly date) =>
        history.Where(r => r.EffectiveFrom <= date).MaxBy(r => r.EffectiveFrom);

    private static TaxRegistration Create(
        Guid businessId, string mode, string? gstin, DateOnly effectiveFrom, string reason, string? evidenceReference,
        Guid recordedBy, Guid? approvalRequestId, DateTimeOffset now)
    {
        TaxRegistrationModes.Validate(mode);
        var normalizedGstin = string.IsNullOrWhiteSpace(gstin) ? null : Tax.Gstin.Normalize(gstin);
        if (TaxRegistrationModes.RequiresGstin(mode))
        {
            if (normalizedGstin is null || !Tax.Gstin.IsValid(normalizedGstin))
            {
                throw new DomainException("tax_mode.gstin_required", "GST Regular and Composition need the business's valid GSTIN.");
            }
        }
        else
        {
            normalizedGstin = null; // Not registered: no GSTIN is printed or used.
        }

        return new TaxRegistration
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            Mode = mode,
            EffectiveFrom = effectiveFrom,
            Gstin = normalizedGstin,
            Reason = reason.Trim(),
            EvidenceReference = string.IsNullOrWhiteSpace(evidenceReference) ? null : evidenceReference.Trim(),
            RecordedByUserId = recordedBy,
            ApprovalRequestId = approvalRequestId,
            RecordedAtUtc = now,
        };
    }
}
