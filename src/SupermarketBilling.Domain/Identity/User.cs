using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Identity;

/// <summary>
/// A named person who signs in. Users are shared across businesses; what they can do in each business comes from
/// <see cref="RoleAssignment"/>s. Credentials are stored only as hashes (password) or encrypted (MFA secret).
/// </summary>
public sealed partial class User : ITenantOwned
{
    public const int MinPasswordLength = 10;
    public const int MaxPasswordLength = 128;

    private User()
    {
        Username = DisplayName = PasswordHash = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Lower-case login name, unique across the installation.</summary>
    public string Username { get; private set; }

    public string DisplayName { get; private set; }

    public bool IsActive { get; private set; }

    public string PasswordHash { get; private set; }

    public DateTimeOffset PasswordChangedAtUtc { get; private set; }

    /// <summary>Set for new users and after a reset; the user must choose a new password before doing anything else.</summary>
    public bool MustChangePassword { get; private set; }

    public int FailedLoginCount { get; private set; }

    public DateTimeOffset? LockedUntilUtc { get; private set; }

    public DateTimeOffset? LastLoginAtUtc { get; private set; }

    public bool MfaEnabled { get; private set; }

    /// <summary>TOTP secret, encrypted by the application's data-protection key.</summary>
    public string? MfaSecretProtected { get; private set; }

    /// <summary>Secret generated during MFA enrolment, awaiting confirmation with a valid code.</summary>
    public string? MfaPendingSecretProtected { get; private set; }

    /// <summary>Last accepted TOTP time step, preventing reuse of the same code.</summary>
    public long? MfaLastUsedStep { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static User Create(string username, string displayName, string passwordHash, DateTimeOffset now, bool mustChangePassword)
    {
        return new User
        {
            Id = Guid.CreateVersion7(now),
            Username = NormalizeUsername(username),
            DisplayName = ValidateDisplayName(displayName),
            PasswordHash = passwordHash,
            PasswordChangedAtUtc = now,
            MustChangePassword = mustChangePassword,
            IsActive = true,
            CreatedAtUtc = now,
        };
    }

    public static string NormalizeUsername(string username)
    {
        var normalized = (username ?? string.Empty).Trim().ToLowerInvariant();
        if (!UsernamePattern().IsMatch(normalized))
        {
            throw new DomainException("user.username_invalid", "Username must be 3-50 characters: letters, digits, dot, hyphen or underscore.");
        }

        return normalized;
    }

    /// <summary>Checks the password policy. Returns null when acceptable, otherwise the reason.</summary>
    public static string? CheckPasswordPolicy(string password, string username)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            return $"Password must be at least {MinPasswordLength} characters.";
        }

        if (password.Length > MaxPasswordLength)
        {
            return $"Password must be at most {MaxPasswordLength} characters.";
        }

        if (password.Contains(username, StringComparison.OrdinalIgnoreCase))
        {
            return "Password must not contain the username.";
        }

        if (password.Distinct().Count() < 4)
        {
            return "Password is too simple.";
        }

        return null;
    }

    public bool IsLockedOut(DateTimeOffset now) => LockedUntilUtc is { } until && until > now;

    /// <returns>True when this failure caused the account to lock.</returns>
    public bool RecordFailedLogin(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= maxAttempts)
        {
            LockedUntilUtc = now + lockoutDuration;
            FailedLoginCount = 0;
            return true;
        }

        return false;
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockedUntilUtc = null;
        LastLoginAtUtc = now;
    }

    public void Unlock()
    {
        FailedLoginCount = 0;
        LockedUntilUtc = null;
    }

    public void SetPassword(string passwordHash, DateTimeOffset now, bool mustChangePassword)
    {
        PasswordHash = passwordHash;
        PasswordChangedAtUtc = now;
        MustChangePassword = mustChangePassword;
        Unlock();
    }

    public void Rename(string displayName) => DisplayName = ValidateDisplayName(displayName);

    public void SetActive(bool isActive) => IsActive = isActive;

    public void BeginMfaEnrolment(string protectedSecret) => MfaPendingSecretProtected = protectedSecret;

    public void ConfirmMfaEnrolment(long usedStep)
    {
        if (MfaPendingSecretProtected is null)
        {
            throw new DomainException("mfa.no_pending_enrolment", "Start MFA setup first.");
        }

        MfaSecretProtected = MfaPendingSecretProtected;
        MfaPendingSecretProtected = null;
        MfaEnabled = true;
        MfaLastUsedStep = usedStep;
    }

    public void RecordMfaStep(long step) => MfaLastUsedStep = step;

    public void DisableMfa()
    {
        MfaEnabled = false;
        MfaSecretProtected = null;
        MfaPendingSecretProtected = null;
        MfaLastUsedStep = null;
    }

    private static string ValidateDisplayName(string displayName)
    {
        var trimmed = displayName?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 100)
        {
            throw new DomainException("user.display_name_invalid", "Display name is required (max 100 characters).");
        }

        return trimmed;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,49}$")]
    private static partial Regex UsernamePattern();
}
