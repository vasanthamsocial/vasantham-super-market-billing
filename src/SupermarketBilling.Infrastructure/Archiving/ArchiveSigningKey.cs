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

    /// <summary>The archive server's private key for signing archive receipts (PEM, made on first use, D-044). Keep App_Data private.</summary>
    public string ReceiptKeyFile { get; set; } = Path.Combine("App_Data", "archive-receipt-key.pem");
}

/// <summary>
/// Retention on a store server (spec section 22, D-044): removing archived months. Off unless switched on in the
/// configuration (<c>Retention:Enabled</c>); even then only the owner, with the archive's receipt, may act.
/// </summary>
public sealed class RetentionOptions
{
    public const string Section = "Retention";

    public bool Enabled { get; set; }
}

/// <summary>A private key kept in a PEM file next to the API, made on first use (never in the database or source control).</summary>
internal sealed class KeyFile(string path, Func<string> create)
{
    private readonly Lock _gate = new();
    private string? _pem;

    public string Path { get; } = path;

    public string Pem()
    {
        lock (_gate)
        {
            if (_pem is not null)
            {
                return _pem;
            }

            if (!File.Exists(Path))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var temporary = Path + ".tmp";
                File.WriteAllText(temporary, create());
                File.Move(temporary, Path);
            }

            _pem = File.ReadAllText(Path);
            return _pem;
        }
    }
}

/// <summary>
/// The key this store server signs its archive packages with (ECDSA P-256). Made on first use and kept in a file next
/// to the API (never in the database or source control); the archive server trusts its public key once registered.
/// </summary>
public sealed class ArchiveSigningKey(IOptions<ArchiveOptions> options, IHostEnvironment environment)
{
    private readonly KeyFile _file = new(Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.SigningKeyFile)), NewEcdsa);

    public string FilePath => _file.Path;

    internal static string NewEcdsa()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }

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

    private string Pem() => _file.Pem();
}

/// <summary>
/// The archive server's key (ECDH P-256): store servers encrypt their packages for its public key, and only this private
/// key opens them. Made on first use and kept in a file next to the API.
/// </summary>
public sealed class ArchiveRecipientKey(IOptions<ArchiveOptions> options, IHostEnvironment environment)
{
    private readonly KeyFile _file = new(Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.RecipientKeyFile)), () =>
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    });

    public string FilePath => _file.Path;

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

    private string Pem() => _file.Pem();
}

/// <summary>
/// The archive server's receipt key (ECDSA P-256, D-044): it signs the receipts that let a store server remove a month
/// the archive holds, approved. Store servers register its public key. Made on first use, kept in a file next to the API.
/// </summary>
public sealed class ArchiveReceiptKey(IOptions<ArchiveOptions> options, IHostEnvironment environment)
{
    private readonly KeyFile _file = new(Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.ReceiptKeyFile)), ArchiveSigningKey.NewEcdsa);

    public ECDsa Create()
    {
        var key = ECDsa.Create();
        key.ImportFromPem(_file.Pem());
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
}
