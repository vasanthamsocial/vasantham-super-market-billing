using System.Globalization;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Domain.Tax;

namespace SupermarketBilling.Infrastructure.Documents;

/// <summary>
/// Lays out an issued invoice on A4 pages: seller, buyer, lines, a tax summary by rate, totals, the amount in words
/// and the payments. Everything printed comes from the invoice as issued, so a reprint years later is identical.
/// </summary>
public static class InvoicePdf
{
    private const double Margin = 36;
    private const double Right = SimplePdf.PageWidth - Margin;
    private const double Body = 8.5;
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    // Columns: x is the left edge for text columns and the right edge for numbers.
    private static readonly (string Title, double X, TextAlign Align)[] Columns =
    [
        ("#", Margin + 2, TextAlign.Left),
        ("Item", Margin + 20, TextAlign.Left),
        ("HSN/SAC", Margin + 214, TextAlign.Left),
        ("Qty", Margin + 290, TextAlign.Right),
        ("Rate", Margin + 336, TextAlign.Right),
        ("Disc.", Margin + 374, TextAlign.Right),
        ("Taxable", Margin + 422, TextAlign.Right),
        ("GST %", Margin + 462, TextAlign.Right),
        ("Amount", Right - 2, TextAlign.Right),
    ];

    public static string Title(string kind) => kind switch
    {
        InvoiceKinds.TaxInvoice => "TAX INVOICE",
        InvoiceKinds.BillOfSupply => "BILL OF SUPPLY",
        _ => "INVOICE",
    };

    public static byte[] Render(InvoiceDto invoice, string timeZone)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var pdf = new SimplePdf($"{Title(invoice.Kind)} {invoice.Number}");
        var issuedLocal = TimeZoneInfo.ConvertTime(invoice.IssuedAtUtc, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        var collectsTax = invoice.TaxMode == TaxRegistrationModes.GstRegular;

        var y = Header(pdf, invoice, issuedLocal);
        y = TableHeader(pdf, y);
        foreach (var line in invoice.Lines)
        {
            if (y > SimplePdf.PageHeight - 120)
            {
                pdf.NewPage();
                y = TableHeader(pdf, Margin + 20);
            }

            var description = SimplePdf.Fit(line.Description + (line.Mrp is { } mrp ? $" (MRP {Money(mrp)})" : string.Empty), Body, 190);
            pdf.Text(Columns[0].X, y, line.LineNumber.ToString(CultureInfo.InvariantCulture), Body);
            pdf.Text(Columns[1].X, y, description, Body);
            pdf.Text(Columns[2].X, y, line.HsnSac, Body);
            pdf.Text(Columns[3].X, y, $"{Quantity(line.Quantity)} {line.UnitCode}", Body, align: TextAlign.Right);
            pdf.Text(Columns[4].X, y, Money(line.UnitPrice), Body, align: TextAlign.Right);
            pdf.Text(Columns[5].X, y, line.ItemDiscount + line.BillDiscount == 0 ? "-" : Money(line.ItemDiscount + line.BillDiscount), Body, align: TextAlign.Right);
            pdf.Text(Columns[6].X, y, Money(line.Taxable), Body, align: TextAlign.Right);
            pdf.Text(Columns[7].X, y, collectsTax && line.SupplyType == "TAXABLE" ? Percent(line.GstRatePercent + line.CessRatePercent) : "-", Body, align: TextAlign.Right);
            pdf.Text(Columns[8].X, y, Money(line.Total), Body, align: TextAlign.Right);
            y += 13;
        }

        pdf.Line(Margin, y, Right, y);
        y += 8;
        if (y > SimplePdf.PageHeight - 260)
        {
            pdf.NewPage();
            y = Margin + 20;
        }

        y = Totals(pdf, invoice, y, collectsTax);
        Footer(pdf, invoice);
        return pdf.ToBytes();
    }

    private static double Header(SimplePdf pdf, InvoiceDto invoice, DateTimeOffset issuedLocal)
    {
        pdf.Text(SimplePdf.PageWidth / 2, Margin, Title(invoice.Kind), 14, bold: true, align: TextAlign.Center);
        var y = Margin + 26;
        pdf.Text(Margin, y, invoice.SellerName, 11, bold: true);
        var left = y + 15;
        foreach (var line in SimplePdf.Wrap(invoice.SellerAddress, Body, 270))
        {
            pdf.Text(Margin, left, line, Body);
            left += 11;
        }

        if (invoice.SellerGstin is { } gstin)
        {
            pdf.Text(Margin, left, $"GSTIN: {gstin}", Body, bold: true);
            left += 11;
        }

        pdf.Text(Margin, left, $"State: {IndianStates.Describe(invoice.SellerStateCode)}", Body);
        left += 11;

        var labelX = Right - 200;
        var rightY = y;
        foreach (var (label, value) in new[]
                 {
                     ("Invoice No.", invoice.Number),
                     ("Date", issuedLocal.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture)),
                     ("Counter", invoice.CounterCode),
                     ("Cashier", invoice.Cashier),
                     ("Place of supply", IndianStates.Describe(invoice.PlaceOfSupplyStateCode)),
                 })
        {
            pdf.Text(labelX, rightY, label, Body);
            pdf.Text(labelX + 75, rightY, SimplePdf.Fit(value, Body, 125, bold: label == "Invoice No."), Body, bold: label == "Invoice No.");
            rightY += 11;
        }

        y = Math.Max(left, rightY) + 6;
        if (invoice.BuyerName is not null || invoice.BuyerGstin is not null || invoice.BuyerPhone is not null)
        {
            pdf.Line(Margin, y, Right, y);
            y += 6;
            pdf.Text(Margin, y, "Billed to", Body, bold: true);
            y += 11;
            foreach (var text in new[]
                     {
                         invoice.BuyerName, invoice.BuyerAddress, invoice.BuyerGstin is { } g ? $"GSTIN: {g}" : null,
                         invoice.BuyerPhone is { } p ? $"Phone: {p}" : null,
                     }.OfType<string>())
            {
                foreach (var line in SimplePdf.Wrap(text, Body, 400))
                {
                    pdf.Text(Margin, y, line, Body);
                    y += 11;
                }
            }

            y += 4;
        }

        return y;
    }

    private static double TableHeader(SimplePdf pdf, double y)
    {
        pdf.Line(Margin, y, Right, y);
        y += 5;
        foreach (var (title, x, align) in Columns)
        {
            pdf.Text(x, y, title, Body, bold: true, align: align);
        }

        y += 13;
        pdf.Line(Margin, y - 3, Right, y - 3);
        return y + 2;
    }

    private static double Totals(SimplePdf pdf, InvoiceDto invoice, double y, bool collectsTax)
    {
        var top = y;

        // Tax summary by rate (left), as GST invoices usually show.
        if (collectsTax)
        {
            pdf.Text(Margin, y, "Tax summary", Body, bold: true);
            y += 12;
            var cols = new[] { Margin + 40, Margin + 110, Margin + 165, Margin + 220, Margin + 275 };
            string[] heads = ["Rate", "Taxable", invoice.IsInterState ? "IGST" : "CGST", invoice.IsInterState ? "" : "SGST", "Cess"];
            for (var i = 0; i < heads.Length; i++)
            {
                pdf.Text(cols[i], y, heads[i], Body, bold: true, align: TextAlign.Right);
            }

            y += 11;
            foreach (var group in invoice.Lines.Where(l => l.SupplyType == "TAXABLE").GroupBy(l => l.GstRatePercent).OrderBy(g => g.Key))
            {
                pdf.Text(cols[0], y, Percent(group.Key), Body, align: TextAlign.Right);
                pdf.Text(cols[1], y, Money(group.Sum(l => l.Taxable)), Body, align: TextAlign.Right);
                pdf.Text(cols[2], y, Money(invoice.IsInterState ? group.Sum(l => l.Igst) : group.Sum(l => l.Cgst)), Body, align: TextAlign.Right);
                if (!invoice.IsInterState)
                {
                    pdf.Text(cols[3], y, Money(group.Sum(l => l.Sgst)), Body, align: TextAlign.Right);
                }

                pdf.Text(cols[4], y, Money(group.Sum(l => l.Cess)), Body, align: TextAlign.Right);
                y += 11;
            }
        }

        // Totals (right).
        var labelX = Right - 190;
        var right = top;
        var rows = new List<(string Label, decimal Amount, bool Bold)> { ("Gross amount", invoice.GrossTotal, false) };
        if (invoice.DiscountTotal > 0)
        {
            rows.Add(("Discount", -invoice.DiscountTotal, false));
        }

        if (collectsTax)
        {
            rows.Add(("Taxable value", invoice.TaxableTotal, false));
            if (invoice.IsInterState)
            {
                rows.Add(("IGST", invoice.IgstTotal, false));
            }
            else
            {
                rows.Add(("CGST", invoice.CgstTotal, false));
                rows.Add(("SGST", invoice.SgstTotal, false));
            }

            if (invoice.CessTotal > 0)
            {
                rows.Add(("Cess", invoice.CessTotal, false));
            }
        }

        if (invoice.RoundOff != 0)
        {
            rows.Add(("Round off", invoice.RoundOff, false));
        }

        rows.Add(("Total (Rs.)", invoice.GrandTotal, true));
        foreach (var (label, amount, bold) in rows)
        {
            if (bold)
            {
                right += 3;
                pdf.Line(labelX, right - 1, Right, right - 1);
                right += 3;
            }

            pdf.Text(labelX, right, label, bold ? 10 : Body, bold);
            pdf.Text(Right - 2, right, Money(amount), bold ? 10 : Body, bold, TextAlign.Right);
            right += bold ? 14 : 11;
        }

        y = Math.Max(y, right) + 6;
        foreach (var line in SimplePdf.Wrap(AmountInWords.Rupees(invoice.GrandTotal), Body, Right - Margin, bold: true))
        {
            pdf.Text(Margin, y, line, Body, bold: true);
            y += 11;
        }

        y += 4;
        var paid = string.Join(", ", invoice.Payments.Select(p => $"{p.Method} {Money(p.Amount)}{(p.Reference is { } r ? $" ({r})" : string.Empty)}"));
        foreach (var line in SimplePdf.Wrap($"Paid: {paid}" + (invoice.ChangeDue > 0 ? $". Change returned: {Money(invoice.ChangeDue)}" : string.Empty), Body, Right - Margin))
        {
            pdf.Text(Margin, y, line, Body);
            y += 11;
        }

        if (invoice.Declaration is { } declaration)
        {
            y += 4;
            pdf.Text(Margin, y, declaration, Body, bold: true);
            y += 11;
        }

        if (invoice.TaxMode == TaxRegistrationModes.NotGstRegistered)
        {
            y += 4;
            pdf.Text(Margin, y, "The seller is not registered under GST. No GST has been charged.", Body);
            y += 11;
        }

        return y;
    }

    private static void Footer(SimplePdf pdf, InvoiceDto invoice)
    {
        // Drawn last, so every page can say how many pages there are.
        for (var page = 0; page < pdf.PageCount; page++)
        {
            pdf.SelectPage(page);
            pdf.Text(Margin, SimplePdf.PageHeight - Margin, $"{invoice.Number} - computer-generated document, no signature required.", 7.5);
            pdf.Text(Right, SimplePdf.PageHeight - Margin, string.Create(CultureInfo.InvariantCulture, $"Page {page + 1} of {pdf.PageCount}"), 7.5, align: TextAlign.Right);
        }
    }

    private static string Money(decimal value) => value.ToString("#,##,##0.00", India);

    private static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Percent(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
