namespace SupermarketBilling.Domain.Identity;

/// <summary>
/// A signed-in browser session. The browser holds a random token in an HttpOnly cookie; only its SHA-256 hash is
/// stored here, so a database leak does not expose usable session tokens.
/// </summary>
public sealed class Session
{
    private Session()
    {
        TokenHash = CsrfTokenHash = [];
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public byte[] TokenHash { get; private set; }

    /// <summary>Hash of the anti-forgery token that must accompany every state-changing request.</summary>
    public byte[] CsrfTokenHash { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset LastSeenAtUtc { get; private set; }

    public DateTimeOffset IdleExpiresAtUtc { get; private set; }

    public DateTimeOffset AbsoluteExpiresAtUtc { get; private set; }

    /// <summary>True once the second factor has been verified for this session.</summary>
    public bool MfaSatisfied { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public string? RevokedReason { get; private set; }

    public static Session Start(
        Guid userId, byte[] tokenHash, byte[] csrfTokenHash, DateTimeOffset now, TimeSpan idleTimeout, TimeSpan absoluteLifetime,
        string? ipAddress, string? userAgent)
    {
        return new Session
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            TokenHash = tokenHash,
            CsrfTokenHash = csrfTokenHash,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            IdleExpiresAtUtc = now + idleTimeout,
            AbsoluteExpiresAtUtc = now + absoluteLifetime,
            IpAddress = ipAddress,
            UserAgent = userAgent is { Length: > 300 } ? userAgent[..300] : userAgent,
        };
    }

    public bool IsValid(DateTimeOffset now) =>
        RevokedAtUtc is null && now < IdleExpiresAtUtc && now < AbsoluteExpiresAtUtc;

    public void MarkMfaSatisfied() => MfaSatisfied = true;

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAtUtc is null)
        {
            RevokedAtUtc = now;
            RevokedReason = reason;
        }
    }
}
