using System.Globalization;
using SupermarketBilling.Application.Contracts;

namespace SupermarketBilling.Infrastructure.Documents;

/// <summary>
/// The packing challan on A4 and its package labels. By design they show goods and people only: no prices, cost,
/// profit or the customer's balance (spec section 20).
/// </summary>
public static class ChallanPdf
{
    private const double Margin = 36;
    private const double Right = SimplePdf.PageWidth - Margin;
    private const double Body = 8.5;

    private static readonly Dictionary<string, string> Modes = new(StringComparer.Ordinal)
    {
        ["OWN_VEHICLE"] = "Own vehicle",
        ["LORRY"] = "Lorry service",
        ["LOCAL_DELIVERY"] = "Local delivery",
        ["PICKUP"] = "Customer pickup",
    };

    public static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static byte[] Render(ChallanDto challan, string storeName)
    {
        ArgumentNullException.ThrowIfNull(challan);
        var pdf = new SimplePdf($"PACKING CHALLAN {challan.Number}");
        pdf.Text(SimplePdf.PageWidth / 2, Margin, "PACKING / DELIVERY CHALLAN", 14, bold: true, align: TextAlign.Center);
        pdf.Text(SimplePdf.PageWidth / 2, Margin + 16, SimplePdf.Fit(storeName, Body, 300), Body, align: TextAlign.Center);
        var y = Margin + 34;
        var left = new List<(string, string)>
        {
            ("Party", challan.PartyName),
            ("Deliver to", challan.DeliveryAddress ?? "-"),
            ("Contact", challan.ContactPhone ?? "-"),
            ("Route", challan.Route ?? "-"),
            ("Delivery", Modes.GetValueOrDefault(challan.Mode, challan.Mode)),
        };
        if (challan.TransporterName is { } transporter)
        {
            left.Add(("Transporter", transporter + (challan.DestinationBranch is { } d ? $" to {d}" : string.Empty)));
        }

        var right = new List<(string, string)>
        {
            ("Challan No.", challan.Number),
            ("Invoice No.", challan.InvoiceNumber),
            ("Invoice date", challan.InvoiceDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)),
            ("Packages", challan.PackageCount.ToString(CultureInfo.InvariantCulture)),
            ("Status", challan.Progress.Replace('_', ' ')),
        };
        var leftY = y;
        foreach (var (label, value) in left)
        {
            pdf.Text(Margin, leftY, label, Body, bold: true);
            foreach (var line in SimplePdf.Wrap(value, Body, 250))
            {
                pdf.Text(Margin + 62, leftY, line, Body);
                leftY += 11;
            }
        }

        var rightY = y;
        foreach (var (label, value) in right)
        {
            pdf.Text(Right - 200, rightY, label, Body);
            pdf.Text(Right - 120, rightY, SimplePdf.Fit(value, Body, 120), Body, bold: label == "Challan No.");
            rightY += 11;
        }

        y = Math.Max(leftY, rightY) + 8;
        (string Title, double X, TextAlign Align)[] columns =
        [
            ("#", Margin + 2, TextAlign.Left), ("Item", Margin + 18, TextAlign.Left), ("Unit", Margin + 238, TextAlign.Left), ("Qty", Margin + 300, TextAlign.Right),
            ("Free", Margin + 335, TextAlign.Right), ("Batch", Margin + 345, TextAlign.Left), ("Picked", Margin + 460, TextAlign.Right), ("Packed", Right - 2, TextAlign.Right),
        ];
        pdf.Line(Margin, y, Right, y);
        y += 5;
        foreach (var (title, x, align) in columns)
        {
            pdf.Text(x, y, title, Body, bold: true, align: align);
        }

        y += 13;
        pdf.Line(Margin, y - 3, Right, y - 3);
        foreach (var line in challan.Lines)
        {
            if (y > SimplePdf.PageHeight - 120)
            {
                pdf.NewPage();
                y = Margin;
            }

            var item = line.VariantName is { } v ? $"{line.ItemName} - {v}" : line.ItemName;
            pdf.Text(Margin + 2, y, line.LineNumber.ToString(CultureInfo.InvariantCulture), Body);
            pdf.Text(Margin + 18, y, SimplePdf.Fit(item, Body, 215), Body);
            pdf.Text(Margin + 238, y, line.UnitCode, Body);
            pdf.Text(Margin + 300, y, Quantity(line.Quantity), Body, align: TextAlign.Right);
            pdf.Text(Margin + 335, y, Quantity(line.FreeQuantity), Body, align: TextAlign.Right);
            pdf.Text(Margin + 345, y, SimplePdf.Fit(line.Batches ?? "-", Body, 85), Body);
            pdf.Text(Margin + 460, y, line.Picked is { } p ? Quantity(p) : "____", Body, align: TextAlign.Right);
            pdf.Text(Right - 2, y, line.Packed > 0 ? Quantity(line.Packed) : "____", Body, align: TextAlign.Right);
            y += 12;
            if (line.ShortReason is { } reason)
            {
                pdf.Text(Margin + 18, y, SimplePdf.Fit($"Short: {reason}", Body - 1, 400), Body - 1);
                y += 11;
            }
        }

        pdf.Line(Margin, y, Right, y);
        y += 30;
        foreach (var (i, (label, name)) in new[] { ("Picked by", challan.Picker), ("Checked by", challan.Checker), ("Packed by", challan.Packer), ("Received by", (string?)null) }
                     .Select((x, i) => (i, x)))
        {
            var x = Margin + (i * (Right - Margin) / 4);
            pdf.Line(x, y, x + 110, y);
            pdf.Text(x, y + 4, label, Body);
            if (name is not null)
            {
                pdf.Text(x, y - 12, SimplePdf.Fit(name, Body, 110), Body);
            }
        }

        return pdf.ToBytes();
    }

    /// <summary>Eight labels to an A4 page, one per package: "Package 2 of 5", who it is for and how it goes.</summary>
    public static byte[] Labels(ChallanDto challan, string? lrNumber)
    {
        ArgumentNullException.ThrowIfNull(challan);
        var pdf = new SimplePdf($"LABELS {challan.Number}");
        const double width = (SimplePdf.PageWidth - (2 * Margin) - 12) / 2;
        const double height = (SimplePdf.PageHeight - (2 * Margin) - 36) / 4;
        var count = Math.Max(1, challan.PackageCount);
        for (var n = 0; n < count; n++)
        {
            if (n > 0 && n % 8 == 0)
            {
                pdf.NewPage();
            }

            var x = Margin + (n % 2 * (width + 12));
            var top = Margin + (n % 8 / 2 * (height + 12));
            pdf.Box(x, top, width, height, 1);
            var y = top + 14;
            pdf.Text(x + 10, y, $"Package {n + 1} of {count}", 14, bold: true);
            y += 22;
            pdf.Text(x + 10, y, SimplePdf.Fit(challan.PartyName, 11, width - 20, bold: true), 11, bold: true);
            y += 15;
            foreach (var line in SimplePdf.Wrap(challan.DeliveryAddress ?? string.Empty, Body, width - 20).Take(3))
            {
                pdf.Text(x + 10, y, line, Body);
                y += 11;
            }

            y += 4;
            if (challan.TransporterName is { } transporter)
            {
                pdf.Text(x + 10, y, SimplePdf.Fit($"By {transporter}{(challan.DestinationBranch is { } d ? $" to {d}" : string.Empty)}", Body, width - 20, bold: true), Body, bold: true);
                y += 11;
            }

            if (lrNumber is not null)
            {
                pdf.Text(x + 10, y, $"LR/GR {lrNumber}", Body, bold: true);
                y += 11;
            }

            pdf.Text(x + 10, top + height - 14, SimplePdf.Fit($"Challan {challan.Number}  Invoice {challan.InvoiceNumber}", Body, width - 20), Body);
        }

        return pdf.ToBytes();
    }
}
