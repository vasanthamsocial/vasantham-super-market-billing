using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SupermarketBilling.Archiving;

namespace SupermarketBilling.Infrastructure.Archiving;

public sealed class ArchiveOptions
{
    public const string Section = "Archive";

    /// <summary>This installation's private key for signing archive packages (PEM, made on first use). Keep App_Data private.</summary>
    public string SigningKeyFile { get; set; } = Path.Combine("App_Data", "archive-signing-key.pem");

    /// <summary>
    /// True on the archive server (D-042): only sign-in and archive endpoints are served, against the archive database.
    /// </summary>
    public bool Server { get; set; }

    /// <summary>The archive server's private key that packages are encrypted for (PEM, made on first use). Keep App_Data private.</summary>
    public string RecipientKeyFile { get; set; } = Path.Combine("App_Data", "archive-recipient-key.pem");
}

/// <summary>
/// The key this store server signs its archive packages with (ECDSA P-256). Made on first use and kept in a file next
/// to the API (never in the database or source control); the archive server trusts its public key once registered.
/// </summary>
public sealed class ArchiveSigningKey(IOptions<ArchiveOptions> options, IHostEnvironment environment)
{
    private readonly Lock _gate = new();
    private string? _pem;

    public string FilePath => Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.SigningKeyFile));

    public ECDsa Create()
    {
        var key = ECDsa.Create();
        key.ImportFromPem(Pem());
        return key;
    }

    public string PublicKeyPem()
    {
        using var key = Create();
        return key.ExportSubjectPublicKeyInfoPem();
    }

    public string KeyId()
    {
        using var key = Create();
        return ArchivePackage.KeyId(key.ExportSubjectPublicKeyInfo());
    }

    private string Pem()
    {
        lock (_gate)
        {
            if (_pem is not null)
            {
                return _pem;
            }

            if (!File.Exists(FilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, key.ExportPkcs8PrivateKeyPem());
                File.Move(temporary, FilePath);
            }

            _pem = File.ReadAllText(FilePath);
            return _pem;
        }
    }
}

/// <summary>
/// The archive server's key (ECDH P-256): store servers encrypt their packages for its public key, and only this private
/// key opens them. Made on first use and kept in a file next to the API.
/// </summary>
public sealed class ArchiveRecipientKey(IOptions<ArchiveOptions> options, IHostEnvironment environment)
{
    private readonly Lock _gate = new();
    private string? _pem;

    public string FilePath => Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.RecipientKeyFile));

    public ECDiffieHellman Create()
    {
        var key = ECDiffieHellman.Create();
        key.ImportFromPem(Pem());
        return key;
    }

    public string PublicKeyPem()
    {
        using var key = Create();
        return key.ExportSubjectPublicKeyInfoPem();
    }

    public string KeyId()
    {
        using var key = Create();
        return ArchivePackage.KeyId(key.ExportSubjectPublicKeyInfo());
    }

    private string Pem()
    {
        lock (_gate)
        {
            if (_pem is not null)
            {
                return _pem;
            }

            if (!File.Exists(FilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, key.ExportPkcs8PrivateKeyPem());
                File.Move(temporary, FilePath);
            }

            _pem = File.ReadAllText(FilePath);
            return _pem;
        }
    }
}
