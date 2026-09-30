using System.Buffers.Binary;
using System.Text.Json;

namespace SupermarketBilling.BackupTool.Format;

/// <summary>
/// Describes what a backup contains. It is stored encrypted inside the backup, ahead of the database dump,
/// and is used by the restore test to prove the restored database matches the source exactly.
/// </summary>
internal sealed record BackupManifest(
    string Application,
    string ToolVersion,
    string Database,
    string ServerVersion,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<string> Migrations,
    IReadOnlyDictionary<string, long> TableRowCounts);

/// <summary>Encrypted payload layout: int32 manifest length | manifest JSON | pg_dump custom-format archive.</summary>
internal static class BackupPayload
{
    public const int MaxManifestSize = 4 * 1024 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static byte[] CreatePrefix(BackupManifest manifest)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var prefix = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, json.Length);
        json.CopyTo(prefix, 4);
        return prefix;
    }
}

/// <summary>Consumes decrypted payload blocks, extracting the manifest and forwarding the dump bytes.</summary>
internal sealed class PayloadSplitter(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? dumpSink)
{
    private readonly byte[] _lengthBuffer = new byte[4];
    private int _lengthFilled;
    private byte[]? _manifestBuffer;
    private int _manifestFilled;

    public BackupManifest? Manifest { get; private set; }

    public long DumpBytes { get; private set; }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        while (!data.IsEmpty)
        {
            if (_lengthFilled < _lengthBuffer.Length)
            {
                var take = Math.Min(_lengthBuffer.Length - _lengthFilled, data.Length);
                data[..take].CopyTo(_lengthBuffer.AsMemory(_lengthFilled));
                _lengthFilled += take;
                data = data[take..];
                if (_lengthFilled == _lengthBuffer.Length)
                {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(_lengthBuffer);
                    if (length <= 0 || length > BackupPayload.MaxManifestSize)
                    {
                        throw new BackupIntegrityException("The backup manifest length is invalid.");
                    }

                    _manifestBuffer = new byte[length];
                }

                continue;
            }

            if (Manifest is null)
            {
                var buffer = _manifestBuffer!;
                var take = Math.Min(buffer.Length - _manifestFilled, data.Length);
                data[..take].CopyTo(buffer.AsMemory(_manifestFilled));
                _manifestFilled += take;
                data = data[take..];
                if (_manifestFilled == buffer.Length)
                {
                    Manifest = ParseManifest(buffer);
                }

                continue;
            }

            DumpBytes += data.Length;
            if (dumpSink is not null)
            {
                await dumpSink(data, cancellationToken).ConfigureAwait(false);
            }

            return;
        }
    }

    public BackupManifest Complete()
    {
        if (Manifest is null)
        {
            throw new BackupIntegrityException("The backup does not contain a manifest.");
        }

        if (DumpBytes == 0)
        {
            throw new BackupIntegrityException("The backup does not contain a database dump.");
        }

        return Manifest;
    }

    private static BackupManifest ParseManifest(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(json, BackupPayload.JsonOptions)
                ?? throw new BackupIntegrityException("The backup manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new BackupIntegrityException("The backup manifest is not valid JSON.", ex);
        }
    }
}

/// <summary>A read-only stream that yields a prefix followed by the contents of another stream.</summary>
internal sealed class PrefixedReadStream(byte[] prefix, Stream inner) : Stream
{
    private int _prefixPosition;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_prefixPosition < prefix.Length)
        {
            var take = Math.Min(prefix.Length - _prefixPosition, buffer.Length);
            prefix.AsSpan(_prefixPosition, take).CopyTo(buffer);
            _prefixPosition += take;
            return take;
        }

        return inner.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixPosition < prefix.Length)
        {
            var take = Math.Min(prefix.Length - _prefixPosition, buffer.Length);
            prefix.AsMemory(_prefixPosition, take).CopyTo(buffer);
            _prefixPosition += take;
            return take;
        }

        return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
