using System.Globalization;

namespace SupermarketBilling.Domain.Sales;

/// <summary>Rupees and paise in words, in the Indian system (thousand, lakh, crore), as printed on invoices.</summary>
public static class AmountInWords
{
    private static readonly string[] Ones =
    [
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen",
        "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    ];

    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    public static string Rupees(decimal amount)
    {
        if (amount < 0)
        {
            return "Minus " + Rupees(-amount);
        }

        var rounded = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        var rupees = (long)decimal.Truncate(rounded);
        var paise = (int)((rounded - rupees) * 100);
        var words = "Rupees " + Words(rupees);
        if (paise > 0)
        {
            words += " and " + Words(paise) + " Paise";
        }

        return words + " Only";
    }

    public static string Words(long number)
    {
        if (number < 20)
        {
            return Ones[number];
        }

        if (number < 100)
        {
            return Tens[number / 10] + (number % 10 > 0 ? " " + Ones[number % 10] : string.Empty);
        }

        foreach (var (size, name) in new (long Size, string Name)[] { (10_000_000, "Crore"), (100_000, "Lakh"), (1_000, "Thousand"), (100, "Hundred") })
        {
            if (number >= size)
            {
                var rest = number % size;
                return string.Create(CultureInfo.InvariantCulture, $"{Words(number / size)} {name}") + (rest > 0 ? " " + Words(rest) : string.Empty);
            }
        }

        throw new System.Diagnostics.UnreachableException();
    }
}
