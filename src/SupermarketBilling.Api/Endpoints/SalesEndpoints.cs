using SupermarketBilling.Api.Security;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Infrastructure.Sales;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Counters, counter devices, POS billing and invoice lookup.</summary>
internal static class SalesEndpoints
{
    /// <summary>Marks this browser as an enrolled counter device. HttpOnly: page scripts cannot read or leak it.</summary>
    public const string DeviceCookie = "sb_counter_device";

    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder routes)
    {
        var counters = routes.MapGroup("/api/v1/businesses/{businessId:guid}/counters").WithTags("Counters");
        counters.MapGet("/", (Guid businessId, CounterService s, CancellationToken ct) => s.ListAsync(businessId, ct));
        counters.MapPost("/", async (Guid businessId, CreateCounterRequest r, CounterService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.CreateAsync(businessId, r, ct).ConfigureAwait(false)));
        counters.MapPut("/{counterId:guid}", (Guid businessId, Guid counterId, UpdateCounterRequest r, CounterService s, CancellationToken ct) =>
            s.UpdateAsync(businessId, counterId, r, ct));
        counters.MapGet("/{counterId:guid}/devices", (Guid businessId, Guid counterId, HttpContext http, CounterService s, CancellationToken ct) =>
            s.DevicesAsync(businessId, counterId, DeviceToken(http), ct));
        counters.MapPost("/{counterId:guid}/devices", async (Guid businessId, Guid counterId, EnrolDeviceRequest r, HttpContext http, CounterService s, CancellationToken ct) =>
            {
                var result = await s.EnrolAsync(businessId, counterId, r, DeviceToken(http), ct).ConfigureAwait(false);
                http.Response.Cookies.Append(DeviceCookie, result.DeviceToken, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = AuthCookies.Options(http).SecureCookies,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    IsEssential = true,
                    MaxAge = TimeSpan.FromDays(365),
                });
                return Results.Created(string.Empty, result.Device);
            })
            .WithSummary("Enrol this browser as a device of the counter (sets an HttpOnly device cookie).");
        counters.MapPost("/{counterId:guid}/devices/{deviceId:guid}/revoke", async (Guid businessId, Guid counterId, Guid deviceId, CounterService s, CancellationToken ct) =>
        {
            await s.RevokeDeviceAsync(businessId, counterId, deviceId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        var pos = routes.MapGroup("/api/v1/pos").WithTags("POS");
        pos.MapGet("/context", (HttpContext http, BillingService s, CancellationToken ct) => s.ContextAsync(DeviceToken(http), ct))
            .WithSummary("The counter this browser bills on, and what the signed-in cashier may do there.");
        pos.MapPost("/cart", (CartRequest r, HttpContext http, BillingService s, CancellationToken ct) => s.PriceAsync(DeviceToken(http), r, ct))
            .WithSummary("Price a cart without issuing it (the POS preview). The server's prices and taxes are final.");
        pos.MapPost("/supervisor-approvals", (SupervisorApprovalRequest r, HttpContext http, BillingService s, CancellationToken ct) =>
                s.ApproveAsync(DeviceToken(http), r, ct))
            .WithSummary("A supervisor approves a price override or discount at this counter with their own credentials.");
        pos.MapPost("/invoices", async (IssueInvoiceRequest r, HttpContext http, BillingService s, CancellationToken ct) =>
            {
                var invoice = await s.IssueAsync(DeviceToken(http), r, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/pos/invoices/{invoice.Id}", invoice);
            })
            .WithSummary("Issue the bill. Retrying with the same idempotency key returns the same invoice.");
        pos.MapGet("/invoices/{invoiceId:guid}", (Guid invoiceId, HttpContext http, BillingService s, CancellationToken ct) =>
            s.CounterInvoiceAsync(DeviceToken(http), invoiceId, ct));
        pos.MapGet("/invoices/{invoiceId:guid}/pdf", async (Guid invoiceId, HttpContext http, BillingService s, CancellationToken ct) =>
            Pdf(await s.CounterInvoicePdfAsync(DeviceToken(http), invoiceId, ct).ConfigureAwait(false)));
        pos.MapGet("/returns/invoice", (string number, HttpContext http, ReturnService s, CancellationToken ct) => s.FindInvoiceAsync(DeviceToken(http), number, ct))
            .WithSummary("Find an invoice of this store to return goods against, with what can still be returned.");
        pos.MapPost("/returns/preview", (ReturnPreviewRequest r, HttpContext http, ReturnService s, CancellationToken ct) => s.PreviewAsync(DeviceToken(http), r, ct));
        pos.MapPost("/returns", async (IssueReturnRequest r, HttpContext http, ReturnService s, CancellationToken ct) =>
            {
                var note = await s.IssueAsync(DeviceToken(http), r, ct).ConfigureAwait(false);
                return Results.Created(string.Empty, note);
            })
            .WithSummary("Issue a credit note: goods back into stock, money refunded or kept as store credit. Idempotent.");
        pos.MapGet("/credit-notes", (string number, HttpContext http, ReturnService s, CancellationToken ct) => s.CounterCreditNoteAsync(DeviceToken(http), number, ct))
            .WithSummary("A credit note by number, with its store credit left (for an exchange).");
        pos.MapGet("/parked", (HttpContext http, ParkedBillService s, CancellationToken ct) => s.ListAsync(DeviceToken(http), ct));
        pos.MapPost("/parked", async (ParkBillRequest r, HttpContext http, ParkedBillService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.ParkAsync(DeviceToken(http), r, ct).ConfigureAwait(false)));
        pos.MapPost("/parked/{parkedBillId:guid}/retrieve", (Guid parkedBillId, HttpContext http, ParkedBillService s, CancellationToken ct) =>
                s.RetrieveAsync(DeviceToken(http), parkedBillId, ct))
            .WithSummary("Takes a parked bill back into the cart (and removes it from the parked list).");

        var sales = routes.MapGroup("/api/v1/businesses/{businessId:guid}/sales").WithTags("Sales");
        sales.MapGet("/invoices", (Guid businessId, Guid storeId, DateOnly? date, string? search, BillingService s, CancellationToken ct) =>
            s.ListAsync(businessId, storeId, date, search, ct));
        sales.MapGet("/invoices/{invoiceId:guid}", (Guid businessId, Guid invoiceId, BillingService s, CancellationToken ct) =>
            s.GetAsync(businessId, invoiceId, ct));
        sales.MapGet("/returns", (Guid businessId, Guid storeId, DateOnly? date, ReturnService s, CancellationToken ct) => s.ListAsync(businessId, storeId, date, ct));
        sales.MapGet("/returns/{returnId:guid}", (Guid businessId, Guid returnId, ReturnService s, CancellationToken ct) => s.GetAsync(businessId, returnId, ct));
        sales.MapGet("/returns/{returnId:guid}/pdf", async (Guid businessId, Guid returnId, ReturnService s, CancellationToken ct) =>
            Pdf(await s.PdfAsync(businessId, returnId, ct).ConfigureAwait(false)));
        sales.MapGet("/invoices/{invoiceId:guid}/pdf", async (Guid businessId, Guid invoiceId, BillingService s, CancellationToken ct) =>
            Pdf(await s.InvoicePdfAsync(businessId, invoiceId, ct).ConfigureAwait(false)));

        return routes;
    }

    private static IResult Pdf((byte[] Content, string FileName) pdf) => Results.File(pdf.Content, "application/pdf", pdf.FileName);

    private static string? DeviceToken(HttpContext http) => http.Request.Cookies.TryGetValue(DeviceCookie, out var token) ? token : null;
}
