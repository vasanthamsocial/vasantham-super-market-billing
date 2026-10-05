using System.Text;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Reporting;

namespace SupermarketBilling.UnitTests.Reporting;

public sealed class ReportTableTests
{
    private static readonly ReportQuery Query = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);

    private static ReportDto Report()
    {
        var table = new ReportTable().Column("name", "Name", ColumnKinds.Text).Column("bills", "Bills", ColumnKinds.Count).Column("qty", "Qty", ColumnKinds.Quantity)
            .Column("sales", "Sales", ColumnKinds.Money).Column("profit", "Profit", ColumnKinds.Money).Ratio("margin", "Margin", "profit", "sales")
            .Average("average", "Average bill", "sales", "bills").Column("day", "Day", ColumnKinds.Date);
        table.Row(("name", "Rice"), ("bills", 2), ("qty", 2.5m), ("sales", 300m), ("profit", 30m), ("day", new DateOnly(2026, 10, 1)));
        table.Row(("name", "=SUM(A1)"), ("bills", 1), ("qty", 0.125m), ("sales", 100m), ("profit", 40m), ("day", new DateOnly(2026, 10, 2)));
        table.Row(("name", "Nothing sold"), ("bills", 0), ("qty", 0m), ("sales", 0m), ("profit", 0m), ("day", null));
        return table.Build("test", "Test", Query, Now);
    }

    [Fact]
    public void Totals_add_up_the_rows_and_ratios_come_from_the_totals_not_from_averaging_rows()
    {
        var report = Report();
        Assert.Equal((10m, 150m), (report.Rows[0]["margin"], report.Rows[0]["average"]));
        Assert.Null(report.Rows[2]["margin"]); // nothing sold: no margin rather than a division by zero
        var totals = report.Totals!;
        Assert.Equal(("Total", 3L, 2.625m, 400m, 70m), (totals["name"], totals["bills"], totals["qty"], totals["sales"], totals["profit"]));
        Assert.Equal((17.5m, 133.33m), (totals["margin"], totals["average"])); // 70/400 and 400/3, not the means of the rows
        Assert.False(totals.ContainsKey("day"));
    }

    [Fact]
    public void Csv_quotes_every_cell_formats_numbers_plainly_and_defuses_formulas()
    {
        var bytes = ReportCsv.Write(Report());
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("\"Name\",\"Bills\",\"Qty\",\"Sales\",\"Profit\",\"Margin\",\"Average bill\",\"Day\"", lines[0]);
        Assert.Equal("\"Rice\",\"2\",\"2.5\",\"300.00\",\"30.00\",\"10.00\",\"150.00\",\"2026-10-01\"", lines[1]);
        Assert.StartsWith("\"'=SUM(A1)\",\"1\",\"0.125\"", lines[2], StringComparison.Ordinal);
        Assert.Equal("\"Nothing sold\",\"0\",\"0\",\"0.00\",\"0.00\",\"\",\"\",\"\"", lines[3]);
        Assert.Equal("\"Total\",\"3\",\"2.625\",\"400.00\",\"70.00\",\"17.50\",\"133.33\",\"\"", lines[4]);
        Assert.Equal("test_2026-10-01_2026-10-31.csv", ReportCsv.FileName(Report()));
    }
}
