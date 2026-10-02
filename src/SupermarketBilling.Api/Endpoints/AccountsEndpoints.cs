using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Infrastructure.Accounts;
using SupermarketBilling.Infrastructure.Purchases;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Debtors, supplier and debtor accounts (statements, open items, opening balances, corrections) and supplier payments.</summary>
internal static class AccountsEndpoints
{
    public static IEndpointRouteBuilder MapAccountsEndpoints(this IEndpointRouteBuilder routes)
    {
        var business = routes.MapGroup("/api/v1/businesses/{businessId:guid}").WithTags("Accounts");

        business.MapGet("/debtors", (Guid businessId, string? search, string? status, DebtorService s, CancellationToken ct) => s.ListAsync(businessId, search, status, ct));
        business.MapPost("/debtors", async (Guid businessId, CreateDebtorRequest r, DebtorService s, CancellationToken ct) =>
        {
            var debtor = await s.CreateAsync(businessId, r, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/businesses/{businessId}/debtors/{debtor.Id}", debtor);
        });
        business.MapGet("/debtors/{debtorId:guid}", (Guid businessId, Guid debtorId, DebtorService s, CancellationToken ct) => s.GetAsync(businessId, debtorId, ct));
        business.MapPut("/debtors/{debtorId:guid}", (Guid businessId, Guid debtorId, UpdateDebtorRequest r, DebtorService s, CancellationToken ct) =>
            s.UpdateAsync(businessId, debtorId, r, ct));

        business.MapGet("/suppliers/{supplierId:guid}", (Guid businessId, Guid supplierId, SupplierService s, CancellationToken ct) => s.GetAsync(businessId, supplierId, ct));

        MapAccount(business, "suppliers", "supplierId", PartyTypes.Supplier);
        MapAccount(business, "debtors", "debtorId", PartyTypes.Debtor);

        business.MapGet("/supplier-payments", (Guid businessId, Guid? storeId, Guid? supplierId, SupplierPaymentService s, CancellationToken ct) =>
            s.ListAsync(businessId, storeId, supplierId, ct));
        business.MapPost("/supplier-payments", async (Guid businessId, SupplierPaymentRequest r, SupplierPaymentService s, CancellationToken ct) =>
            {
                var payment = await s.CreateAsync(businessId, r, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/businesses/{businessId}/supplier-payments/{payment.Id}", payment);
            })
            .WithSummary("Records a payment to a supplier and applies it to the bills named, or the oldest due first; the rest stays as an advance.");
        business.MapGet("/supplier-payments/{paymentId:guid}", (Guid businessId, Guid paymentId, SupplierPaymentService s, CancellationToken ct) =>
            s.GetAsync(businessId, paymentId, ct));
        return routes;
    }

    private static void MapAccount(RouteGroupBuilder business, string collection, string idName, string partyType)
    {
        var account = business.MapGroup($"/{collection}/{{{idName}:guid}}");
        Guid Id(HttpContext context) => Guid.Parse((string)context.Request.RouteValues[idName]!);

        account.MapGet("/statement", (Guid businessId, DateOnly? from, DateOnly? to, HttpContext context, PartyAccountService s, CancellationToken ct) =>
                s.StatementAsync(partyType, businessId, Id(context), from, to, ct))
            .WithSummary("The account's entries with running balance (and what remains of each), optionally for a period.");
        account.MapGet("/open-items", (Guid businessId, HttpContext context, PartyAccountService s, CancellationToken ct) =>
                s.OpenItemsAsync(partyType, businessId, Id(context), ct))
            .WithSummary("Unpaid charges, unapplied payments and ageing by days past due.");
        account.MapPost("/opening-balance", (Guid businessId, OpeningBalanceRequest r, HttpContext context, PartyAccountService s, CancellationToken ct) =>
                s.SetOpeningBalanceAsync(partyType, businessId, Id(context), r, ct))
            .WithSummary("The balance brought forward; only as the account's first entry.");
        account.MapPost("/adjustments", (Guid businessId, LedgerAdjustmentRequest r, HttpContext context, PartyAccountService s, CancellationToken ct) =>
                s.RequestAdjustmentAsync(partyType, businessId, Id(context), r, ct))
            .WithSummary("Asks for a correction to the account; it is posted once another authorised person approves it.");
        account.MapPost("/apply-payments", (Guid businessId, HttpContext context, PartyAccountService s, CancellationToken ct) =>
                s.ApplyUnappliedAsync(partyType, businessId, Id(context), ct))
            .WithSummary("Applies unapplied payments and advances to open charges, oldest first.");
    }
}
