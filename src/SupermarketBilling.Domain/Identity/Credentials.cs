using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Identity;

/// <summary>
/// One-time password reset code issued by an authorised manager (the store works offline, so there is no email
/// link). Only the hash is stored; the code is shown once to the manager and handed to the user in person.
/// </summary>
public sealed class PasswordResetToken : ITenantOwned
{
    private PasswordResetToken()
    {
        TokenHash = [];
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public byte[] TokenHash { get; private set; }

    public Guid IssuedByUserId { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? UsedAtUtc { get; private set; }

    public static PasswordResetToken Issue(Guid userId, byte[] tokenHash, Guid issuedBy, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.CreateVersion7(now),
        UserId = userId,
        TokenHash = tokenHash,
        IssuedByUserId = issuedBy,
        IssuedAtUtc = now,
        ExpiresAtUtc = now + lifetime,
    };

    public bool IsUsable(DateTimeOffset now) => UsedAtUtc is null && now < ExpiresAtUtc;

    public void MarkUsed(DateTimeOffset now) => UsedAtUtc ??= now;
}

/// <summary>Single-use MFA recovery code, stored as a hash.</summary>
public sealed class MfaRecoveryCode : ITenantOwned
{
    private MfaRecoveryCode()
    {
        CodeHash = [];
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public byte[] CodeHash { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? UsedAtUtc { get; private set; }

    public static MfaRecoveryCode Create(Guid userId, byte[] codeHash, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        UserId = userId,
        CodeHash = codeHash,
        CreatedAtUtc = now,
    };

    public void MarkUsed(DateTimeOffset now) => UsedAtUtc ??= now;
}
