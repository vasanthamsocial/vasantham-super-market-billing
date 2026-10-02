using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Organisation;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Sales;

public static class PaymentMethods
{
    public const string Cash = "CASH";
    public const string Card = "CARD";
    public const string Upi = "UPI";
    public const string Wallet = "WALLET";

    /// <summary>Paid from a credit note's store credit (an exchange). The reference is the credit note number.</summary>
    public const string CreditNote = "CREDIT_NOTE";

    /// <summary>Not paid now: added to the debtor's account, due after their credit period.</summary>
    public const string OnAccount = "ON_ACCOUNT";

    public static readonly IReadOnlyList<string> All = [Cash, Card, Upi, Wallet, CreditNote, OnAccount];
}

/// <summary>Where a line's price came from, when it was not a price rule.</summary>
public static class SaleRateTypes
{
    /// <summary>Overridden by the cashier with a supervisor's approval.</summary>
    public const string Override = "OVERRIDE";

    /// <summary>Overridden by a person who may override prices themselves (recorded and audited).</summary>
    public const string OverrideSelf = "OVERRIDE_SELF";
}

public static class SupervisorApprovalKinds
{
    /// <summary>Selling a pack at a price other than the one the price rules give.</summary>
    public const string PriceOverride = "PRICE_OVERRIDE";

    /// <summary>Item or bill discounts on one bill, up to an amount.</summary>
    public const string Discount = "DISCOUNT";

    /// <summary>A return (credit note) up to an amount.</summary>
    public const string Return = "RETURN";

    /// <summary>Cash paid out of the drawer during a shift, up to an amount.</summary>
    public const string PayOut = "PAY_OUT";

    /// <summary>A sale on account taking the debtor beyond their credit limit, by up to an amount.</summary>
    public const string CreditLimit = "CREDIT_LIMIT";

    public static readonly IReadOnlyList<string> All = [PriceOverride, Discount, Return, PayOut, CreditLimit];
}

public sealed record PaymentInput(string Method, decimal Amount, string? Reference);

/// <summary>How a bill is paid: split across methods, with change given only from cash.</summary>
public static class PaymentRules
{
    /// <summary>Validates the payments against the bill and returns the change due to the customer.</summary>
    public static decimal ChangeDue(decimal grandTotal, IReadOnlyList<PaymentInput> payments)
    {
        ArgumentNullException.ThrowIfNull(payments);
        if (payments.Count == 0)
        {
            throw new DomainException("payment.required", "Record how the bill was paid.");
        }

        foreach (var payment in payments)
        {
            if (!PaymentMethods.All.Contains(payment.Method))
            {
                throw new DomainException("payment.method_invalid", $"Unknown payment method '{payment.Method}'.");
            }

            if (payment.Amount <= 0 || payment.Amount != InvoiceCalculator.Money(payment.Amount))
            {
                throw new DomainException("payment.amount_invalid", "Each payment must be a positive amount in rupees and paise.");
            }

            if (payment.Reference is { Length: > 60 })
            {
                throw new DomainException("payment.reference_too_long", "A payment reference can be at most 60 characters.");
            }
        }

        if (payments.Any(p => p.Method == PaymentMethods.CreditNote && string.IsNullOrWhiteSpace(p.Reference)))
        {
            throw new DomainException("payment.credit_note_number_required", "Enter the credit note number to pay with store credit.");
        }

        var cash = payments.Where(p => p.Method == PaymentMethods.Cash).Sum(p => p.Amount);
        var other = payments.Sum(p => p.Amount) - cash;
        if (other > grandTotal)
        {
            throw new DomainException("payment.overpaid_non_cash", "Card, UPI, wallet, credit-note and on-account payments cannot be more than the bill; change is given only in cash.");
        }

        var change = cash + other - grandTotal;
        return change >= 0
            ? change
            : throw new DomainException("payment.short", $"The payments are Rs. {-change:0.00} short of the bill.");
    }
}

/// <summary>A billing counter in a store. Its code is part of every invoice number it issues, so it never changes.</summary>
public sealed partial class Counter : ITenantOwned
{
    private Counter()
    {
        Code = Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    /// <summary>1-6 letters or digits, unique in the business (so unique per GSTIN, as invoice numbers must be).</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    /// <summary>
    /// The prefix of this counter's invoice numbers while a given tax registration is in force. A change of registration
    /// mode starts a new series (spec section 6): the first registration uses the bare code (<c>C1</c>), later ones
    /// add a letter (<c>C1B</c>, <c>C1C</c>, ...), so numbers stay unique without restarting the old series.
    /// </summary>
    public string InvoicePrefix(int registrationIndex) => registrationIndex switch
    {
        0 => Code,
        > 0 and < 26 => Code + (char)('A' + registrationIndex),
        _ => throw new DomainException("invoice.series_exhausted", "Too many registration changes for one counter series."),
    };

    /// <summary>The document-sequence name of an invoice prefix.</summary>
    public static string InvoiceSeries(string prefix) => "INV-" + prefix;

    public static Counter Create(Guid businessId, Guid storeId, string code, string name, DateTimeOffset now)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalized))
        {
            throw new DomainException("counter.code_invalid", "A counter code is 1 to 6 letters or digits (it starts every invoice number).");
        }

        return new Counter
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            StoreId = storeId,
            Code = normalized,
            Name = Business.Required(name, "counter.name_required", "Give the counter a name (max 60 characters).", 60),
            IsActive = true,
            CreatedAtUtc = now,
        };
    }

    public void Rename(string name) => Name = Business.Required(name, "counter.name_required", "Give the counter a name (max 60 characters).", 60);

    public void SetActive(bool active) => IsActive = active;

    /// <summary>The number printed on the invoice: counter prefix, hyphen, at least six digits (D-017).</summary>
    public static string InvoiceNumber(string prefix, long sequence) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{prefix}-{sequence:000000}");

    [GeneratedRegex("^[A-Z0-9]{1,6}$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// A browser trusted to bill on a counter. A manager enrols it once; it then carries a secret device cookie.
/// Billing requires both a signed-in cashier and an enrolled device, so a stolen password alone cannot bill.
/// </summary>
public sealed class CounterDevice : ITenantOwned
{
    private CounterDevice()
    {
        Name = string.Empty;
        TokenHash = [];
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid CounterId { get; private set; }

    public string Name { get; private set; }

    public byte[] TokenHash { get; private set; }

    public Guid EnrolledByUserId { get; private set; }

    public DateTimeOffset EnrolledAtUtc { get; private set; }

    public DateTimeOffset? LastSeenAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public Guid? RevokedByUserId { get; private set; }

    public bool IsActive => RevokedAtUtc is null;

    public static CounterDevice Enrol(Guid businessId, Guid counterId, string name, byte[] tokenHash, Guid enrolledBy, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = businessId,
        CounterId = counterId,
        Name = Business.Required(name, "device.name_required", "Name the device, for example 'Counter 1 PC' (max 60 characters).", 60),
        TokenHash = tokenHash,
        EnrolledByUserId = enrolledBy,
        EnrolledAtUtc = now,
    };

    public void Revoke(Guid by, DateTimeOffset now)
    {
        RevokedAtUtc ??= now;
        RevokedByUserId ??= by;
    }

    public void Seen(DateTimeOffset now) => LastSeenAtUtc = now;
}

/// <summary>
/// A supervisor's on-the-spot approval at a counter, for a price override or a discount. It is single-use, short-lived,
/// bound to the counter (and for a price, to the pack and price), and the supervisor must be someone other than the cashier.
/// </summary>
public sealed class SupervisorApproval : ITenantOwned
{
    private SupervisorApproval()
    {
        Kind = Reason = string.Empty;
        TokenHash = [];
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid CounterId { get; private set; }

    public string Kind { get; private set; }

    public Guid? VariantUnitId { get; private set; }

    public decimal? ApprovedPrice { get; private set; }

    public decimal? MaxAmount { get; private set; }

    public string Reason { get; private set; }

    public Guid ApprovedByUserId { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public byte[] TokenHash { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? UsedAtUtc { get; private set; }

    public Guid? UsedDocumentId { get; private set; }

    public static SupervisorApproval Grant(
        Guid businessId, Guid counterId, string kind, Guid? variantUnitId, decimal? price, decimal? maxAmount, string reason,
        Guid approvedBy, Guid requestedBy, byte[] tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        if (approvedBy == requestedBy)
        {
            throw new DomainException("approval.self", "A supervisor cannot approve their own bill. Another authorised person must approve.");
        }

        switch (kind)
        {
            case SupervisorApprovalKinds.PriceOverride when variantUnitId is null || price is null || price < 0 || price != InvoiceCalculator.Money(price.Value):
                throw new DomainException("approval.price_invalid", "A price override needs the item and a price in rupees and paise.");
            case SupervisorApprovalKinds.Discount or SupervisorApprovalKinds.Return or SupervisorApprovalKinds.PayOut or SupervisorApprovalKinds.CreditLimit
                when maxAmount is null || maxAmount <= 0:
                throw new DomainException("approval.amount_invalid", "This approval needs the largest amount allowed.");
            case SupervisorApprovalKinds.PriceOverride or SupervisorApprovalKinds.Discount or SupervisorApprovalKinds.Return or SupervisorApprovalKinds.PayOut
                or SupervisorApprovalKinds.CreditLimit:
                break;
            default:
                throw new DomainException("approval.kind_invalid", $"Unknown approval kind '{kind}'.");
        }

        return new SupervisorApproval
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            CounterId = counterId,
            Kind = kind,
            VariantUnitId = kind == SupervisorApprovalKinds.PriceOverride ? variantUnitId : null,
            ApprovedPrice = kind == SupervisorApprovalKinds.PriceOverride ? price : null,
            MaxAmount = kind == SupervisorApprovalKinds.PriceOverride ? null : InvoiceCalculator.Money(maxAmount!.Value),
            Reason = Business.Required(reason, "approval.reason_required", "Give a reason for the approval (max 200 characters).", 200),
            ApprovedByUserId = approvedBy,
            RequestedByUserId = requestedBy,
            TokenHash = tokenHash,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + lifetime,
        };
    }

    /// <summary>Uses the approval for one document; refuses if it was used, has expired, or was given to someone else or another counter.</summary>
    public void Use(Guid counterId, Guid cashierUserId, Guid documentId, DateTimeOffset now)
    {
        if (UsedAtUtc is not null)
        {
            throw new DomainException("approval.used", "This approval has already been used. Ask the supervisor again.");
        }

        if (now >= ExpiresAtUtc)
        {
            throw new DomainException("approval.expired", "This approval has expired. Ask the supervisor again.");
        }

        if (counterId != CounterId || cashierUserId != RequestedByUserId)
        {
            throw new DomainException("approval.mismatch", "This approval was given for another counter or cashier.");
        }

        UsedAtUtc = now;
        UsedDocumentId = documentId;
    }
}

/// <summary>
/// An issued sales invoice (tax invoice, bill of supply or commercial invoice). Immutable: it keeps the seller's and
/// buyer's details, prices, rule ids and taxes as they were, so it reads the same forever. Corrections are credit notes.
/// </summary>
public sealed class SalesInvoice : ITenantOwned
{
    private readonly List<SalesInvoiceLine> _lines = [];
    private readonly List<SalesInvoicePayment> _payments = [];

    private SalesInvoice()
    {
        Number = NumberPrefix = Kind = TaxMode = Channel = SellerName = SellerAddress = SellerStateCode = PlaceOfSupplyStateCode = IdempotencyKey = RequestHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid CounterId { get; private set; }

    public Guid DeviceId { get; private set; }

    /// <summary>The cashier's shift the document was issued in (empty only for documents from before shifts existed).</summary>
    public Guid? ShiftId { get; private set; }

    public string Number { get; private set; }

    /// <summary>The counter's series this invoice belongs to (counter code, plus a letter after a registration change).</summary>
    public string NumberPrefix { get; private set; }

    public long SequenceNumber { get; private set; }

    public string Kind { get; private set; }

    public string TaxMode { get; private set; }

    public string Channel { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    public Guid CashierUserId { get; private set; }

    public string SellerName { get; private set; }

    public string? SellerGstin { get; private set; }

    public string SellerAddress { get; private set; }

    public string SellerStateCode { get; private set; }

    public string? BuyerName { get; private set; }

    public string? BuyerGstin { get; private set; }

    public string? BuyerPhone { get; private set; }

    public string? BuyerAddress { get; private set; }

    public string PlaceOfSupplyStateCode { get; private set; }

    public bool IsInterState { get; private set; }

    public decimal GrossTotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    public decimal TaxableTotal { get; private set; }

    public decimal CgstTotal { get; private set; }

    public decimal SgstTotal { get; private set; }

    public decimal IgstTotal { get; private set; }

    public decimal CessTotal { get; private set; }

    public decimal RoundOff { get; private set; }

    public decimal GrandTotal { get; private set; }

    public decimal PaidTotal { get; private set; }

    public decimal ChangeDue { get; private set; }

    public Guid? DiscountApprovalId { get; private set; }

    public bool NegativeStockOverride { get; private set; }

    /// <summary>The debtor (customer account) billed, if any.</summary>
    public Guid? DebtorId { get; private set; }

    /// <summary>When the part on account is due: the invoice date plus the debtor's credit period at the time (never recomputed).</summary>
    public DateOnly? DueDate { get; private set; }

    /// <summary>The supervisor approval that let this sale go beyond the debtor's credit limit.</summary>
    public Guid? CreditApprovalId { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string RequestHash { get; private set; }

    public decimal OnAccount => _payments.Where(p => p.Method == PaymentMethods.OnAccount).Sum(p => p.Amount);

    public IReadOnlyList<SalesInvoiceLine> Lines => _lines;

    public IReadOnlyList<SalesInvoicePayment> Payments => _payments;

    public sealed record Seller(string Name, string? Gstin, string Address, string StateCode);

    public sealed record Buyer(string? Name, string? Gstin, string? Phone, string? Address);

    /// <summary>The debtor billed; with an amount on account, its due date (and any credit-limit approval).</summary>
    public sealed record Account(Guid DebtorId, int CreditPeriodDays, Guid? CreditApprovalId);

    public static SalesInvoice Issue(
        Guid id, Guid businessId, Guid storeId, Counter counter, Guid deviceId, Guid shiftId, string numberPrefix, long sequence, string taxMode, string channel, DateOnly businessDate,
        Guid cashier, Seller seller, Buyer buyer, string placeOfSupply, BillResult bill, IReadOnlyList<PaymentInput> payments,
        Guid? discountApprovalId, bool negativeStockOverride, string idempotencyKey, string requestHash, DateTimeOffset now, Account? account = null)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(seller);
        ArgumentNullException.ThrowIfNull(buyer);
        ArgumentNullException.ThrowIfNull(bill);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            throw new DomainException("idempotency.key_required", "Each bill needs an idempotency key (max 100 characters).");
        }

        var change = PaymentRules.ChangeDue(bill.GrandTotal, payments);
        var onAccount = payments.Where(p => p.Method == PaymentMethods.OnAccount).Sum(p => p.Amount);
        if (onAccount > 0 && account is null)
        {
            throw new DomainException("payment.debtor_required", "Choose the customer's account to put the bill on account.");
        }

        var invoice = new SalesInvoice
        {
            Id = id,
            BusinessId = businessId,
            StoreId = storeId,
            CounterId = counter.Id,
            DeviceId = deviceId,
            ShiftId = shiftId,
            Number = numberPrefix.StartsWith(counter.Code, StringComparison.Ordinal) ? Counter.InvoiceNumber(numberPrefix, sequence)
                : throw new DomainException("invoice.prefix_invalid", "The invoice prefix must start with the counter code."),
            NumberPrefix = numberPrefix,
            SequenceNumber = sequence,
            Kind = bill.Kind,
            TaxMode = taxMode,
            Channel = channel,
            BusinessDate = businessDate,
            IssuedAtUtc = now,
            CashierUserId = cashier,
            SellerName = seller.Name,
            SellerGstin = seller.Gstin,
            SellerAddress = seller.Address,
            SellerStateCode = seller.StateCode,
            BuyerName = Clean(buyer.Name, 100),
            BuyerGstin = buyer.Gstin,
            BuyerPhone = Clean(buyer.Phone, 20),
            BuyerAddress = Clean(buyer.Address, 300),
            PlaceOfSupplyStateCode = placeOfSupply,
            IsInterState = placeOfSupply != seller.StateCode,
            GrossTotal = bill.Gross,
            DiscountTotal = bill.Discount,
            TaxableTotal = bill.Taxable,
            CgstTotal = bill.Cgst,
            SgstTotal = bill.Sgst,
            IgstTotal = bill.Igst,
            CessTotal = bill.Cess,
            RoundOff = bill.RoundOff,
            GrandTotal = bill.GrandTotal,
            PaidTotal = payments.Sum(p => p.Amount),
            ChangeDue = change,
            DiscountApprovalId = discountApprovalId,
            NegativeStockOverride = negativeStockOverride,
            DebtorId = account?.DebtorId,
            DueDate = onAccount > 0 ? businessDate.AddDays(account!.CreditPeriodDays) : null,
            CreditApprovalId = onAccount > 0 ? account!.CreditApprovalId : null,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
        };

        var order = 0;
        foreach (var payment in payments)
        {
            invoice._payments.Add(new SalesInvoicePayment(SequentialGuid.Next(now), businessId, id, ++order, payment.Method, payment.Amount, Clean(payment.Reference, 60)));
        }

        return invoice;
    }

    public void AddLine(SalesInvoiceLine line) => _lines.Add(line);

    /// <summary>Records the supervisor approval that let the part on account go beyond the debtor's credit limit.</summary>
    public void UseCreditApproval(Guid approvalId) =>
        CreditApprovalId = DueDate is not null ? approvalId : throw new DomainException("credit.not_on_account", "Only a bill on account needs a credit approval.");

    private static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= max ? trimmed : throw new DomainException("invoice.field_too_long", $"'{trimmed[..Math.Min(20, trimmed.Length)]}...' is too long (max {max}).");
    }
}

/// <summary>An invoice line as sold: what, how many, at which price and by which rule, with its taxes and its cost.</summary>
public sealed class SalesInvoiceLine : ITenantOwned
{
    private SalesInvoiceLine()
    {
        Description = HsnSac = UnitCode = SupplyType = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid InvoiceId { get; private set; }

    public int LineNumber { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid VariantUnitId { get; private set; }

    public string Description { get; private set; }

    public string HsnSac { get; private set; }

    public string UnitCode { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal BaseQuantity { get; private set; }

    public decimal? Mrp { get; private set; }

    /// <summary>The price rule that gave the price; null when a supervisor overrode it.</summary>
    public Guid? PriceRuleId { get; private set; }

    public string RateType { get; private set; } = string.Empty;

    public Guid? PriceOverrideApprovalId { get; private set; }

    public decimal UnitPrice { get; private set; }

    public bool TaxInclusive { get; private set; }

    public string SupplyType { get; private set; }

    public decimal GstRatePercent { get; private set; }

    public decimal CessRatePercent { get; private set; }

    public decimal Gross { get; private set; }

    public decimal ItemDiscount { get; private set; }

    public decimal BillDiscount { get; private set; }

    public decimal Taxable { get; private set; }

    public decimal Cgst { get; private set; }

    public decimal Sgst { get; private set; }

    public decimal Igst { get; private set; }

    public decimal Cess { get; private set; }

    public decimal Total { get; private set; }

    /// <summary>Cost of the stock that left (from the stock ledger), for margin reports.</summary>
    public decimal CostOfGoods { get; private set; }

    public sealed record Item(
        Guid ProductId, Guid VariantId, Guid VariantUnitId, string Description, string HsnSac, string UnitCode, decimal Quantity, decimal BaseQuantity,
        decimal? Mrp);

    public sealed record Pricing(Guid? PriceRuleId, string RateType, Guid? OverrideApprovalId, decimal UnitPrice, bool TaxInclusive, string SupplyType, decimal GstRate, decimal CessRate);

    public static SalesInvoiceLine Create(Guid businessId, Guid invoiceId, int lineNumber, Item item, Pricing pricing, BillLineResult amounts, decimal costOfGoods, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(amounts);
        return new SalesInvoiceLine
        {
            Id = SequentialGuid.Next(now),
            BusinessId = businessId,
            InvoiceId = invoiceId,
            LineNumber = lineNumber,
            ProductId = item.ProductId,
            VariantId = item.VariantId,
            VariantUnitId = item.VariantUnitId,
            Description = item.Description,
            HsnSac = item.HsnSac,
            UnitCode = item.UnitCode,
            Quantity = item.Quantity,
            BaseQuantity = item.BaseQuantity,
            Mrp = item.Mrp,
            PriceRuleId = pricing.PriceRuleId,
            RateType = pricing.RateType,
            PriceOverrideApprovalId = pricing.OverrideApprovalId,
            UnitPrice = pricing.UnitPrice,
            TaxInclusive = pricing.TaxInclusive,
            SupplyType = pricing.SupplyType,
            GstRatePercent = pricing.GstRate,
            CessRatePercent = pricing.CessRate,
            Gross = amounts.Gross,
            ItemDiscount = amounts.ItemDiscount,
            BillDiscount = amounts.BillDiscount,
            Taxable = amounts.Taxable,
            Cgst = amounts.Cgst,
            Sgst = amounts.Sgst,
            Igst = amounts.Igst,
            Cess = amounts.Cess,
            Total = amounts.Total,
            CostOfGoods = costOfGoods,
        };
    }
}

public sealed class SalesInvoicePayment : ITenantOwned
{
    private SalesInvoicePayment()
    {
        Method = string.Empty;
    }

    internal SalesInvoicePayment(Guid id, Guid businessId, Guid invoiceId, int order, string method, decimal amount, string? reference)
    {
        Id = id;
        BusinessId = businessId;
        InvoiceId = invoiceId;
        PaymentOrder = order;
        Method = method;
        Amount = amount;
        Reference = reference;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid InvoiceId { get; private set; }

    public int PaymentOrder { get; private set; }

    public string Method { get; private set; }

    public decimal Amount { get; private set; }

    public string? Reference { get; private set; }
}

/// <summary>
/// A bill put aside at a counter (the customer went to fetch something) and picked up again later. It is only a cart,
/// not a sale: no number, no stock, no payment. Retrieving it removes it.
/// </summary>
public sealed class ParkedBill : ITenantOwned
{
    public const int MaxPerCounter = 20;

    private ParkedBill()
    {
        CartJson = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid CounterId { get; private set; }

    public Guid ParkedByUserId { get; private set; }

    public string? Label { get; private set; }

    public int ItemCount { get; private set; }

    public string CartJson { get; private set; }

    public DateTimeOffset ParkedAtUtc { get; private set; }

    public static ParkedBill Park(Guid businessId, Guid counterId, Guid parkedBy, string? label, int itemCount, string cartJson, DateTimeOffset now)
    {
        var trimmed = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (trimmed is { Length: > 40 })
        {
            throw new DomainException("parked.label_too_long", "A parked bill's label can be at most 40 characters.");
        }

        if (itemCount is < 1 or > 300)
        {
            throw new DomainException("parked.empty", "Only a bill with 1 to 300 items can be parked.");
        }

        return new ParkedBill
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            CounterId = counterId,
            ParkedByUserId = parkedBy,
            Label = trimmed,
            ItemCount = itemCount,
            CartJson = cartJson,
            ParkedAtUtc = now,
        };
    }
}
