using System.Globalization;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Domain.Tax;

namespace SupermarketBilling.Infrastructure.Documents;

/// <summary>A debit note on A4, given to the supplier with the goods sent back: the receipt and invoice it is against, the goods, taxes and the amount.</summary>
public static class DebitNotePdf
{
    private const double Margin = 36;
    private const double Right = SimplePdf.PageWidth - Margin;
    private const double Body = 8.5;
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    public sealed record Issuer(string Name, string? Gstin, string Address, string StateCode);

    public static byte[] Render(PurchaseReturnDto note, Issuer issuer)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(issuer);
        var pdf = new SimplePdf($"DEBIT NOTE {note.Number}");
        var taxed = note.Cgst + note.Sgst + note.Igst + note.Cess > 0;

        pdf.Text(SimplePdf.PageWidth / 2, Margin, "DEBIT NOTE", 14, bold: true, align: TextAlign.Center);
        var y = Margin + 26;
        pdf.Text(Margin, y, issuer.Name, 11, bold: true);
        var left = y + 15;
        foreach (var line in SimplePdf.Wrap(issuer.Address, Body, 270))
        {
            pdf.Text(Margin, left, line, Body);
            left += 11;
        }

        if (issuer.Gstin is { } gstin)
        {
            pdf.Text(Margin, left, $"GSTIN: {gstin}", Body, bold: true);
            left += 11;
        }

        pdf.Text(Margin, left, $"State: {IndianStates.Describe(issuer.StateCode)}", Body);
        left += 11;

        var rightY = y;
        foreach (var (label, value) in new[]
                 {
                     ("Debit note No.", note.Number),
                     ("Date", note.BusinessDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)),
                     ("Supplier invoice", note.SupplierInvoiceNumber),
                     ("Invoice date", note.SupplierInvoiceDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)),
                     ("Our receipt", note.GrnNumber),
                 })
        {
            pdf.Text(Right - 200, rightY, label, Body);
            pdf.Text(Right - 120, rightY, SimplePdf.Fit(value, Body, 120), Body, bold: label == "Debit note No.");
            rightY += 11;
        }

        y = Math.Max(left, rightY) + 6;
        pdf.Text(Margin, y, SimplePdf.Fit($"To: {note.SupplierName}{(note.SupplierGstin is { } g ? $"  GSTIN: {g}" : string.Empty)}  State: {IndianStates.Describe(note.SupplierStateCode)}",
            Body, Right - Margin), Body, bold: true);
        y += 13;
        pdf.Text(Margin, y, SimplePdf.Fit($"Reason: {note.Reason}", Body, Right - Margin), Body);
        y += 14;

        (string Title, double X, TextAlign Align)[] columns =
        [
            ("#", Margin + 2, TextAlign.Left), ("Goods returned", Margin + 20, TextAlign.Left), ("Qty", Margin + 300, TextAlign.Right),
            ("Taxable", Margin + 380, TextAlign.Right), ("Tax", Margin + 450, TextAlign.Right), ("Amount", Right - 2, TextAlign.Right),
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
            pdf.Text(columns[1].X, y, SimplePdf.Fit(line.Description, Body, 250), Body);
            pdf.Text(columns[2].X, y, $"{line.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} {line.UnitCode}", Body, align: TextAlign.Right);
            pdf.Text(columns[3].X, y, Money(line.Taxable), Body, align: TextAlign.Right);
            pdf.Text(columns[4].X, y, Money(line.Cgst + line.Sgst + line.Igst + line.Cess), Body, align: TextAlign.Right);
            pdf.Text(columns[5].X, y, Money(line.Total), Body, align: TextAlign.Right);
            y += 13;
        }

        pdf.Line(Margin, y, Right, y);
        y += 8;
        var rows = new List<(string, decimal)> { ("Taxable value", note.Taxable) };
        if (taxed)
        {
            if (note.IsInterState)
            {
                rows.Add(("IGST", note.Igst));
            }
            else
            {
                rows.Add(("CGST", note.Cgst));
                rows.Add(("SGST", note.Sgst));
            }

            if (note.Cess > 0)
            {
                rows.Add(("Cess", note.Cess));
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
        pdf.Text(Right - 190, y, "Debit (Rs.)", 10, bold: true);
        pdf.Text(Right - 2, y, Money(note.Total), 10, bold: true, align: TextAlign.Right);
        y += 18;
        pdf.Text(Margin, y, AmountInWords.Rupees(note.Total), Body, bold: true);

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
