namespace SupermarketBilling.Domain.Common;

/// <summary>
/// UUIDv7 values that strictly increase within this process (RFC 9562, method 1: a 12-bit counter in rand_a).
/// <see cref="Guid.CreateVersion7(DateTimeOffset)"/> is random within a millisecond, and EF Core inserts rows in key
/// order, so rows whose database sequence must follow the order they were created in (ledger entries, cost layers)
/// take their ids from here.
/// </summary>
public static class SequentialGuid
{
    private static readonly Lock Gate = new();
    private static long _lastMilliseconds;
    private static int _counter;

    public static Guid Next(DateTimeOffset now)
    {
        long milliseconds;
        int counter;
        lock (Gate)
        {
            milliseconds = Math.Max(now.ToUnixTimeMilliseconds(), _lastMilliseconds);
            if (milliseconds == _lastMilliseconds)
            {
                _counter++;
                if (_counter > 0xFFF)
                {
                    milliseconds++;
                    _counter = 0;
                }
            }
            else
            {
                _counter = 0;
            }

            _lastMilliseconds = milliseconds;
            counter = _counter;
        }

        Span<byte> bytes = stackalloc byte[16];
        Random.Shared.NextBytes(bytes[8..]);
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = (byte)(milliseconds >> (8 * (5 - i)));
        }

        bytes[6] = (byte)(0x70 | (counter >> 8));
        bytes[7] = (byte)counter;
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));
        return new Guid(bytes, bigEndian: true);
    }
}
