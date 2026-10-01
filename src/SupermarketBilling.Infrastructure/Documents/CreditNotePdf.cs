using System.Globalization;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Domain.Tax;

namespace SupermarketBilling.Infrastructure.Documents;

/// <summary>A credit note on A4: the original invoice it reverses, the goods returned, taxes reversed and the refund.</summary>
public static class CreditNotePdf
{
    private const double Margin = 36;
    private const double Right = SimplePdf.PageWidth - Margin;
    private const double Body = 8.5;
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    public static byte[] Render(CreditNoteDto note, string timeZone)
    {
        ArgumentNullException.ThrowIfNull(note);
        var pdf = new SimplePdf($"CREDIT NOTE {note.Number}");
        var issued = TimeZoneInfo.ConvertTime(note.IssuedAtUtc, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        var tax = note.TaxMode == TaxRegistrationModes.GstRegular;

        pdf.Text(SimplePdf.PageWidth / 2, Margin, "CREDIT NOTE", 14, bold: true, align: TextAlign.Center);
        var y = Margin + 26;
        pdf.Text(Margin, y, note.SellerName, 11, bold: true);
        var left = y + 15;
        foreach (var line in SimplePdf.Wrap(note.SellerAddress, Body, 270))
        {
            pdf.Text(Margin, left, line, Body);
            left += 11;
        }

        if (note.SellerGstin is { } gstin)
        {
            pdf.Text(Margin, left, $"GSTIN: {gstin}", Body, bold: true);
            left += 11;
        }

        pdf.Text(Margin, left, $"State: {IndianStates.Describe(note.SellerStateCode)}", Body);
        left += 11;

        var rightY = y;
        foreach (var (label, value) in new[]
                 {
                     ("Credit note No.", note.Number),
                     ("Date", issued.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture)),
                     ("Against invoice", note.OriginalInvoiceNumber),
                     ("Invoice date", note.OriginalInvoiceDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)),
                     ("Place of supply", IndianStates.Describe(note.PlaceOfSupplyStateCode)),
                 })
        {
            pdf.Text(Right - 200, rightY, label, Body);
            pdf.Text(Right - 120, rightY, SimplePdf.Fit(value, Body, 120), Body, bold: label == "Credit note No.");
            rightY += 11;
        }

        y = Math.Max(left, rightY) + 6;
        if (note.BuyerName is not null || note.BuyerGstin is not null)
        {
            pdf.Text(Margin, y, $"Customer: {note.BuyerName}{(note.BuyerGstin is { } g ? $"  GSTIN: {g}" : string.Empty)}", Body);
            y += 13;
        }

        pdf.Text(Margin, y, SimplePdf.Fit($"Reason: {note.Reason}", Body, Right - Margin), Body);
        y += 14;

        (string Title, double X, TextAlign Align)[] columns =
        [
            ("#", Margin + 2, TextAlign.Left), ("Item returned", Margin + 20, TextAlign.Left), ("HSN/SAC", Margin + 250, TextAlign.Left),
            ("Qty", Margin + 330, TextAlign.Right), ("Taxable", Margin + 400, TextAlign.Right), ("GST %", Margin + 440, TextAlign.Right),
            ("Amount", Right - 2, TextAlign.Right),
        ];
        pdf.Line(Margin, y, Right, y);
        y += 5;
        foreach (var (title, x, align) in columns)
        {
            pdf.Text(x, y, title, Body, bold: true, align: align);
        }

        y += 13;
        pdf.Line(Margin, y - 3, Right, y - 3);
        foreach (var line in note.Lines)
        {
            if (y > SimplePdf.PageHeight - 200)
            {
                pdf.NewPage();
                y = Margin + 20;
            }

            pdf.Text(columns[0].X, y, line.LineNumber.ToString(CultureInfo.InvariantCulture), Body);
            pdf.Text(columns[1].X, y, SimplePdf.Fit(line.Description + (line.Restocked ? string.Empty : " (not restocked)"), Body, 225), Body);
            pdf.Text(columns[2].X, y, line.HsnSac, Body);
            pdf.Text(columns[3].X, y, $"{line.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} {line.UnitCode}", Body, align: TextAlign.Right);
            pdf.Text(columns[4].X, y, Money(line.Taxable), Body, align: TextAlign.Right);
            pdf.Text(columns[5].X, y, tax && line.Cgst + line.Igst > 0 ? line.GstRatePercent.ToString("0.##", CultureInfo.InvariantCulture) + "%" : "-", Body, align: TextAlign.Right);
            pdf.Text(columns[6].X, y, Money(line.Total), Body, align: TextAlign.Right);
            y += 13;
        }

        pdf.Line(Margin, y, Right, y);
        y += 8;
        var rows = new List<(string, decimal)>();
        if (tax)
        {
            rows.Add(("Taxable value", note.TaxableTotal));
            if (note.IsInterState)
            {
                rows.Add(("IGST reversed", note.IgstTotal));
            }
            else
            {
                rows.Add(("CGST reversed", note.CgstTotal));
                rows.Add(("SGST reversed", note.SgstTotal));
            }

            if (note.CessTotal > 0)
            {
                rows.Add(("Cess reversed", note.CessTotal));
            }
        }

        if (note.RoundOff != 0)
        {
            rows.Add(("Round off", note.RoundOff));
        }

        foreach (var (label, amount) in rows)
        {
            pdf.Text(Right - 190, y, label, Body);
            pdf.Text(Right - 2, y, Money(amount), Body, align: TextAlign.Right);
            y += 11;
        }

        y += 3;
        pdf.Line(Right - 190, y - 1, Right, y - 1);
        y += 3;
        pdf.Text(Right - 190, y, "Credit (Rs.)", 10, bold: true);
        pdf.Text(Right - 2, y, Money(note.GrandTotal), 10, bold: true, align: TextAlign.Right);
        y += 18;
        pdf.Text(Margin, y, AmountInWords.Rupees(note.GrandTotal), Body, bold: true);
        y += 13;
        var refunds = string.Join(", ", note.Refunds.Select(r => $"{(r.Method == RefundMethods.StoreCredit ? "Store credit" : r.Method)} {Money(r.Amount)}{(r.Reference is { } x ? $" ({x})" : string.Empty)}"));
        pdf.Text(Margin, y, SimplePdf.Fit($"Settled: {refunds}", Body, Right - Margin), Body);
        y += 11;
        if (note.StoreCredit > 0)
        {
            pdf.Text(Margin, y, $"Store credit left on this note: Rs. {Money(note.StoreCreditLeft)} (quote {note.Number} at the counter)", Body);
        }

        for (var page = 0; page < pdf.PageCount; page++)
        {
            pdf.SelectPage(page);
            pdf.Text(Margin, SimplePdf.PageHeight - Margin, $"{note.Number} - computer-generated document, no signature required.", 7.5);
            pdf.Text(Right, SimplePdf.PageHeight - Margin, string.Create(CultureInfo.InvariantCulture, $"Page {page + 1} of {pdf.PageCount}"), 7.5, align: TextAlign.Right);
        }

        return pdf.ToBytes();
    }

    private static string Money(decimal value) => value.ToString("#,##,##0.00", India);
}
