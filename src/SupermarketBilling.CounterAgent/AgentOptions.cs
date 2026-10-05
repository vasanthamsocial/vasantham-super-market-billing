namespace SupermarketBilling.CounterAgent;

/// <summary>How a device is connected.</summary>
public enum DeviceTransport
{
    /// <summary>Not connected; requests for it are refused with a clear message.</summary>
    None,

    /// <summary>A printer installed in Windows; bytes are sent raw (ESC/POS) through the spooler.</summary>
    WindowsPrinter,

    /// <summary>A network printer on its raw port (usually 9100).</summary>
    Tcp,

    /// <summary>A serial (COM) port, including USB-serial adapters.</summary>
    Serial,

    /// <summary>Appends to a file: for testing without hardware.</summary>
    File,

    /// <summary>Scale only: reports a fixed weight, for testing without hardware.</summary>
    Simulated,
}

public sealed class DeviceOptions
{
    public DeviceTransport Transport { get; set; } = DeviceTransport.None;

    /// <summary>Windows printer name.</summary>
    public string? Name { get; set; }

    public string? Host { get; set; }

    public int Port { get; set; } = 9100;

    public string? SerialPort { get; set; }

    public int BaudRate { get; set; } = 9600;

    public string? FilePath { get; set; }

    /// <summary>Characters per line: 48 for 80 mm paper, 32 for 58 mm; 20 for a customer display.</summary>
    public int Columns { get; set; } = 48;

    /// <summary>Scale <see cref="DeviceTransport.Simulated"/> only.</summary>
    public decimal SimulatedWeightKg { get; set; } = 1.235m;
}

/// <summary>Settings of the counter agent (counter-agent.json).</summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>Where the agent listens. Always loopback: the agent is never reachable from the network.</summary>
    public string Url { get; set; } = "http://127.0.0.1:47800";

    /// <summary>Billing web addresses allowed to use the agent, for example http://store-server:3000.</summary>
    public IList<string> AllowedOrigins { get; init; } = [];

    /// <summary>Shared secret the billing page sends in X-Agent-Token; entered once in the POS hardware settings.</summary>
    public string PairingToken { get; set; } = string.Empty;

    public DeviceOptions Printer { get; set; } = new();

    /// <summary>The cash drawer opens through the receipt printer's drawer port.</summary>
    public bool DrawerEnabled { get; set; }

    public DeviceOptions Scale { get; set; } = new();

    public DeviceOptions Display { get; set; } = new() { Columns = 20 };

    /// <summary>
    /// Where offline bills and the offline price list are kept (encrypted for this Windows user, D-039). Empty: the
    /// SupermarketBilling\offline folder in the user's local application data.
    /// </summary>
    public string? OfflineDirectory { get; set; }
}
