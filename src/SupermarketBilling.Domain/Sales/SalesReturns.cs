using System.Globalization;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Sales;

/// <summary>How a return is settled with the customer.</summary>
public static class RefundMethods
{
    public const string Cash = "CASH";
    public const string Card = "CARD";
    public const string Upi = "UPI";
    public const string Wallet = "WALLET";

    /// <summary>Kept as store credit on the credit note, to pay for a later bill (an exchange).</summary>
    public const string StoreCredit = "STORE_CREDIT";

    public static readonly IReadOnlyList<string> All = [Cash, Card, Upi, Wallet, StoreCredit];
}

/// <summary>An original invoice line, and what has already been returned from it.</summary>
public sealed record ReturnableLine(decimal Quantity, BillLineResult Amounts, BillLineResult AlreadyReturned, decimal AlreadyReturnedQuantity);

/// <summary>
/// The amounts of a returned quantity, taken from the original line so that the credit note reverses exactly what
/// was charged: the customer gets back their share of what they paid, with the taxes in the same proportion
/// (CGST and SGST stay equal). Returning the last of a line takes exactly what is left, so any number of partial
/// returns add up to the original line to the paisa.
/// </summary>
public static class ReturnCalculator
{
    public static BillLineResult Line(ReturnableLine original, decimal quantity)
    {
        ArgumentNullException.ThrowIfNull(original);
        var left = original.Quantity - original.AlreadyReturnedQuantity;
        if (quantity <= 0)
        {
            throw new DomainException("return.quantity_invalid", "The quantity returned must be more than zero.");
        }

        if (quantity > left)
        {
            throw new DomainException("return.quantity_too_large",
                string.Create(CultureInfo.InvariantCulture, $"Only {left:0.###} of this item can still be returned."));
        }

        var a = original.Amounts;
        var r = original.AlreadyReturned;
        if (quantity == left)
        {
            return new BillLineResult(a.Gross - r.Gross, a.ItemDiscount - r.ItemDiscount, a.BillDiscount - r.BillDiscount, a.Taxable - r.Taxable,
                a.Cgst - r.Cgst, a.Sgst - r.Sgst, a.Igst - r.Igst, a.Cess - r.Cess, a.Total - r.Total);
        }

        var ratio = quantity / original.Quantity;
        decimal Part(decimal value) => InvoiceCalculator.Money(value * ratio);
        var total = Part(a.Total);
        var cgst = Part(a.Cgst);
        var igst = Part(a.Igst);
        var cess = Part(a.Cess);
        return new BillLineResult(Part(a.Gross), Part(a.ItemDiscount), Part(a.BillDiscount), total - (2 * cgst) - igst - cess, cgst, cgst, igst, cess, total);
    }

    /// <summary>Validates the refunds against the credit note total; they must add up exactly. Returns the store credit kept.</summary>
    public static decimal StoreCredit(decimal grandTotal, IReadOnlyList<PaymentInput> refunds)
    {
        ArgumentNullException.ThrowIfNull(refunds);
        if (refunds.Count == 0)
        {
            throw new DomainException("refund.required", "Record how the customer is refunded.");
        }

        foreach (var refund in refunds)
        {
            if (!RefundMethods.All.Contains(refund.Method))
            {
                throw new DomainException("refund.method_invalid", $"Unknown refund method '{refund.Method}'.");
            }

            if (refund.Amount <= 0 || refund.Amount != InvoiceCalculator.Money(refund.Amount))
            {
                throw new DomainException("refund.amount_invalid", "Each refund must be a positive amount in rupees and paise.");
            }
        }

        var sum = refunds.Sum(r => r.Amount);
        return sum == grandTotal
            ? refunds.Where(r => r.Method == RefundMethods.StoreCredit).Sum(r => r.Amount)
            : throw new DomainException("refund.mismatch", $"The refunds (Rs. {sum:0.00}) must add up to the credit note (Rs. {grandTotal:0.00}).");
    }

    /// <summary>The credit note number: the counter prefix, "/CN" and six or more digits, within GST's 16 characters.</summary>
    public static string CreditNoteNumber(string prefix, long sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}/CN{sequence:000000}");

    public static string CreditNoteSeries(string prefix) => "CN-" + prefix;
}

/// <summary>
/// A credit note: goods returned against an issued invoice, with the money refunded or kept as store credit. Like an
/// invoice it is immutable; it keeps the original invoice's number and date as GST requires.
/// </summary>
public sealed class SalesReturn : ITenantOwned
{
    private readonly List<SalesReturnLine> _lines = [];
    private readonly List<SalesReturnRefund> _refunds = [];

    private SalesReturn()
    {
        Number = NumberPrefix = OriginalInvoiceNumber = TaxMode = PlaceOfSupplyStateCode = Reason = IdempotencyKey = RequestHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid CounterId { get; private set; }

    public Guid DeviceId { get; private set; }

    /// <summary>The cashier's shift the document was issued in (empty only for documents from before shifts existed).</summary>
    public Guid? ShiftId { get; private set; }

    public Guid OriginalInvoiceId { get; private set; }

    public string OriginalInvoiceNumber { get; private set; }

    public DateOnly OriginalInvoiceDate { get; private set; }

    public string Number { get; private set; }

    public string NumberPrefix { get; private set; }

    public long SequenceNumber { get; private set; }

    public string TaxMode { get; private set; }

    public bool IsInterState { get; private set; }

    public string PlaceOfSupplyStateCode { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    public Guid CashierUserId { get; private set; }

    public string Reason { get; private set; }

    public Guid? ApprovalId { get; private set; }

    public decimal TaxableTotal { get; private set; }

    public decimal CgstTotal { get; private set; }

    public decimal SgstTotal { get; private set; }

    public decimal IgstTotal { get; private set; }

    public decimal CessTotal { get; private set; }

    public decimal RoundOff { get; private set; }

    public decimal GrandTotal { get; private set; }

    /// <summary>Kept as store credit (not paid out); redeemable on later bills.</summary>
    public decimal StoreCredit { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string RequestHash { get; private set; }

    public IReadOnlyList<SalesReturnLine> Lines => _lines;

    public IReadOnlyList<SalesReturnRefund> Refunds => _refunds;

    public sealed record Original(Guid InvoiceId, string Number, DateOnly Date, string TaxMode, bool IsInterState, string PlaceOfSupply);

    public static SalesReturn Issue(
        Guid id, Guid businessId, Guid storeId, Guid counterId, Guid deviceId, Guid shiftId, string numberPrefix, long sequence, Original original, DateOnly businessDate,
        Guid cashier, string reason, Guid? approvalId, IReadOnlyList<BillLineResult> lines, IReadOnlyList<PaymentInput> refunds, string idempotencyKey,
        string requestHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            throw new DomainException("return.lines_required", "Choose at least one item to return.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            throw new DomainException("idempotency.key_required", "Each return needs an idempotency key (max 100 characters).");
        }

        var beforeRounding = lines.Sum(l => l.Total);
        var grandTotal = decimal.Round(beforeRounding, 0, MidpointRounding.AwayFromZero);
        var credit = ReturnCalculator.StoreCredit(grandTotal, refunds);
        var result = new SalesReturn
        {
            Id = id,
            BusinessId = businessId,
            StoreId = storeId,
            CounterId = counterId,
            DeviceId = deviceId,
            ShiftId = shiftId,
            OriginalInvoiceId = original.InvoiceId,
            OriginalInvoiceNumber = original.Number,
            OriginalInvoiceDate = original.Date,
            Number = ReturnCalculator.CreditNoteNumber(numberPrefix, sequence),
            NumberPrefix = numberPrefix,
            SequenceNumber = sequence,
            TaxMode = original.TaxMode,
            IsInterState = original.IsInterState,
            PlaceOfSupplyStateCode = original.PlaceOfSupply,
            BusinessDate = businessDate,
            IssuedAtUtc = now,
            CashierUserId = cashier,
            Reason = Business.Required(reason, "return.reason_required", "Give the reason for the return (max 200 characters).", 200),
            ApprovalId = approvalId,
            TaxableTotal = lines.Sum(l => l.Taxable),
            CgstTotal = lines.Sum(l => l.Cgst),
            SgstTotal = lines.Sum(l => l.Sgst),
            IgstTotal = lines.Sum(l => l.Igst),
            CessTotal = lines.Sum(l => l.Cess),
            RoundOff = grandTotal - beforeRounding,
            GrandTotal = grandTotal,
            StoreCredit = credit,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
        };

        var order = 0;
        foreach (var refund in refunds)
        {
            result._refunds.Add(new SalesReturnRefund(SequentialGuid.Next(now), businessId, id, ++order, refund.Method, refund.Amount, refund.Reference?.Trim() is { Length: > 0 } r ? r : null));
        }

        return result;
    }

    public void AddLine(SalesReturnLine line) => _lines.Add(line);
}

public sealed class SalesReturnLine : ITenantOwned
{
    private SalesReturnLine()
    {
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ReturnId { get; private set; }

    public int LineNumber { get; private set; }

    public Guid OriginalLineId { get; private set; }

    public Guid VariantId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal BaseQuantity { get; private set; }

    /// <summary>False when the goods are damaged and do not go back on the shelf.</summary>
    public bool Restocked { get; private set; }

    public decimal Gross { get; private set; }

    public decimal ItemDiscount { get; private set; }

    public decimal BillDiscount { get; private set; }

    public decimal Taxable { get; private set; }

    public decimal Cgst { get; private set; }

    public decimal Sgst { get; private set; }

    public decimal Igst { get; private set; }

    public decimal Cess { get; private set; }

    public decimal Total { get; private set; }

    /// <summary>Cost of the stock that came back (zero when not restocked).</summary>
    public decimal CostReturned { get; private set; }

    public static SalesReturnLine Create(
        Guid businessId, Guid returnId, int lineNumber, Guid originalLineId, Guid variantId, decimal quantity, decimal baseQuantity, bool restocked,
        BillLineResult amounts, decimal costReturned, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        return new SalesReturnLine
        {
            Id = SequentialGuid.Next(now),
            BusinessId = businessId,
            ReturnId = returnId,
            LineNumber = lineNumber,
            OriginalLineId = originalLineId,
            VariantId = variantId,
            Quantity = quantity,
            BaseQuantity = baseQuantity,
            Restocked = restocked,
            Gross = amounts.Gross,
            ItemDiscount = amounts.ItemDiscount,
            BillDiscount = amounts.BillDiscount,
            Taxable = amounts.Taxable,
            Cgst = amounts.Cgst,
            Sgst = amounts.Sgst,
            Igst = amounts.Igst,
            Cess = amounts.Cess,
            Total = amounts.Total,
            CostReturned = costReturned,
        };
    }
}

public sealed class SalesReturnRefund : ITenantOwned
{
    private SalesReturnRefund()
    {
        Method = string.Empty;
    }

    internal SalesReturnRefund(Guid id, Guid businessId, Guid returnId, int order, string method, decimal amount, string? reference)
    {
        Id = id;
        BusinessId = businessId;
        ReturnId = returnId;
        RefundOrder = order;
        Method = method;
        Amount = amount;
        Reference = reference;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ReturnId { get; private set; }

    public int RefundOrder { get; private set; }

    public string Method { get; private set; }

    public decimal Amount { get; private set; }

    public string? Reference { get; private set; }
}

/// <summary>Store credit from a credit note used to pay (part of) a later invoice.</summary>
public sealed class CreditNoteRedemption : ITenantOwned
{
    private CreditNoteRedemption()
    {
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ReturnId { get; private set; }

    public Guid InvoiceId { get; private set; }

    public decimal Amount { get; private set; }

    public DateTimeOffset RedeemedAtUtc { get; private set; }

    /// <summary>Uses <paramref name="amount"/> of the credit note's store credit, of which <paramref name="alreadyRedeemed"/> is used.</summary>
    public static CreditNoteRedemption Redeem(SalesReturn creditNote, decimal alreadyRedeemed, Guid invoiceId, decimal amount, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(creditNote);
        var left = creditNote.StoreCredit - alreadyRedeemed;
        if (amount > left)
        {
            throw new DomainException("credit_note.insufficient",
                string.Create(CultureInfo.InvariantCulture, $"Credit note {creditNote.Number} has Rs. {left:0.00} of store credit left."));
        }

        return new CreditNoteRedemption
        {
            Id = SequentialGuid.Next(now),
            BusinessId = creditNote.BusinessId,
            ReturnId = creditNote.Id,
            InvoiceId = invoiceId,
            Amount = amount,
            RedeemedAtUtc = now,
        };
    }
}
