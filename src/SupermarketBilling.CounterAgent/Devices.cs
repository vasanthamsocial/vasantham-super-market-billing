using System.Globalization;
using System.IO.Ports;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SupermarketBilling.CounterAgent;

/// <summary>A device is not set up, or not reachable: shown to the cashier as is.</summary>
public sealed class DeviceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sends raw bytes to a printer or display.</summary>
public interface IByteSink
{
    Task SendAsync(byte[] bytes, CancellationToken cancellationToken);
}

public static class ByteSinks
{
    public static IByteSink For(DeviceOptions options, string what)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Transport switch
        {
            DeviceTransport.File => new FileSink(options.FilePath ?? throw new DeviceException($"The {what} file path is not set.")),
            DeviceTransport.Tcp => new TcpSink(options.Host ?? throw new DeviceException($"The {what} host is not set."), options.Port),
            DeviceTransport.Serial => new SerialSink(options.SerialPort ?? throw new DeviceException($"The {what} serial port is not set."), options.BaudRate),
            DeviceTransport.WindowsPrinter => new WindowsPrinterSink(options.Name ?? throw new DeviceException($"The {what} printer name is not set.")),
            _ => throw new DeviceException($"No {what} is set up on this counter."),
        };
    }

    private sealed class FileSink(string path) : IByteSink
    {
        public async Task SendAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TcpSink(string host, int port) : IByteSink
    {
        public async Task SendAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                await client.GetStream().WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
            {
                throw new DeviceException($"The printer at {host}:{port} did not answer. Check it is on and connected.", e);
            }
        }
    }

    private sealed class SerialSink(string portName, int baudRate) : IByteSink
    {
        public Task SendAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            try
            {
                using var port = new SerialPort(portName, baudRate) { WriteTimeout = 3000 };
                port.Open();
                port.Write(bytes, 0, bytes.Length);
                return Task.CompletedTask;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
            {
                throw new DeviceException($"Port {portName} could not be used. Check the cable and that no other program has it open.", e);
            }
        }
    }

    /// <summary>Raw printing through the Windows spooler (winspool.drv), so ESC/POS reaches the printer untouched.</summary>
    private sealed partial class WindowsPrinterSink(string printerName) : IByteSink
    {
        public Task SendAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new DeviceException("Windows printers can only be used on Windows. Use a network (TCP) or serial printer.");
            }

            if (!OpenPrinter(printerName, out var printer, IntPtr.Zero))
            {
                throw new DeviceException($"Printer '{printerName}' was not found. Check its name in Windows printer settings.");
            }

            try
            {
                var document = new DocInfo { DocName = "SupermarketBilling receipt", DataType = "RAW" };
                if (!StartDocPrinter(printer, 1, document) || !StartPagePrinter(printer))
                {
                    throw new DeviceException($"Printer '{printerName}' refused the receipt.");
                }

                var unmanaged = Marshal.AllocCoTaskMem(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, unmanaged, bytes.Length);
                    if (!WritePrinter(printer, unmanaged, bytes.Length, out _))
                    {
                        throw new DeviceException($"Printer '{printerName}' did not accept the receipt.");
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(unmanaged);
                    EndPagePrinter(printer);
                    EndDocPrinter(printer);
                }
            }
            finally
            {
                ClosePrinter(printer);
            }

            return Task.CompletedTask;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class DocInfo
        {
            public string? DocName;
            public string? OutputFile;
            public string? DataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool OpenPrinter(string printerName, out IntPtr printer, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool ClosePrinter(IntPtr printer);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool StartDocPrinter(IntPtr printer, int level, [In] DocInfo document);

        [DllImport("winspool.drv", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool EndDocPrinter(IntPtr printer);

        [DllImport("winspool.drv", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool StartPagePrinter(IntPtr printer);

        [DllImport("winspool.drv", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool EndPagePrinter(IntPtr printer);

        [DllImport("winspool.drv", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool WritePrinter(IntPtr printer, IntPtr bytes, int count, out int written);
    }
}

/// <summary>A weight read from the scale.</summary>
public sealed record Weight(decimal Kilograms, bool Stable);

/// <summary>
/// Reads the weighing scale. Most retail scales send continuous lines such as "ST,GS,+  1.235kg" (ST = stable,
/// US = unstable); a few send only a number. Grams are converted to kilograms.
/// </summary>
public static partial class ScaleReader
{
    public static Weight? Parse(string line)
    {
        var match = WeightPattern().Match(line ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        var value = decimal.Parse(match.Groups["value"].Value.Replace(" ", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture);
        var kilograms = match.Groups["unit"].Value.Equals("g", StringComparison.OrdinalIgnoreCase) ? value / 1000 : value;
        var stable = !line!.Contains("US", StringComparison.OrdinalIgnoreCase);
        return new Weight(decimal.Round(kilograms, 3, MidpointRounding.AwayFromZero), stable);
    }

    public static Task<Weight> ReadAsync(DeviceOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        switch (options.Transport)
        {
            case DeviceTransport.Simulated:
                return Task.FromResult(new Weight(options.SimulatedWeightKg, true));
            case DeviceTransport.Serial:
                try
                {
                    using var port = new SerialPort(options.SerialPort ?? throw new DeviceException("The scale's serial port is not set."), options.BaudRate)
                    {
                        ReadTimeout = 1500,
                        NewLine = "\r\n",
                    };
                    port.Open();
                    port.DiscardInBuffer();
                    var deadline = DateTime.UtcNow.AddSeconds(2);
                    Weight? last = null;
                    while (DateTime.UtcNow < deadline)
                    {
                        last = Parse(port.ReadLine()) ?? last;
                        if (last is { Stable: true })
                        {
                            return Task.FromResult(last);
                        }
                    }

                    return Task.FromResult(last ?? throw new DeviceException("The scale sent nothing readable. Check its cable and settings."));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
                {
                    throw new DeviceException("The scale could not be read. Check its cable and that no other program has the port open.", e);
                }

            default:
                throw new DeviceException("No scale is set up on this counter.");
        }
    }

    [GeneratedRegex(@"(?<value>[+-]?\s*\d+(?:\.\d+)?)\s*(?<unit>kg|g)?", RegexOptions.IgnoreCase)]
    private static partial Regex WeightPattern();
}

/// <summary>A two-line customer pole display using the common CD5220 command set.</summary>
public static class CustomerDisplay
{
    public static byte[] Show(string line1, string line2, int columns)
    {
        static string Fit(string text, int width)
        {
            var clean = EscPos.Clean(text);
            return clean.Length >= width ? clean[..width] : clean.PadRight(width);
        }

        var bytes = new List<byte> { EscPos.Esc, (byte)'@', 0x0C }; // initialise, clear
        bytes.AddRange([EscPos.Esc, (byte)'Q', (byte)'A']);
        bytes.AddRange(Encoding.Latin1.GetBytes(Fit(line1, columns)));
        bytes.Add(0x0D);
        bytes.AddRange([EscPos.Esc, (byte)'Q', (byte)'B']);
        bytes.AddRange(Encoding.Latin1.GetBytes(Fit(line2, columns)));
        bytes.Add(0x0D);
        return [.. bytes];
    }
}
