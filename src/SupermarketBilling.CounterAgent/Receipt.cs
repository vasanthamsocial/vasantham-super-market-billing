using System.Globalization;

namespace SupermarketBilling.CounterAgent;

/// <summary>The issued invoice as the billing page has it (the API's invoice), to print. Only what the receipt needs.</summary>
public sealed record ReceiptInvoice(
    string Number,
    string Kind,
    string TaxMode,
    DateTimeOffset IssuedAtUtc,
    string CounterCode,
    string Cashier,
    string SellerName,
    string SellerAddress,
    string? SellerGstin,
    string? BuyerName,
    string? BuyerGstin,
    bool IsInterState,
    IReadOnlyList<ReceiptLine> Lines,
    decimal DiscountTotal,
    decimal TaxableTotal,
    decimal CgstTotal,
    decimal SgstTotal,
    decimal IgstTotal,
    decimal CessTotal,
    decimal RoundOff,
    decimal GrandTotal,
    IReadOnlyList<ReceiptPayment> Payments,
    decimal ChangeDue,
    string? Declaration);

public sealed record ReceiptLine(string Description, decimal Quantity, string UnitCode, decimal UnitPrice, decimal? Mrp, decimal Total, decimal GstRatePercent, string SupplyType);

public sealed record ReceiptPayment(string Method, decimal Amount, string? Reference);

public sealed record PrintReceiptRequest(ReceiptInvoice Invoice, bool OpenDrawer);

/// <summary>Lays out a receipt for a fixed-width receipt printer (48 columns on 80 mm paper, 32 on 58 mm).</summary>
public static class ReceiptFormatter
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    public static byte[] Format(ReceiptInvoice invoice, int columns, bool openDrawer)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var p = new EscPos();
        var rule = new string('-', columns);
        var tax = invoice.TaxMode == "GST_REGULAR";

        p.Align('C').Bold(true).Line(invoice.SellerName).Bold(false);
        foreach (var line in EscPos.Wrap(invoice.SellerAddress, columns))
        {
            p.Line(line);
        }

        if (invoice.SellerGstin is { } gstin)
        {
            p.Line($"GSTIN: {gstin}");
        }

        p.Bold(true).Line(invoice.Kind switch { "TAX_INVOICE" => "TAX INVOICE", "BILL_OF_SUPPLY" => "BILL OF SUPPLY", _ => "INVOICE" }).Bold(false).Align('L');
        p.Line(EscPos.TwoColumns($"No. {invoice.Number}", invoice.IssuedAtUtc.ToLocalTime().ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture), columns));
        p.Line(EscPos.TwoColumns($"Counter {invoice.CounterCode}", invoice.Cashier, columns));
        if (invoice.BuyerName is not null || invoice.BuyerGstin is not null)
        {
            p.Line($"To: {invoice.BuyerName}");
            if (invoice.BuyerGstin is { } bg)
            {
                p.Line($"GSTIN: {bg}");
            }
        }

        p.Line(rule);
        foreach (var line in invoice.Lines)
        {
            foreach (var part in EscPos.Wrap(line.Description + (line.Mrp is { } mrp ? $" (MRP {Money(mrp)})" : string.Empty), columns))
            {
                p.Line(part);
            }

            var detail = $"  {line.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} {line.UnitCode} x {Money(line.UnitPrice)}" +
                (tax && line.SupplyType == "TAXABLE" ? $" @{line.GstRatePercent.ToString("0.##", CultureInfo.InvariantCulture)}%" : string.Empty);
            p.Line(EscPos.TwoColumns(detail, Money(line.Total), columns));
        }

        p.Line(rule);
        p.Line(EscPos.TwoColumns("Items", invoice.Lines.Count.ToString(CultureInfo.InvariantCulture), columns));
        if (invoice.DiscountTotal > 0)
        {
            p.Line(EscPos.TwoColumns("You saved", Money(invoice.DiscountTotal), columns));
        }

        if (tax)
        {
            p.Line(EscPos.TwoColumns("Taxable value", Money(invoice.TaxableTotal), columns));
            if (invoice.IsInterState)
            {
                p.Line(EscPos.TwoColumns("IGST", Money(invoice.IgstTotal), columns));
            }
            else
            {
                p.Line(EscPos.TwoColumns("CGST", Money(invoice.CgstTotal), columns));
                p.Line(EscPos.TwoColumns("SGST", Money(invoice.SgstTotal), columns));
            }

            if (invoice.CessTotal > 0)
            {
                p.Line(EscPos.TwoColumns("Cess", Money(invoice.CessTotal), columns));
            }
        }

        if (invoice.RoundOff != 0)
        {
            p.Line(EscPos.TwoColumns("Round off", Money(invoice.RoundOff), columns));
        }

        // Double size: half the columns.
        p.Bold(true).Large(true).Line(EscPos.TwoColumns("TOTAL", Money(invoice.GrandTotal), columns / 2)).Large(false).Bold(false);
        foreach (var payment in invoice.Payments)
        {
            p.Line(EscPos.TwoColumns(payment.Method + (payment.Reference is { } r ? $" ({r})" : string.Empty), Money(payment.Amount), columns));
        }

        if (invoice.ChangeDue > 0)
        {
            p.Line(EscPos.TwoColumns("Change", Money(invoice.ChangeDue), columns));
        }

        if (invoice.Declaration is { } declaration)
        {
            foreach (var part in EscPos.Wrap(declaration, columns))
            {
                p.Line(part);
            }
        }

        if (invoice.TaxMode == "NOT_GST_REGISTERED")
        {
            p.Line("Seller not registered under GST.");
        }

        p.Align('C').Line().Line("Thank you. Please visit again.").Align('L').Cut();
        if (openDrawer)
        {
            p.OpenDrawer();
        }

        return p.ToArray();
    }

    private static string Money(decimal value) => value.ToString("#,##,##0.00", India);
}
