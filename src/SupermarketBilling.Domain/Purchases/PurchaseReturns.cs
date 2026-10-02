using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Purchases;

/// <summary>Taxable value and taxes of (part of) a receipt line.</summary>
public sealed record LineAmounts(decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst, decimal Cess)
{
    public static readonly LineAmounts Zero = new(0, 0, 0, 0, 0);

    public decimal Total => Taxable + Cgst + Sgst + Igst + Cess;

    public LineAmounts Plus(LineAmounts other) => new(Taxable + other.Taxable, Cgst + other.Cgst, Sgst + other.Sgst, Igst + other.Igst, Cess + other.Cess);

    public LineAmounts Minus(LineAmounts other) => new(Taxable - other.Taxable, Cgst - other.Cgst, Sgst - other.Sgst, Igst - other.Igst, Cess - other.Cess);
}

/// <summary>
/// What a supplier credits for goods returned against a receipt line: the line's value and taxes in proportion to the
/// quantity returned out of everything received on it (free goods included, as they were costed). The return that
/// takes the last of a line takes exactly what is left, so returns never exceed the receipt.
/// </summary>
public static class DebitNoteCalculator
{
    public static LineAmounts Line(LineAmounts received, decimal receivedQuantity, LineAmounts alreadyReturned, decimal alreadyReturnedQuantity, decimal quantity)
    {
        ArgumentNullException.ThrowIfNull(received);
        ArgumentNullException.ThrowIfNull(alreadyReturned);
        var left = receivedQuantity - alreadyReturnedQuantity;
        if (quantity <= 0 || StockMath.Quantity(quantity) != quantity)
        {
            throw new DomainException("purchase_return.quantity_invalid", "A returned quantity is above zero, with at most 3 decimals.");
        }

        if (quantity > left)
        {
            throw new DomainException("purchase_return.quantity_too_large", $"Only {left:0.###} of this line can still be returned.");
        }

        var remaining = received.Minus(alreadyReturned);
        if (quantity == left)
        {
            return remaining;
        }

        decimal Share(decimal amount, decimal max) => Math.Min(decimal.Round(amount * quantity / receivedQuantity, 2, MidpointRounding.AwayFromZero), max);
        return new LineAmounts(
            Share(received.Taxable, remaining.Taxable), Share(received.Cgst, remaining.Cgst), Share(received.Sgst, remaining.Sgst),
            Share(received.Igst, remaining.Igst), Share(received.Cess, remaining.Cess));
    }
}

/// <summary>
/// Goods sent back to a supplier against a posted receipt (spec section 22): a debit note numbered per store. Stock
/// goes out, the supplier owes the value and taxes back (deducted from what is owed to them), and nothing changes later.
/// </summary>
public sealed class PurchaseReturn : ITenantOwned
{
    private PurchaseReturn()
    {
        Number = Reason = IdempotencyKey = RequestHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid SupplierId { get; private set; }

    public Guid GrnId { get; private set; }

    public string Number { get; private set; }

    public long SequenceNumber { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public string Reason { get; private set; }

    public bool IsInterState { get; private set; }

    public bool TaxRecoverable { get; private set; }

    public decimal Taxable { get; private set; }

    public decimal Cgst { get; private set; }

    public decimal Sgst { get; private set; }

    public decimal Igst { get; private set; }

    public decimal Cess { get; private set; }

    /// <summary>The receipt's round-off, given back by the return that completes the receipt.</summary>
    public decimal RoundOff { get; private set; }

    /// <summary>What the supplier owes back: lines and taxes plus any round-off.</summary>
    public decimal Total { get; private set; }

    /// <summary>The stock value that went out (at cost).</summary>
    public decimal StockValue { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string RequestHash { get; private set; }

    public static PurchaseReturn Create(
        Guid id, Guid businessId, Grn grn, string number, long sequence, DateOnly businessDate, string? reason, LineAmounts amounts, decimal roundOff,
        decimal stockValue, Guid createdBy, string idempotencyKey, string requestHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(grn);
        ArgumentNullException.ThrowIfNull(amounts);
        var cleanReason = (reason ?? string.Empty).Trim();
        if (cleanReason.Length is < 3 or > 200)
        {
            throw new DomainException("purchase_return.reason_required", "Say why the goods go back (3 to 200 characters).");
        }

        if (grn.Status != GrnStatus.Posted)
        {
            throw new DomainException("purchase_return.grn_not_posted", $"Goods receipt {grn.Number} is not posted, so nothing can be returned against it.");
        }

        return new PurchaseReturn
        {
            Id = id,
            BusinessId = businessId,
            StoreId = grn.StoreId,
            SupplierId = grn.SupplierId,
            GrnId = grn.Id,
            Number = number,
            SequenceNumber = sequence,
            BusinessDate = businessDate,
            Reason = cleanReason,
            IsInterState = grn.IsInterState,
            TaxRecoverable = grn.TaxRecoverable,
            Taxable = amounts.Taxable,
            Cgst = amounts.Cgst,
            Sgst = amounts.Sgst,
            Igst = amounts.Igst,
            Cess = amounts.Cess,
            RoundOff = roundOff,
            Total = amounts.Total + roundOff,
            StockValue = stockValue,
            CreatedByUserId = createdBy,
            CreatedAtUtc = now,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
        };
    }
}

public sealed class PurchaseReturnLine : ITenantOwned
{
    private PurchaseReturnLine()
    {
        Description = UnitCode = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid PurchaseReturnId { get; private set; }

    public Guid GrnLineId { get; private set; }

    public int LineNumber { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid VariantUnitId { get; private set; }

    public string Description { get; private set; }

    public string UnitCode { get; private set; }

    /// <summary>In the receipt line's pack.</summary>
    public decimal Quantity { get; private set; }

    public decimal BaseQuantity { get; private set; }

    public decimal Taxable { get; private set; }

    public decimal Cgst { get; private set; }

    public decimal Sgst { get; private set; }

    public decimal Igst { get; private set; }

    public decimal Cess { get; private set; }

    public decimal Total { get; private set; }

    public decimal StockValue { get; private set; }

    public LineAmounts Amounts => new(Taxable, Cgst, Sgst, Igst, Cess);

    public static PurchaseReturnLine Create(
        Guid businessId, Guid returnId, int lineNumber, GrnLine grnLine, decimal quantity, LineAmounts amounts, decimal stockValue, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(grnLine);
        ArgumentNullException.ThrowIfNull(amounts);
        return new PurchaseReturnLine
        {
            Id = SequentialGuid.Next(now),
            BusinessId = businessId,
            PurchaseReturnId = returnId,
            GrnLineId = grnLine.Id,
            LineNumber = lineNumber,
            VariantId = grnLine.VariantId,
            VariantUnitId = grnLine.VariantUnitId,
            Description = grnLine.Description,
            UnitCode = grnLine.UnitCode,
            Quantity = quantity,
            BaseQuantity = StockMath.Quantity(quantity * grnLine.FactorToBase),
            Taxable = amounts.Taxable,
            Cgst = amounts.Cgst,
            Sgst = amounts.Sgst,
            Igst = amounts.Igst,
            Cess = amounts.Cess,
            Total = amounts.Total,
            StockValue = stockValue,
        };
    }
}
