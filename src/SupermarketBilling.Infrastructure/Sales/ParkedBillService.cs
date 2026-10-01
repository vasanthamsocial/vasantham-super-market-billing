using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Application.Security;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Sales;
using SupermarketBilling.Infrastructure.Auditing;
using SupermarketBilling.Infrastructure.Persistence;

namespace SupermarketBilling.Infrastructure.Sales;

/// <summary>Bills put aside at a counter and picked up again. Kept on the server, so a refresh or a crash loses nothing.</summary>
public sealed class ParkedBillService(
    SupermarketBillingDbContext db,
    CounterService counters,
    AuditRecorder audit,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ParkedBillDto> ParkAsync(string? deviceToken, ParkBillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Cart);
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        if (await db.ParkedBills.CountAsync(p => p.CounterId == pos.Counter.Id, cancellationToken).ConfigureAwait(false) >= ParkedBill.MaxPerCounter)
        {
            throw AppException.Conflict("parked.too_many", $"This counter already has {ParkedBill.MaxPerCounter} parked bills. Finish or clear some first.");
        }

        ParkedBill bill;
        try
        {
            bill = ParkedBill.Park(pos.Counter.BusinessId, pos.Counter.Id, currentUser.UserId, request.Label, request.Cart.Lines?.Count ?? 0,
                JsonSerializer.Serialize(request.Cart, Json), clock.GetUtcNow());
        }
        catch (DomainException e)
        {
            throw AppException.Validation(e.Code, e.Message);
        }

        db.ParkedBills.Add(bill);
        audit.Record("pos.bill_parked", "parked_bill", bill.Id, pos.Counter.BusinessId, pos.Counter.StoreId,
            details: new { counter = pos.Counter.Code, bill.Label, items = bill.ItemCount });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return (await ListAsync(deviceToken, cancellationToken).ConfigureAwait(false)).First(p => p.Id == bill.Id);
    }

    public async Task<IReadOnlyList<ParkedBillDto>> ListAsync(string? deviceToken, CancellationToken cancellationToken)
    {
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        return await (
                from p in db.ParkedBills.AsNoTracking()
                join u in db.Users.AsNoTracking() on p.ParkedByUserId equals u.Id
                where p.CounterId == pos.Counter.Id
                orderby p.ParkedAtUtc
                select new ParkedBillDto(p.Id, p.Label, p.ItemCount, u.DisplayName, p.ParkedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the bill back into the cart: returns it and removes it, once (a second retrieve finds nothing).</summary>
    public async Task<CartRequest> RetrieveAsync(string? deviceToken, Guid parkedBillId, CancellationToken cancellationToken)
    {
        var pos = await counters.RequireDeviceAsync(deviceToken, cancellationToken).ConfigureAwait(false);
        var removed = await db.ParkedBills.Where(p => p.Id == parkedBillId && p.CounterId == pos.Counter.Id)
            .Select(p => p.CartJson).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (removed.Count == 0 || await db.ParkedBills.Where(p => p.Id == parkedBillId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw AppException.NotFound("Parked bill");
        }

        audit.Record("pos.bill_retrieved", "parked_bill", parkedBillId, pos.Counter.BusinessId, pos.Counter.StoreId, details: new { counter = pos.Counter.Code });
        await db.SaveChangesCheckedAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<CartRequest>(removed[0], Json) ?? throw AppException.NotFound("Parked bill");
    }
}
