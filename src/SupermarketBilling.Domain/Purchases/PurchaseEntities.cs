using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tax;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Purchases;

/// <summary>Someone the business buys from. Stage 8 adds the ledger, credit terms and contacts.</summary>
public sealed partial class Supplier : ITenantOwned
{
    private Supplier()
    {
        Code = Name = StateCode = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public string? Gstin { get; private set; }

    public string StateCode { get; private set; }

    public string? Address { get; private set; }

    public string? Phone { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public bool IsGstRegistered => Gstin is not null;

    public static Supplier Create(Guid businessId, string code, string name, string? gstin, string stateCode, string? address, string? phone, DateTimeOffset now)
    {
        var supplier = new Supplier { Id = Guid.CreateVersion7(now), BusinessId = businessId, IsActive = true, CreatedAtUtc = now };
        supplier.Code = (code ?? string.Empty).Trim().ToUpperInvariant() is var c && CodePattern().IsMatch(c)
            ? c
            : throw new DomainException("supplier.code_invalid", "A supplier code is 1-20 letters, digits or hyphens.");
        supplier.Update(name, gstin, stateCode, address, phone);
        return supplier;
    }

    public void Update(string name, string? gstin, string stateCode, string? address, string? phone)
    {
        Name = Business.Required(name, "supplier.name_required", "Give the supplier's name (max 200 characters).", 200);
        StateCode = (stateCode ?? string.Empty).Trim() is { Length: 2 } s && s.All(char.IsAsciiDigit)
            ? s
            : throw new DomainException("supplier.state_invalid", "The supplier's state is a two-digit GST state code.");
        Gstin = string.IsNullOrWhiteSpace(gstin) ? null : Tax.Gstin.Validate(gstin, StateCode);
        Address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
    }

    public void SetActive(bool active) => IsActive = active;

    [GeneratedRegex("^[A-Z0-9-]{1,20}$")]
    private static partial Regex CodePattern();
}

/// <summary>A business's purchasing rules (spec section 12): when a cost change needs a reason or approval, and whether loss-leaders are allowed.</summary>
public sealed class PurchaseSettings : ITenantOwned
{
    private PurchaseSettings()
    {
    }

    public Guid BusinessId { get; private set; }

    public decimal CostReasonThresholdPercent { get; private set; }

    public decimal CostApprovalThresholdPercent { get; private set; }

    /// <summary>Selling below landed cost, with a reason and independent approval. Off by default.</summary>
    public bool AllowLossLeader { get; private set; }

    public uint RowVersion { get; private set; }

    public static PurchaseSettings Default(Guid businessId) => new() { BusinessId = businessId, CostReasonThresholdPercent = 5, CostApprovalThresholdPercent = 15 };

    public void Change(decimal reasonPercent, decimal approvalPercent, bool allowLossLeader)
    {
        PurchaseRules.ValidateThresholds(reasonPercent, approvalPercent);
        CostReasonThresholdPercent = reasonPercent;
        CostApprovalThresholdPercent = approvalPercent;
        AllowLossLeader = allowLossLeader;
    }
}

public static class GrnStatus
{
    /// <summary>Waiting for a manager (a large cost change or a loss-leader price): no stock has moved.</summary>
    public const string PendingApproval = "PENDING_APPROVAL";

    /// <summary>Stock received.</summary>
    public const string Posted = "POSTED";

    /// <summary>Approval refused: nothing was received; the supplier invoice can be entered again.</summary>
    public const string Rejected = "REJECTED";
}

/// <summary>
/// A goods receipt note: the supplier's invoice as received into a store, with its classification, lines, expenses
/// and landed costs. Its figures never change; it moves from pending approval to posted (or rejected) once.
/// </summary>
public sealed class Grn : ITenantOwned
{
    private Grn()
    {
        Number = SupplierInvoiceNumber = Classification = Status = IdempotencyKey = RequestHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string Number { get; private set; }

    public long SequenceNumber { get; private set; }

    public string SupplierInvoiceNumber { get; private set; }

    public DateOnly SupplierInvoiceDate { get; private set; }

    public string Classification { get; private set; }

    public string? PurchaseOrderReference { get; private set; }

    public bool IsInterState { get; private set; }

    public bool TaxRecoverable { get; private set; }

    public string Status { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public string? Notes { get; private set; }

    public decimal GrossTotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    public decimal TaxableTotal { get; private set; }

    public decimal CgstTotal { get; private set; }

    public decimal SgstTotal { get; private set; }

    public decimal IgstTotal { get; private set; }

    public decimal CessTotal { get; private set; }

    public decimal RoundOff { get; private set; }

    /// <summary>The supplier's invoice total: lines and taxes plus round-off (expenses paid separately are not in it).</summary>
    public decimal InvoiceTotal { get; private set; }

    public decimal ExpensesTotal { get; private set; }

    public decimal LandedTotal { get; private set; }

    public Guid? ApprovalRequestId { get; private set; }

    public Guid ReceivedByUserId { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public DateTimeOffset? PostedAtUtc { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string RequestHash { get; private set; }

    public uint RowVersion { get; private set; }

    public sealed record Header(
        Guid StoreId, Guid SupplierId, string SupplierInvoiceNumber, DateOnly SupplierInvoiceDate, string Classification, string? PurchaseOrderReference,
        bool IsInterState, bool TaxRecoverable, DateOnly BusinessDate, string? Notes);

    public static Grn Receive(
        Guid id, Guid businessId, string number, long sequence, Header header, GrnResult result, decimal roundOff, Guid receivedBy, string idempotencyKey,
        string requestHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(result);
        if (!PurchaseClassifications.All.Contains(header.Classification))
        {
            throw new DomainException("grn.classification_invalid", $"Unknown purchase classification '{header.Classification}'.");
        }

        if (header.SupplierInvoiceDate > header.BusinessDate)
        {
            throw new DomainException("grn.invoice_date_future", "The supplier's invoice cannot be dated after the day it is received.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            throw new DomainException("idempotency.key_required", "Each goods receipt needs an idempotency key (max 100 characters).");
        }

        return new Grn
        {
            Id = id,
            BusinessId = businessId,
            StoreId = header.StoreId,
            SupplierId = header.SupplierId,
            Number = number,
            SequenceNumber = sequence,
            SupplierInvoiceNumber = Business.Required(header.SupplierInvoiceNumber, "grn.invoice_number_required", "Enter the supplier's invoice number (max 30).", 30).ToUpperInvariant(),
            SupplierInvoiceDate = header.SupplierInvoiceDate,
            Classification = header.Classification,
            PurchaseOrderReference = string.IsNullOrWhiteSpace(header.PurchaseOrderReference) ? null : header.PurchaseOrderReference.Trim(),
            IsInterState = header.IsInterState,
            TaxRecoverable = header.TaxRecoverable,
            Status = GrnStatus.PendingApproval,
            BusinessDate = header.BusinessDate,
            Notes = string.IsNullOrWhiteSpace(header.Notes) ? null : header.Notes.Trim(),
            GrossTotal = result.Gross,
            DiscountTotal = result.Discount,
            TaxableTotal = result.Taxable,
            CgstTotal = result.Cgst,
            SgstTotal = result.Sgst,
            IgstTotal = result.Igst,
            CessTotal = result.Cess,
            RoundOff = roundOff,
            InvoiceTotal = result.InvoiceTotal + roundOff,
            ExpensesTotal = result.Expenses,
            LandedTotal = result.LandedTotal,
            ReceivedByUserId = receivedBy,
            ReceivedAtUtc = now,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
        };
    }

    public void AwaitApproval(Guid approvalRequestId) => ApprovalRequestId = approvalRequestId;

    public void Post(DateTimeOffset now)
    {
        if (Status != GrnStatus.PendingApproval)
        {
            throw new DomainException("grn.not_pending", $"Goods receipt {Number} is already {Status.ToLowerInvariant().Replace('_', ' ')}.");
        }

        Status = GrnStatus.Posted;
        PostedAtUtc = now;
    }

    public void Reject()
    {
        if (Status == GrnStatus.PendingApproval)
        {
            Status = GrnStatus.Rejected;
        }
    }
}

public sealed class GrnLine : ITenantOwned
{
    private GrnLine()
    {
        Description = UnitCode = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid GrnId { get; private set; }

    public int LineNumber { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid VariantUnitId { get; private set; }

    public string Description { get; private set; }

    public string UnitCode { get; private set; }

    public decimal FactorToBase { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal FreeQuantity { get; private set; }

    public decimal BaseQuantity { get; private set; }

    public decimal? Mrp { get; private set; }

    public decimal Rate { get; private set; }

    public decimal Discount { get; private set; }

    public decimal GstRatePercent { get; private set; }

    public decimal CessRatePercent { get; private set; }

    public decimal Gross { get; private set; }

    public decimal Taxable { get; private set; }

    public decimal Cgst { get; private set; }

    public decimal Sgst { get; private set; }

    public decimal Igst { get; private set; }

    public decimal Cess { get; private set; }

    public decimal Total { get; private set; }

    public decimal ExpenseShare { get; private set; }

    public decimal NonRecoverableTax { get; private set; }

    public decimal LandedTotal { get; private set; }

    public decimal LandedUnitCost { get; private set; }

    public string? BatchNumber { get; private set; }

    public DateOnly? ManufacturedOn { get; private set; }

    public DateOnly? ExpiresOn { get; private set; }

    public decimal? SellingPrice { get; private set; }

    public decimal? PreviousUnitCost { get; private set; }

    public decimal? CostChangePercent { get; private set; }

    public string? CostChangeReason { get; private set; }

    public string? LossLeaderReason { get; private set; }

    public decimal? Weight { get; private set; }

    public decimal? Volume { get; private set; }

    public sealed record Item(
        Guid ProductId, Guid VariantId, Guid VariantUnitId, string Description, string UnitCode, decimal FactorToBase, decimal Quantity, decimal FreeQuantity,
        decimal? Mrp, decimal Rate, decimal Discount, decimal GstRatePercent, decimal CessRatePercent, string? BatchNumber, DateOnly? ManufacturedOn,
        DateOnly? ExpiresOn, decimal? SellingPrice, decimal? Weight, decimal? Volume);

    public static GrnLine Create(
        Guid businessId, Guid grnId, int lineNumber, Item item, GrnLineResult amounts, CostChange? change, string? costChangeReason, string? lossLeaderReason,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(amounts);
        return new GrnLine
        {
            Id = SequentialGuid.Next(now),
            BusinessId = businessId,
            GrnId = grnId,
            LineNumber = lineNumber,
            ProductId = item.ProductId,
            VariantId = item.VariantId,
            VariantUnitId = item.VariantUnitId,
            Description = item.Description,
            UnitCode = item.UnitCode,
            FactorToBase = item.FactorToBase,
            Quantity = item.Quantity,
            FreeQuantity = item.FreeQuantity,
            BaseQuantity = amounts.BaseQuantity,
            Mrp = item.Mrp,
            Rate = item.Rate,
            Discount = amounts.Discount,
            GstRatePercent = item.GstRatePercent,
            CessRatePercent = item.CessRatePercent,
            Gross = amounts.Gross,
            Taxable = amounts.Taxable,
            Cgst = amounts.Cgst,
            Sgst = amounts.Sgst,
            Igst = amounts.Igst,
            Cess = amounts.Cess,
            Total = amounts.Total,
            ExpenseShare = amounts.ExpenseShare,
            NonRecoverableTax = amounts.NonRecoverableTax,
            LandedTotal = amounts.LandedTotal,
            LandedUnitCost = amounts.LandedUnitCost,
            BatchNumber = item.BatchNumber,
            ManufacturedOn = item.ManufacturedOn,
            ExpiresOn = item.ExpiresOn,
            SellingPrice = item.SellingPrice,
            PreviousUnitCost = change?.PreviousUnitCost,
            CostChangePercent = change?.PercentChange,
            CostChangeReason = string.IsNullOrWhiteSpace(costChangeReason) ? null : costChangeReason.Trim(),
            LossLeaderReason = string.IsNullOrWhiteSpace(lossLeaderReason) ? null : lossLeaderReason.Trim(),
            Weight = item.Weight,
            Volume = item.Volume,
        };
    }
}

public sealed class GrnExpense : ITenantOwned
{
    private GrnExpense()
    {
        Kind = Method = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid GrnId { get; private set; }

    public int ExpenseOrder { get; private set; }

    public string Kind { get; private set; }

    public decimal Amount { get; private set; }

    public string Method { get; private set; }

    public string? Note { get; private set; }

    public static GrnExpense Create(Guid businessId, Guid grnId, int order, GrnExpenseInput input, string? note, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new GrnExpense
        {
            Id = SequentialGuid.Next(now),
            BusinessId = businessId,
            GrnId = grnId,
            ExpenseOrder = order,
            Kind = input.Kind,
            Amount = input.Amount,
            Method = input.Method,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
    }
}

/// <summary>The share of one expense carried by one line (they add up to the expense exactly).</summary>
public sealed class GrnAllocation : ITenantOwned
{
    private GrnAllocation()
    {
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ExpenseId { get; private set; }

    public Guid LineId { get; private set; }

    public decimal Amount { get; private set; }

    public static GrnAllocation Create(Guid businessId, Guid expenseId, Guid lineId, decimal amount, DateTimeOffset now) => new()
    {
        Id = SequentialGuid.Next(now),
        BusinessId = businessId,
        ExpenseId = expenseId,
        LineId = lineId,
        Amount = amount,
    };
}
