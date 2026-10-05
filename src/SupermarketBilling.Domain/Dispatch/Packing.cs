using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Dispatch;

public static class ChallanStatus
{
    public const string Open = "OPEN";

    /// <summary>The bill's delivery changed to pickup before anything was dispatched.</summary>
    public const string Cancelled = "CANCELLED";
}

/// <summary>Where a challan's goods are, worked out from its quantities (never stored).</summary>
public static class ChallanProgress
{
    public const string ToPick = "TO_PICK";
    public const string ToCheck = "TO_CHECK";
    public const string ToPack = "TO_PACK";
    public const string PartlyPacked = "PARTLY_PACKED";
    public const string Packed = "PACKED";
    public const string PartlyDispatched = "PARTLY_DISPATCHED";
    public const string Dispatched = "DISPATCHED";
    public const string Delivered = "DELIVERED";
    public const string NothingToSend = "NOTHING_TO_SEND";
    public const string Cancelled = "CANCELLED";

    /// <param name="Out">Dispatched in dispatches that stand, less what came back.</param>
    public sealed record LineState(decimal Ordered, decimal? Picked, decimal? Checked, decimal Packed, decimal Out, decimal Delivered);

    public static string Of(string status, IReadOnlyCollection<LineState> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (status == ChallanStatus.Cancelled)
        {
            return Cancelled;
        }

        if (lines.Any(l => l.Picked is null))
        {
            return ToPick;
        }

        if (lines.Any(l => l.Checked is null))
        {
            return ToCheck;
        }

        var toSend = lines.Sum(l => l.Checked!.Value);
        if (toSend == 0)
        {
            return NothingToSend;
        }

        if (lines.Sum(l => l.Delivered) >= toSend)
        {
            return Delivered;
        }

        var outstanding = lines.Sum(l => l.Out);
        if (outstanding > 0 || lines.Sum(l => l.Delivered) > 0)
        {
            return outstanding + lines.Sum(l => l.Delivered) >= toSend ? Dispatched : PartlyDispatched;
        }

        var packed = lines.Sum(l => l.Packed);
        return packed == 0 ? ToPack : packed < toSend ? PartlyPacked : Packed;
    }
}

/// <summary>
/// The packing challan of a bill sent by delivery or lorry (spec section 20): what to pick, check, pack and send,
/// with who did each step. It carries no prices, cost, profit or balance. Differences (short picking, goods not
/// delivered) are recorded here and never change the invoice; they are settled by a credit note or by sending again.
/// </summary>
public sealed class PackingChallan : ITenantOwned
{
    private readonly List<PackingChallanLine> _lines = [];

    private PackingChallan()
    {
        Number = Status = PartyName = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid InvoiceId { get; private set; }

    public string Number { get; private set; }

    public string PartyName { get; private set; }

    public string Status { get; private set; }

    public Guid? PickedByUserId { get; private set; }

    public DateTimeOffset? PickedAtUtc { get; private set; }

    public Guid? CheckedByUserId { get; private set; }

    public DateTimeOffset? CheckedAtUtc { get; private set; }

    public Guid? PackedByUserId { get; private set; }

    public int PackageCount { get; private set; }

    public string? CancelReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public IReadOnlyList<PackingChallanLine> Lines => _lines;

    public static PackingChallan Create(Guid businessId, Guid storeId, Guid invoiceId, string number, string partyName, IEnumerable<PackingChallanLine.Item> items,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);
        var challan = new PackingChallan
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            StoreId = storeId,
            InvoiceId = invoiceId,
            Number = number,
            PartyName = DispatchText.Required(partyName, 200, "challan.party_required", "Party name"),
            Status = ChallanStatus.Open,
            CreatedAtUtc = now,
        };
        challan._lines.AddRange(items.Select(i => PackingChallanLine.Create(challan, i, now)));
        return challan.Lines.Count > 0 ? challan : throw new DomainException("challan.no_lines", "A challan needs at least one item.");
    }

    /// <summary>The picker's count of every line. Less than billed (a short pick) needs the reason.</summary>
    public void Pick(IReadOnlyDictionary<Guid, (decimal Quantity, string? Reason)> picked, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(picked);
        RequireOpen();
        if (PickedByUserId is not null)
        {
            throw new DomainException("challan.already_picked", "This challan has been picked.");
        }

        RequireAll(picked.Keys);
        // Every line is checked before any changes, so a refused count leaves the challan as it was.
        var reasons = _lines.ToDictionary(l => l.Id, l => l.PickReason(picked[l.Id].Quantity, picked[l.Id].Reason));
        foreach (var line in _lines)
        {
            line.SetPicked(picked[line.Id].Quantity, reasons[line.Id]);
        }

        (PickedByUserId, PickedAtUtc) = (userId, now);
    }

    /// <summary>A second person counts what was picked. Less than picked needs the reason.</summary>
    public void Check(IReadOnlyDictionary<Guid, (decimal Quantity, string? Reason)> checkedQuantities, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(checkedQuantities);
        RequireOpen();
        if (PickedByUserId is null)
        {
            throw new DomainException("challan.not_picked", "Pick the goods first.");
        }

        if (CheckedByUserId is not null)
        {
            throw new DomainException("challan.already_checked", "This challan has been checked.");
        }

        if (userId == PickedByUserId)
        {
            throw new DomainException("challan.self_check", "Someone other than the picker checks the goods.");
        }

        RequireAll(checkedQuantities.Keys);
        var reasons = _lines.ToDictionary(l => l.Id, l => l.CheckReason(checkedQuantities[l.Id].Quantity, checkedQuantities[l.Id].Reason));
        foreach (var line in _lines)
        {
            line.SetChecked(checkedQuantities[line.Id].Quantity, reasons[line.Id]);
        }

        (CheckedByUserId, CheckedAtUtc) = (userId, now);
    }

    /// <summary>Packs some or all of what was checked, into a number of packages (more can be packed later).</summary>
    public void Pack(IReadOnlyDictionary<Guid, decimal> quantities, int packages, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(quantities);
        RequireOpen();
        if (CheckedByUserId is null)
        {
            throw new DomainException("challan.not_checked", "The goods are checked before they are packed.");
        }

        if (packages is < 1 or > 999)
        {
            throw new DomainException("challan.packages_invalid", "Give the number of packages (1 to 999).");
        }

        if (quantities.Count == 0 || quantities.Values.All(q => q == 0))
        {
            throw new DomainException("challan.nothing_packed", "Enter what was packed.");
        }

        foreach (var (lineId, quantity) in quantities)
        {
            Line(lineId).RequirePackable(quantity);
        }

        foreach (var (lineId, quantity) in quantities)
        {
            Line(lineId).AddPacked(quantity);
        }

        PackedByUserId ??= userId;
        PackageCount += packages;
    }

    public void Cancel(string reason)
    {
        RequireOpen();
        CancelReason = DispatchText.Required(reason, 300, "challan.reason_required", "Reason");
        Status = ChallanStatus.Cancelled;
    }

    public PackingChallanLine Line(Guid lineId) =>
        _lines.FirstOrDefault(l => l.Id == lineId) ?? throw new DomainException("challan.line_unknown", "That item is not on this challan.");

    private void RequireAll(IEnumerable<Guid> ids)
    {
        var given = ids.ToHashSet();
        if (!given.SetEquals(_lines.Select(l => l.Id)))
        {
            throw new DomainException("challan.lines_incomplete", "Give a quantity for every item on the challan.");
        }
    }

    private void RequireOpen()
    {
        if (Status != ChallanStatus.Open)
        {
            throw new DomainException("challan.cancelled", "This challan is cancelled.");
        }
    }
}

/// <summary>One item of a challan: what was billed, and how much was picked, checked and packed.</summary>
public sealed class PackingChallanLine : ITenantOwned
{
    private PackingChallanLine()
    {
        ItemName = UnitCode = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ChallanId { get; private set; }

    public Guid InvoiceLineId { get; private set; }

    public int LineNumber { get; private set; }

    public string ItemName { get; private set; }

    public string? VariantName { get; private set; }

    public string UnitCode { get; private set; }

    /// <summary>As billed.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Free goods sent with the line (not billed). Sales do not carry free goods yet, so 0.</summary>
    public decimal FreeQuantity { get; private set; }

    public decimal? PickedQuantity { get; private set; }

    public decimal? CheckedQuantity { get; private set; }

    /// <summary>Why less was picked or checked than billed.</summary>
    public string? ShortReason { get; private set; }

    public decimal PackedQuantity { get; private set; }

    public uint RowVersion { get; private set; }

    public sealed record Item(Guid InvoiceLineId, int LineNumber, string ItemName, string? VariantName, string UnitCode, decimal Quantity);

    internal static PackingChallanLine Create(PackingChallan challan, Item item, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = challan.BusinessId,
        ChallanId = challan.Id,
        InvoiceLineId = item.InvoiceLineId,
        LineNumber = item.LineNumber,
        ItemName = item.ItemName,
        VariantName = item.VariantName is { } v && v != item.ItemName ? v : null,
        UnitCode = item.UnitCode,
        Quantity = item.Quantity > 0 ? item.Quantity : throw new DomainException("challan.quantity_invalid", "Billed quantities are more than zero."),
    };

    /// <summary>Validates a picked count and returns its short reason (if short); changes nothing.</summary>
    internal string? PickReason(decimal quantity, string? reason)
    {
        RequireQuantity(quantity, Quantity, "picked", "billed");
        return Reason(quantity < Quantity, reason, "picked");
    }

    internal void SetPicked(decimal quantity, string? shortReason) => (PickedQuantity, ShortReason) = (quantity, shortReason);

    /// <summary>Validates a checked count and returns its short reason (if less than picked); changes nothing.</summary>
    internal string? CheckReason(decimal quantity, string? reason)
    {
        RequireQuantity(quantity, PickedQuantity!.Value, "checked", "picked");
        return Reason(quantity < PickedQuantity, reason, "checked");
    }

    internal void SetChecked(decimal quantity, string? reason)
    {
        CheckedQuantity = quantity;
        if (reason is not null)
        {
            ShortReason = ShortReason is null ? reason : $"{ShortReason}; {reason}";
        }
    }

    internal void RequirePackable(decimal quantity)
    {
        if (quantity < 0 || decimal.Round(quantity, 3) != quantity || PackedQuantity + quantity > CheckedQuantity!.Value)
        {
            throw new DomainException("challan.pack_too_much", $"{ItemName}: pack at most the {CheckedQuantity - PackedQuantity:0.###} {UnitCode} checked and not yet packed.");
        }
    }

    internal void AddPacked(decimal quantity)
    {
        RequirePackable(quantity);
        PackedQuantity += quantity;
    }

    private void RequireQuantity(decimal quantity, decimal most, string what, string than)
    {
        if (quantity < 0 || quantity > most || decimal.Round(quantity, 3) != quantity)
        {
            throw new DomainException("challan.quantity_invalid", $"{ItemName}: {what} is 0 to {most:0.###} {UnitCode} (as {than}).");
        }
    }

    private string? Reason(bool needed, string? reason, string what) =>
        !needed ? null
        : DispatchText.Optional(reason, 200, "challan.reason_invalid", "The reason")
          ?? throw new DomainException("challan.short_reason_required", $"{ItemName}: say why less was {what} than expected.");
}

/// <summary>One step in a challan's life (append-only): picked, checked, packed, dispatched, delivered, returned, cancelled.</summary>
public sealed class PackingEvent : ITenantOwned
{
    private PackingEvent()
    {
        Kind = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid ChallanId { get; private set; }

    public string Kind { get; private set; }

    public string? Detail { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset AtUtc { get; private set; }

    public static PackingEvent Record(Guid businessId, Guid challanId, string kind, string? detail, Guid userId, DateTimeOffset now) => new()
    {
        Id = SequentialGuid.Next(now),
        BusinessId = businessId,
        ChallanId = challanId,
        Kind = kind,
        Detail = detail is { Length: > 500 } d ? d[..500] : detail,
        UserId = userId,
        AtUtc = now,
    };
}

public static class DeliveryOutcomes
{
    public const string Delivered = "DELIVERED";
    public const string PartlyDelivered = "PARTLY_DELIVERED";
    public const string Failed = "FAILED";
}

/// <summary>How much of a challan item a dispatch carried, and later how much was delivered and how much came back.</summary>
public sealed class ConsignmentLine : ITenantOwned
{
    private ConsignmentLine()
    {
    }

    public Guid ConsignmentId { get; private set; }

    public Guid ChallanLineId { get; private set; }

    public Guid BusinessId { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal? DeliveredQuantity { get; private set; }

    public decimal? ReturnedQuantity { get; private set; }

    public static ConsignmentLine Carry(Guid businessId, Guid consignmentId, Guid challanLineId, decimal quantity) =>
        quantity > 0 && decimal.Round(quantity, 3) == quantity
            ? new ConsignmentLine { BusinessId = businessId, ConsignmentId = consignmentId, ChallanLineId = challanLineId, Quantity = quantity }
            : throw new DomainException("consignment.quantity_invalid", "Dispatched quantities are more than zero.");

    /// <summary>What is neither delivered nor back in the store (in transit until reported, lost after).</summary>
    public decimal Outstanding => Quantity - (DeliveredQuantity ?? 0) - (ReturnedQuantity ?? 0);

    internal static void RequireDelivered(decimal sent, decimal quantity)
    {
        if (quantity < 0 || quantity > sent || decimal.Round(quantity, 3) != quantity)
        {
            throw new DomainException("delivery.quantity_invalid", $"Delivered is 0 to {sent:0.###}.");
        }
    }

    internal static void RequireReturned(ConsignmentLine line, decimal quantity)
    {
        var undelivered = line.Quantity - line.DeliveredQuantity!.Value;
        if (quantity < 0 || quantity > undelivered || decimal.Round(quantity, 3) != quantity)
        {
            throw new DomainException("return.quantity_invalid", $"Back in the store is 0 to {undelivered:0.###} (what was not delivered).");
        }
    }

    internal void Deliver(decimal quantity)
    {
        RequireDelivered(Quantity, quantity);
        DeliveredQuantity = quantity;
    }

    internal void Return(decimal quantity)
    {
        RequireReturned(this, quantity);
        ReturnedQuantity = quantity;
    }
}
