using SupermarketBilling.BackupTool;
using SupermarketBilling.BackupTool.Format;

namespace SupermarketBilling.UnitTests.Backup;

public sealed class BackupPayloadTests
{
    private static readonly BackupManifest Manifest = new(
        "SupermarketBilling",
        "0.1.0",
        "supermarketbilling",
        "16.15",
        new DateTimeOffset(2026, 9, 30, 6, 0, 0, TimeSpan.Zero),
        ["20260929153130_InitialFoundation"],
        new Dictionary<string, long> { ["audit_events"] = 42, ["__ef_migrations_history"] = 1 });

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    [InlineData(100_000)]
    public async Task Splitter_recovers_manifest_and_dump_regardless_of_block_boundaries(int blockSize)
    {
        var dump = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        var payload = BackupPayload.CreatePrefix(Manifest).Concat(dump).ToArray();
        using var received = new MemoryStream();
        var splitter = new PayloadSplitter((data, ct) => received.WriteAsync(data, ct));

        for (var offset = 0; offset < payload.Length; offset += blockSize)
        {
            await splitter.WriteAsync(payload.AsMemory(offset, Math.Min(blockSize, payload.Length - offset)), CancellationToken.None);
        }

        var manifest = splitter.Complete();
        Assert.Equal("supermarketbilling", manifest.Database);
        Assert.Equal(42, manifest.TableRowCounts["audit_events"]);
        Assert.Equal(dump, received.ToArray());
        Assert.Equal(dump.Length, splitter.DumpBytes);
    }

    [Fact]
    public async Task Payload_without_a_dump_is_rejected()
    {
        var splitter = new PayloadSplitter(null);
        await splitter.WriteAsync(BackupPayload.CreatePrefix(Manifest), CancellationToken.None);

        Assert.Throws<BackupIntegrityException>(() => splitter.Complete());
    }

    [Fact]
    public async Task Prefixed_stream_yields_prefix_then_inner_content()
    {
        await using var stream = new PrefixedReadStream([1, 2, 3], new MemoryStream([4, 5]));
        using var copy = new MemoryStream();

        await stream.CopyToAsync(copy);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, copy.ToArray());
    }

    [Theory]
    [InlineData("supermarketbilling")]
    [InlineData("sb_restoretest_20260930064310_fb0846")]
    public void Valid_database_names_are_accepted(string name)
    {
        BackupService.ValidateDatabaseName(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Uppercase")]
    [InlineData("name; DROP DATABASE x")]
    [InlineData("name\"quoted")]
    [InlineData("1starts_with_digit")]
    public void Unsafe_database_names_are_rejected(string name)
    {
        Assert.Throws<BackupException>(() => BackupService.ValidateDatabaseName(name));
    }

    [Theory]
    [InlineData("--database")]
    [InlineData("--unknown", "x")]
    [InlineData("positional")]
    public void Command_line_rejects_invalid_arguments(params string[] arguments)
    {
        Assert.Throws<BackupException>(() => CommandLine.Parse(arguments));
    }

    [Fact]
    public void Command_line_parses_values_and_flags()
    {
        var options = CommandLine.Parse(["--file", "a.sbbak", "--replace", "--confirm", "db"]);

        Assert.Equal("a.sbbak", options.Required("file"));
        Assert.Equal("db", options.Value("confirm"));
        Assert.True(options.Flag("replace"));
        Assert.False(options.Flag("keep"));
    }
}
