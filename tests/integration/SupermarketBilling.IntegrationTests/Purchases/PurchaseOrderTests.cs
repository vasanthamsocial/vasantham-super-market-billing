using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Npgsql;
using SupermarketBilling.Application.Contracts;
using SupermarketBilling.IntegrationTests.Catalog;
using SupermarketBilling.IntegrationTests.Infrastructure;
using SupermarketBilling.IntegrationTests.Inventory;

namespace SupermarketBilling.IntegrationTests.Purchases;

internal static class Orders
{
    public static async Task<PurchaseOrderDto> PlaceAsync(TestClient client, Guid businessId, Guid storeId, Guid supplierId, params PurchaseOrderLineRequest[] lines)
    {
        var response = await client.PostJsonAsync($"/api/v1/businesses/{businessId}/purchase-orders",
            new CreatePurchaseOrderRequest(storeId, supplierId, new DateOnly(2026, 10, 9), lines, "Weekly order"));
        await response.EnsureSuccessWithBodyAsync();
        return (await response.Content.ReadFromJsonAsync<PurchaseOrderDto>(TestClient.Json))!;
    }

    public static Task<PurchaseOrderDto> GetAsync(TestClient client, Guid businessId, Guid orderId) =>
        client.GetJsonAsync<PurchaseOrderDto>($"/api/v1/businesses/{businessId}/purchase-orders/{orderId}");

    public static Task<HttpResponseMessage> UploadAsync(TestClient client, Guid businessId, Guid grnId, string fileName, byte[] content)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return client.Http.PostAsync(new Uri($"/api/v1/businesses/{businessId}/grns/{grnId}/attachments", UriKind.Relative), form);
    }
}

[Collection(ApiTestGroup.Name)]
public sealed class PurchaseOrderTests(ApiFactory factory)
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% scanned supplier invoice\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private Guid Business => factory.BusinessId;

    private Guid Store => factory.MainStoreId;

    [Fact]
    public async Task Receipts_against_an_order_bring_in_only_what_is_outstanding_and_close_it_when_complete()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var rice = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var dal = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var salt = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        var other = await Purchasing.SupplierAsync(owner, Business);

        var order = await Orders.PlaceAsync(owner, Business, Store, supplier.Id, new PurchaseOrderLineRequest(rice, 10, 40m), new PurchaseOrderLineRequest(dal, 5));
        Assert.Matches(@"/PO/\d{6}$", order.Number);
        Assert.Equal(("OPEN", "NOT_RECEIVED", 10m), (order.Status, order.Progress, order.Lines[0].Outstanding));

        // Not on the order, more than ordered, another supplier: refused with the reason.
        var notOnOrder = await Purchasing.PreviewAsync(owner, Business,
            Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(salt, 1, 10m)) with { PurchaseOrderId = order.Id });
        Assert.Contains(notOnOrder.Issues, i => i.Code == "grn.not_on_order" && i.LineNumber == 1);
        var tooMuch = Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(rice, 7, 40m), new GrnLineRequest(rice, 4, 40m)) with { PurchaseOrderId = order.Id };
        Assert.Contains((await Purchasing.PreviewAsync(owner, Business, tooMuch)).Issues, i => i.Code == "grn.over_receipt");
        Assert.Equal("grn.over_receipt", await (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/grns", tooMuch)).ProblemCodeAsync());
        var wrongSupplier = await Purchasing.PreviewAsync(owner, Business,
            Purchasing.Request(Store, other.Id, "UNREGISTERED", new GrnLineRequest(rice, 1, 40m)) with { PurchaseOrderId = order.Id });
        Assert.Contains(wrongSupplier.Issues, i => i.Code == "grn.order_mismatch");

        // Part delivery.
        var first = await Purchasing.SaveAsync(owner, Business,
            Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(rice, 6, 40m)) with { PurchaseOrderId = order.Id });
        Assert.Equal(order.Number, first.PurchaseOrderNumber);
        order = await Orders.GetAsync(owner, Business, order.Id);
        Assert.Equal(("OPEN", "PARTLY_RECEIVED", 6m, 4m), (order.Status, order.Progress, order.Lines[0].Received, order.Lines[0].Outstanding));
        Assert.Equal([first.Number], order.ReceiptNumbers);
        Assert.Equal("purchase_order.received", await (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/purchase-orders/{order.Id}/cancel", new { })).ProblemCodeAsync());

        // The rest arrives (free goods do not count against the order): the order closes itself.
        await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED",
            new GrnLineRequest(rice, 4, 40m), new GrnLineRequest(dal, 5, 80m, FreeQuantity: 1)) with { PurchaseOrderId = order.Id });
        order = await Orders.GetAsync(owner, Business, order.Id);
        Assert.Equal(("CLOSED", "RECEIVED"), (order.Status, order.Progress));
        var late = await Purchasing.PreviewAsync(owner, Business,
            Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(rice, 1, 40m)) with { PurchaseOrderId = order.Id });
        Assert.Contains(late.Issues, i => i.Code == "grn.order_not_open");

        // An order nothing arrived against can be cancelled; one with receipts only closed.
        var unwanted = await Orders.PlaceAsync(owner, Business, Store, supplier.Id, new PurchaseOrderLineRequest(salt, 3));
        (await owner.PostJsonAsync($"/api/v1/businesses/{Business}/purchase-orders/{unwanted.Id}/cancel", new { })).EnsureSuccessStatusCode();
        Assert.Equal("CANCELLED", (await Orders.GetAsync(owner, Business, unwanted.Id)).Status);
        var open = await owner.GetJsonAsync<List<PurchaseOrderDto>>($"/api/v1/businesses/{Business}/purchase-orders?storeId={Store}&status=OPEN");
        Assert.DoesNotContain(open, o => o.Id == unwanted.Id || o.Id == order.Id);
    }

    [Fact]
    public async Task Concurrent_receipts_against_one_order_cannot_together_exceed_it()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var pack = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        var order = await Orders.PlaceAsync(owner, Business, Store, supplier.Id, new PurchaseOrderLineRequest(pack, 10));
        GrnRequest Receipt() => Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 4, 10m)) with { PurchaseOrderId = order.Id };

        // Warm up the receipt path and open enough connections, so the requests below really overlap.
        await Purchasing.PreviewAsync(owner, Business, Receipt());
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => owner.GetAsync($"/api/v1/businesses/{Business}/purchase-orders/{order.Id}")));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.PostJsonAsync($"/api/v1/businesses/{Business}/grns", Receipt())));
        var saved = 0;
        foreach (var response in responses)
        {
            if (response.IsSuccessStatusCode)
            {
                saved++;
            }
            else
            {
                Assert.Equal("grn.over_receipt", await response.ProblemCodeAsync());
            }
        }

        Assert.Equal(2, saved); // 4 + 4 of 10; a third would make 12
        order = await Orders.GetAsync(owner, Business, order.Id);
        Assert.Equal((8m, 2m, "OPEN"), (order.Lines[0].Received, order.Lines[0].Outstanding, order.Status));
    }

    [Fact]
    public async Task Supplier_invoice_rates_and_new_selling_prices_are_taken_from_the_receipt_when_allowed()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var (_, pack) = await Sales.Pos.StockedProductAsync(owner, Business, Store, price: 50m, mrp: 60m, stock: 0, gst: 18);
        var registered = await Purchasing.SupplierAsync(owner, Business, SupermarketBilling.Domain.Tax.Gstin.Complete("33AABCR4444K1Z"));
        var unregistered = await Purchasing.SupplierAsync(owner, Business);

        // The invoice charged 12% where the catalogue says 18%: the invoice's rate is used.
        var taxed = await Purchasing.PreviewAsync(owner, Business,
            Purchasing.Request(Store, registered.Id, "GST_TAX_INVOICE", new GrnLineRequest(pack, 10, 30m, Mrp: 60m, GstRatePercent: 12m)));
        Assert.Equal((12m, 18m, 18m), (taxed.Lines[0].GstRatePercent, taxed.CgstTotal, taxed.SgstTotal));
        var noGst = await Purchasing.PreviewAsync(owner, Business,
            Purchasing.Request(Store, unregistered.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 30m, GstRatePercent: 5m)));
        Assert.Contains(noGst.Issues, i => i.Code == "grn.gst_not_charged");
        var invalid = await owner.PostJsonAsync($"/api/v1/businesses/{Business}/grns/preview",
            Purchasing.Request(Store, registered.Id, "GST_TAX_INVOICE", new GrnLineRequest(pack, 10, 30m, GstRatePercent: 101m)));
        Assert.Equal("grn.gst_rate_invalid", await invalid.ProblemCodeAsync());

        // A purchase operator cannot change selling prices from a receipt; the price may never exceed the MRP.
        var operatorUser = await factory.CreateSignedInUserAsync("purchase_operator", Store);
        using (var receiver = operatorUser.Client)
        {
            var refused = await Purchasing.PreviewAsync(receiver, Business,
                Purchasing.Request(Store, unregistered.Id, "UNREGISTERED", new GrnLineRequest(pack, 10, 30m, Mrp: 60m, SellingPrice: 55m, UpdateSellingPrice: true)));
            Assert.Contains(refused.Issues, i => i.Code == "grn.price_permission");
        }

        var issues = (await Purchasing.PreviewAsync(owner, Business, Purchasing.Request(Store, unregistered.Id, "UNREGISTERED",
            new GrnLineRequest(pack, 10, 30m, Mrp: 60m, SellingPrice: 65m, UpdateSellingPrice: true),
            new GrnLineRequest(pack, 1, 30m, Mrp: 60m, UpdateSellingPrice: true)))).Issues;
        Assert.Contains(issues, i => i.Code == "grn.selling_above_mrp" && i.LineNumber == 1);
        Assert.Contains(issues, i => i.Code == "grn.selling_price_required" && i.LineNumber == 2);

        // The owner's receipt sets the retail price when it posts.
        var grn = await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, unregistered.Id, "UNREGISTERED",
            new GrnLineRequest(pack, 10, 30m, Mrp: 60m, SellingPrice: 55m, UpdateSellingPrice: true)));
        Assert.True(grn.Lines[0].UpdateSellingPrice);
        Assert.Equal(55m, (await QuoteAsync(owner, pack)).UnitPriceInclusive);

        // A receipt waiting for approval changes the price only once approved.
        var pending = await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, unregistered.Id, "UNREGISTERED",
            new GrnLineRequest(pack, 10, 40m, Mrp: 60m, SellingPrice: 58m, UpdateSellingPrice: true, CostChangeReason: "Crop failure")));
        Assert.Equal("PENDING_APPROVAL", pending.Status);
        Assert.Equal(55m, (await QuoteAsync(owner, pack)).UnitPriceInclusive);
        using var approver = await factory.LoginAsync(ApiFactory.ApproverUsername, ApiFactory.DefaultUserPassword);
        factory.Clock.Advance(TimeSpan.FromMinutes(1)); // the newer price wins over the one from the earlier receipt
        (await approver.PostJsonAsync($"/api/v1/approvals/{pending.ApprovalRequestId}/approve", new ApprovalDecisionRequest("Checked"))).EnsureSuccessStatusCode();
        Assert.Equal(58m, (await QuoteAsync(owner, pack)).UnitPriceInclusive);
    }

    [Fact]
    public async Task Attachments_accept_only_real_pdf_and_images_and_download_safely()
    {
        using var owner = await factory.LoginAsync(ApiFactory.OwnerUsername, ApiFactory.OwnerPassword);
        var pack = (await CatalogTests.CreateProductAsync(owner, Business)).Variants.Single().Units.Single().Id;
        var supplier = await Purchasing.SupplierAsync(owner, Business);
        var grn = await Purchasing.SaveAsync(owner, Business, Purchasing.Request(Store, supplier.Id, "UNREGISTERED", new GrnLineRequest(pack, 1, 10m)));
        var basePath = $"/api/v1/businesses/{Business}/grns/{grn.Id}/attachments";

        var uploaded = await Orders.UploadAsync(owner, Business, grn.Id, @"..\..\scans/supplier invoice.pdf", Pdf);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var attachment = (await uploaded.Content.ReadFromJsonAsync<AttachmentDto>(TestClient.Json))!;
        Assert.Equal(("supplier invoice.pdf", "application/pdf", Pdf.LongLength), (attachment.FileName, attachment.ContentType, attachment.Size));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Pdf)), attachment.Sha256);

        // Recognised by content: an HTML page or an executable named .pdf, or a PNG named .pdf, is refused.
        Assert.Equal("attachment.type_invalid", await (await Orders.UploadAsync(owner, Business, grn.Id, "invoice.pdf", Encoding.ASCII.GetBytes("<html><script>alert(1)</script>"))).ProblemCodeAsync());
        Assert.Equal("attachment.type_invalid", await (await Orders.UploadAsync(owner, Business, grn.Id, "invoice.pdf", Encoding.ASCII.GetBytes("MZ\u0090\0\u0003"))).ProblemCodeAsync());
        Assert.Equal("attachment.extension_mismatch", await (await Orders.UploadAsync(owner, Business, grn.Id, "invoice.pdf", Png)).ProblemCodeAsync());
        Assert.Equal("attachment.file_required", await (await owner.Http.PostAsync(new Uri(basePath, UriKind.Relative), new MultipartFormDataContent { { new StringContent("x"), "other" } })).ProblemCodeAsync());

        // Over 10 MB: refused (by the size limit or by the reader, whichever sees it first).
        var tooBig = await Orders.UploadAsync(owner, Business, grn.Id, "big.pdf", [.. Pdf, .. new byte[10 * 1024 * 1024]]);
        Assert.True(tooBig.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode.ToString());

        // Download: always as a file, never rendered under the app's origin.
        var download = await owner.GetAsync($"{basePath}/{attachment.Id}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("default-src 'none'", download.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        // Viewing needs purchases.view; adding needs purchases.manage and the CSRF header; the file belongs to its receipt.
        var auditorUser = await factory.CreateSignedInUserAsync("auditor");
        using (var auditor = auditorUser.Client)
        {
            Assert.Single(await auditor.GetJsonAsync<List<AttachmentDto>>(basePath));
            Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync($"{basePath}/{attachment.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Orders.UploadAsync(auditor, Business, grn.Id, "x.pdf", Pdf)).StatusCode);
        }

        owner.Cookies.SendCsrfHeader = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await Orders.UploadAsync(owner, Business, grn.Id, "x.pdf", Pdf)).StatusCode);
        owner.Cookies.SendCsrfHeader = true;
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/businesses/{Business}/grns/{Guid.NewGuid()}/attachments/{attachment.Id}")).StatusCode);

        // The stored file can never change, and verification notices if it is tampered with.
        await using (var db = await factory.OpenAppConnectionAsync())
        {
            await using var command = new NpgsqlCommand("UPDATE attachments SET file_name = 'other.pdf' WHERE id = @id", db);
            command.Parameters.AddWithValue("id", attachment.Id);
            Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }

        var sql7 = await File.ReadAllTextAsync(Path.Combine(StockTests.RepoRoot(), "database", "verification", "007_purchase_orders.sql"));
        await using var admin = await TestDatabase.OpenAdminAsync(factory.DatabaseName);
        await using (var verify = new NpgsqlCommand(sql7, admin))
        {
            await verify.ExecuteNonQueryAsync();
        }

        await using var transaction = await admin.BeginTransactionAsync();
        await using (var tamper = new NpgsqlCommand(
                         "ALTER TABLE attachments DISABLE TRIGGER trg_attachments_no_update_delete; " +
                         "UPDATE attachments SET content = overlay(content placing '\\x58'::bytea from 10 for 1) WHERE id = @id; " +
                         "ALTER TABLE attachments ENABLE TRIGGER trg_attachments_no_update_delete;", admin, transaction))
        {
            tamper.Parameters.AddWithValue("id", attachment.Id);
            await tamper.ExecuteNonQueryAsync();
        }

        await using var again = new NpgsqlCommand(sql7, admin, transaction);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => again.ExecuteNonQueryAsync());
        Assert.Contains("attachments damaged", failure.MessageText, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }

    private Task<PriceQuoteDto> QuoteAsync(TestClient client, Guid pack) =>
        client.GetJsonAsync<PriceQuoteDto>($"/api/v1/businesses/{Business}/prices/quote?variantUnitId={pack}&storeId={Store}&mrp=60");
}
