using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SupermarketBilling.Domain.Identity;

namespace SupermarketBilling.Infrastructure.Security;

/// <summary>ASP.NET Core Identity's PBKDF2-HMAC-SHA512 password hasher with a configurable work factor.</summary>
public sealed class PasswordHashing
{
    private readonly PasswordHasher<User> _hasher;

    // A valid hash of a random password, verified when the username does not exist so that response time
    // does not reveal which usernames are real.
    private readonly string _dummyHash;

    public PasswordHashing(IOptions<SecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _hasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = options.Value.PasswordHashIterations,
        }));
        _dummyHash = _hasher.HashPassword(null!, SecretTokens.NewToken());
    }

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    /// <returns>Whether the password matches, and whether the stored hash should be upgraded.</returns>
    public (bool Valid, bool NeedsRehash) Verify(string hash, string password)
    {
        var result = _hasher.VerifyHashedPassword(null!, hash, password);
        return (result != PasswordVerificationResult.Failed, result == PasswordVerificationResult.SuccessRehashNeeded);
    }

    public void VerifyDummy(string password) => _hasher.VerifyHashedPassword(null!, _dummyHash, password);
}
