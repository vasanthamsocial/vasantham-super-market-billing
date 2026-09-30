namespace SupermarketBilling.Api;

internal static partial class StartupLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "SupermarketBilling API {Version} started in {Environment}")]
    public static partial void Started(ILogger logger, string version, string environment);
}
