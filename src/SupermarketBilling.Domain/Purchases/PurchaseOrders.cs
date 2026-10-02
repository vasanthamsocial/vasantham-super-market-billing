using System.Security.Cryptography;
using System.Text;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Inventory;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Purchases;

public static class PurchaseOrderStatus
{
    public const string Open = "OPEN";

    /// <summary>Closed by hand (no more goods expected), or after everything was received.</summary>
    public const string Closed = "CLOSED";

    public const string Cancelled = "CANCELLED";
}

/// <summary>What the business ordered from a supplier for a store. Receipts against it may not bring in more than is outstanding.</summary>
public sealed class PurchaseOrder : ITenantOwned
{
    private PurchaseOrder()
    {
        Number = Status = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid StoreId { get; private set; }

    public Guid SupplierId { get; private set; }

    public string Number { get; private set; }

    public long SequenceNumber { get; private set; }

    public DateOnly OrderDate { get; private set; }

    public DateOnly? ExpectedDate { get; private set; }

    public string Status { get; private set; }

    public string? Notes { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static PurchaseOrder Place(
        Guid businessId, Guid storeId, Guid supplierId, string number, long sequence, DateOnly orderDate, DateOnly? expectedDate, string? notes, Guid createdBy,
        DateTimeOffset now) =>
        expectedDate is { } expected && expected < orderDate
            ? throw new DomainException("purchase_order.expected_date_invalid", "Goods cannot be expected before the order date.")
            : new PurchaseOrder
            {
                Id = Guid.CreateVersion7(now),
                BusinessId = businessId,
                StoreId = storeId,
                SupplierId = supplierId,
                Number = number,
                SequenceNumber = sequence,
                OrderDate = orderDate,
                ExpectedDate = expectedDate,
                Status = PurchaseOrderStatus.Open,
                Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                CreatedByUserId = createdBy,
                CreatedAtUtc = now,
            };

    public void Close(DateTimeOffset now)
    {
        if (Status == PurchaseOrderStatus.Open)
        {
            Status = PurchaseOrderStatus.Closed;
            ClosedAtUtc = now;
        }
    }

    public void Cancel(bool anythingReceived, DateTimeOffset now)
    {
        if (Status != PurchaseOrderStatus.Open)
        {
            throw new DomainException("purchase_order.not_open", "Only an open order can be cancelled.");
        }

        if (anythingReceived)
        {
            throw new DomainException("purchase_order.received", "Goods have been received against this order: close it instead of cancelling.");
        }

        Status = PurchaseOrderStatus.Cancelled;
        ClosedAtUtc = now;
    }
}

public sealed class PurchaseOrderLine : ITenantOwned
{
    private PurchaseOrderLine()
    {
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid PurchaseOrderId { get; private set; }

    public int LineNumber { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid VariantUnitId { get; private set; }

    /// <summary>Ordered quantity in the pack.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>The agreed basic rate per pack, if any (for comparing with the invoice).</summary>
    public decimal? Rate { get; private set; }

    public static PurchaseOrderLine Create(Guid businessId, Guid orderId, int lineNumber, Guid variantId, Guid variantUnitId, decimal quantity, decimal? rate, DateTimeOffset now) =>
        quantity <= 0 || StockMath.Quantity(quantity) != quantity
            ? throw new DomainException("purchase_order.quantity_invalid", "Each ordered quantity must be above zero (at most 3 decimals).")
            : rate is < 0
                ? throw new DomainException("purchase_order.rate_invalid", "A rate cannot be negative.")
                : new PurchaseOrderLine
                {
                    Id = SequentialGuid.Next(now),
                    BusinessId = businessId,
                    PurchaseOrderId = orderId,
                    LineNumber = lineNumber,
                    VariantId = variantId,
                    VariantUnitId = variantUnitId,
                    Quantity = quantity,
                    Rate = rate,
                };
}

/// <summary>
/// A file kept with a document (for example the scanned supplier invoice of a goods receipt). Only PDF, JPEG and PNG
/// files are accepted, recognised by their content rather than their name, up to 10 MB. Files are never changed.
/// </summary>
public sealed class Attachment : ITenantOwned
{
    public const int MaxBytes = 10 * 1024 * 1024;

    private Attachment()
    {
        OwnerType = FileName = ContentType = Sha256 = string.Empty;
        Content = [];
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string OwnerType { get; private set; }

    public Guid OwnerId { get; private set; }

    public string FileName { get; private set; }

    public string ContentType { get; private set; }

    public long Size { get; private set; }

    public string Sha256 { get; private set; }

    public byte[] Content { get; private set; }

    public Guid UploadedByUserId { get; private set; }

    public DateTimeOffset UploadedAtUtc { get; private set; }

    public static Attachment Create(Guid businessId, string ownerType, Guid ownerId, string? fileName, byte[] content, Guid uploadedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0 || content.Length > MaxBytes)
        {
            throw new DomainException("attachment.size_invalid", "A file must be between 1 byte and 10 MB.");
        }

        var (contentType, extensions) = Detect(content) ?? throw new DomainException("attachment.type_invalid", "Only PDF, JPEG and PNG files can be attached.");
        var name = SafeName(fileName, extensions[0]);
        if (!extensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
        {
            throw new DomainException("attachment.extension_mismatch", $"The file's name says '{Path.GetExtension(name)}' but its content is {contentType}.");
        }

        return new Attachment
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            OwnerType = ownerType,
            OwnerId = ownerId,
            FileName = name,
            ContentType = contentType,
            Size = content.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            Content = content,
            UploadedByUserId = uploadedBy,
            UploadedAtUtc = now,
        };
    }

    /// <summary>The file type from its first bytes.</summary>
    public static (string ContentType, string[] Extensions)? Detect(ReadOnlySpan<byte> content) =>
        content.StartsWith("%PDF-"u8) ? ("application/pdf", [".pdf"])
        : content.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) ? ("image/jpeg", [".jpg", ".jpeg"])
        : content.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? ("image/png", [".png"])
        : null;

    /// <summary>The name without any folder, with only safe characters, at most 100 long.</summary>
    public static string SafeName(string? fileName, string defaultExtension)
    {
        var baseName = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/').Split('/')[^1]);
        var cleaned = new StringBuilder();
        foreach (var c in baseName)
        {
            cleaned.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_');
        }

        var name = cleaned.ToString().Trim(' ', '.');
        if (name.Length == 0 || Path.GetFileNameWithoutExtension(name).Length == 0)
        {
            name = "attachment" + defaultExtension;
        }
        else if (Path.GetExtension(name).Length == 0)
        {
            name += defaultExtension;
        }

        return name.Length <= 100 ? name : name[..(100 - Path.GetExtension(name).Length)] + Path.GetExtension(name);
    }
}
