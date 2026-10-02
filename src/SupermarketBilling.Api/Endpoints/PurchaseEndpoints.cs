using Microsoft.AspNetCore.Mvc;
using SupermarketBilling.Application.Common;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.Domain.Purchases;
using SupermarketBilling.Infrastructure.Purchases;

namespace SupermarketBilling.Api.Endpoints;

/// <summary>Suppliers, purchase settings, purchase orders, goods receipts and their attachments.</summary>
internal static class PurchaseEndpoints
{
    /// <summary>The largest attachment plus room for the multipart framing.</summary>
    private const long AttachmentUploadLimit = Attachment.MaxBytes + (512 * 1024);

    public static IEndpointRouteBuilder MapPurchaseEndpoints(this IEndpointRouteBuilder routes)
    {
        var business = routes.MapGroup("/api/v1/businesses/{businessId:guid}").WithTags("Purchases");

        business.MapGet("/suppliers", (Guid businessId, string? search, SupplierService s, CancellationToken ct) => s.ListAsync(businessId, search, ct));
        business.MapPost("/suppliers", async (Guid businessId, CreateSupplierRequest r, SupplierService s, CancellationToken ct) =>
            Results.Created(string.Empty, await s.CreateAsync(businessId, r, ct).ConfigureAwait(false)));
        business.MapPut("/suppliers/{supplierId:guid}", (Guid businessId, Guid supplierId, UpdateSupplierRequest r, SupplierService s, CancellationToken ct) =>
            s.UpdateAsync(businessId, supplierId, r, ct));

        business.MapGet("/purchase-settings", (Guid businessId, SupplierService s, CancellationToken ct) => s.SettingsAsync(businessId, ct));
        business.MapPut("/purchase-settings", (Guid businessId, PurchaseSettingsDto r, SupplierService s, CancellationToken ct) => s.UpdateSettingsAsync(businessId, r, ct))
            .WithSummary("Cost-change thresholds (reason, approval) and whether loss-leader prices are allowed.");

        business.MapGet("/grns", (Guid businessId, Guid storeId, string? status, GrnService s, CancellationToken ct) => s.ListAsync(businessId, storeId, status, ct));
        business.MapPost("/grns/preview", (Guid businessId, GrnRequest r, GrnService s, CancellationToken ct) => s.PreviewAsync(businessId, r, ct))
            .WithSummary("Computes a goods receipt (taxes, expense allocation, landed costs) and lists what stops it from being saved.");
        business.MapPost("/grns", async (Guid businessId, GrnRequest r, GrnService s, CancellationToken ct) =>
            {
                var grn = await s.CreateAsync(businessId, r, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/businesses/{businessId}/grns/{grn.Id}", grn);
            })
            .WithSummary("Saves a goods receipt: stock goes in now, or after a manager approves a large cost change or a loss-leader price.");
        business.MapGet("/grns/{grnId:guid}", (Guid businessId, Guid grnId, GrnService s, CancellationToken ct) => s.GetAsync(businessId, grnId, ct));

        business.MapGet("/grns/{grnId:guid}/attachments", (Guid businessId, Guid grnId, AttachmentService s, CancellationToken ct) => s.ListForGrnAsync(businessId, grnId, ct));
        business.MapPost("/grns/{grnId:guid}/attachments", async (Guid businessId, Guid grnId, HttpRequest request, AttachmentService s, CancellationToken ct) =>
            {
                if (!request.HasFormContentType)
                {
                    throw AppException.Validation("attachment.form_required", "Send the file as multipart form data in a field named 'file'.");
                }

                var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
                var file = form.Files.GetFile("file") ?? throw AppException.Validation("attachment.file_required", "Choose a file to attach.");
                await using var content = file.OpenReadStream();
                return Results.Created(string.Empty, await s.AddToGrnAsync(businessId, grnId, file.FileName, content, ct).ConfigureAwait(false));
            })
            .WithMetadata(new RequestSizeLimitAttribute(AttachmentUploadLimit))
            .WithSummary("Attaches a PDF, JPEG or PNG (at most 10 MB, recognised by content) to a goods receipt, such as the scanned supplier invoice.");

        // Always a download (Content-Disposition: attachment), under the API's no-content CSP and nosniff.
        business.MapGet("/grns/{grnId:guid}/attachments/{attachmentId:guid}", async (Guid businessId, Guid grnId, Guid attachmentId, AttachmentService s, CancellationToken ct) =>
            {
                var (content, contentType, fileName) = await s.DownloadAsync(businessId, grnId, attachmentId, ct).ConfigureAwait(false);
                return Results.File(content, contentType, fileName);
            });

        business.MapGet("/grns/{grnId:guid}/returnable", (Guid businessId, Guid grnId, PurchaseReturnService s, CancellationToken ct) =>
                s.ReturnableAsync(businessId, grnId, ct))
            .WithSummary("What can still be returned to the supplier from each line of a posted receipt.");
        business.MapGet("/purchase-returns", (Guid businessId, Guid storeId, Guid? supplierId, PurchaseReturnService s, CancellationToken ct) =>
            s.ListAsync(businessId, storeId, supplierId, ct));
        business.MapPost("/purchase-returns/preview", (Guid businessId, PurchaseReturnRequest r, PurchaseReturnService s, CancellationToken ct) =>
            s.PreviewAsync(businessId, r, ct));
        business.MapPost("/purchase-returns", async (Guid businessId, PurchaseReturnRequest r, PurchaseReturnService s, CancellationToken ct) =>
            {
                var note = await s.CreateAsync(businessId, r, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/businesses/{businessId}/purchase-returns/{note.Id}", note);
            })
            .WithSummary("Sends goods back against a posted receipt: a debit note; stock goes out and the amount is deducted from what is owed to the supplier.");
        business.MapGet("/purchase-returns/{returnId:guid}", (Guid businessId, Guid returnId, PurchaseReturnService s, CancellationToken ct) =>
            s.GetAsync(businessId, returnId, ct));
        business.MapGet("/purchase-returns/{returnId:guid}/pdf", async (Guid businessId, Guid returnId, PurchaseReturnService s, CancellationToken ct) =>
        {
            var (content, fileName) = await s.PdfAsync(businessId, returnId, ct).ConfigureAwait(false);
            return Results.File(content, "application/pdf", fileName);
        });

        business.MapGet("/purchase-orders", (Guid businessId, Guid storeId, string? status, PurchaseOrderService s, CancellationToken ct) =>
            s.ListAsync(businessId, storeId, status, ct));
        business.MapPost("/purchase-orders", async (Guid businessId, CreatePurchaseOrderRequest r, PurchaseOrderService s, CancellationToken ct) =>
            {
                var order = await s.CreateAsync(businessId, r, ct).ConfigureAwait(false);
                return Results.Created($"/api/v1/businesses/{businessId}/purchase-orders/{order.Id}", order);
            });
        business.MapGet("/purchase-orders/{orderId:guid}", (Guid businessId, Guid orderId, PurchaseOrderService s, CancellationToken ct) => s.GetAsync(businessId, orderId, ct));
        business.MapPost("/purchase-orders/{orderId:guid}/close", (Guid businessId, Guid orderId, PurchaseOrderService s, CancellationToken ct) =>
                s.CloseAsync(businessId, orderId, cancel: false, ct))
            .WithSummary("No more goods are expected against the order.");
        business.MapPost("/purchase-orders/{orderId:guid}/cancel", (Guid businessId, Guid orderId, PurchaseOrderService s, CancellationToken ct) =>
                s.CloseAsync(businessId, orderId, cancel: true, ct))
            .WithSummary("Cancels an order nothing was received against.");
        return routes;
    }
}
