namespace Sankore.Modules.Integration.Features.Insurance.InformationSheets;

using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.Retention;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

/// <summary>
/// The information sheet (insurance lot 2): the insurer's PDF per product, versioned, and the
/// proof that a client received it before signing. The insurer holds the obligation (art. 6) and is
/// the source of truth for the content; the distributor shows it, hands it over and proves it.
/// </summary>
internal static class InformationSheetEndpoints
{
    internal const string SheetMissing = "INSURANCE_INFORMATION_SHEET_MISSING";
    internal const string NotPdf = "INSURANCE_INFORMATION_SHEET_NOT_PDF";
    internal const string TooLarge = "INSURANCE_INFORMATION_SHEET_TOO_LARGE";
    internal const string HoldingNotFound = "INSURANCE_HOLDING_NOT_FOUND";
    internal const string ProductNotForHolding = "INSURANCE_PRODUCT_NOT_FOR_HOLDING";

    private const long MaxBytes = 15L * 1024 * 1024;

    internal static IEndpointRouteBuilder MapInformationSheets(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("integration/insurance")
            .WithTags("Integration")
            .RequireTenantModule(TenantModules.Insurance);

        group.MapPost("products/{productId:guid}/information-sheet", Upload)
            .WithName("UploadInsuranceInformationSheet")
            .WithSummary("Upload a new version of the product's information sheet (the insurer's PDF)")
            .WithDescription(
                "PDF only, 15 MB at most. Becomes the version every new delivery hands over; earlier "
                + "versions stay readable, deliveries name theirs. A product with no sheet is not "
                + "offerable. Requires permission: Ins.Product.Manage.")
            .RequireAuthorization(Permissions.CanManageInsuranceProduct.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<InformationSheetDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .DisableAntiforgery()
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("products/{productId:guid}/information-sheets", ListVersions)
            .WithName("ListInsuranceInformationSheets")
            .WithSummary("Every version of the product's information sheet, newest first")
            .RequireAuthorization(Permissions.CanViewInsurancePolicy.Code)
            .Produces<IReadOnlyList<InformationSheetDto>>(StatusCodes.Status200OK)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("information-sheets/{sheetId:guid}/content", Content)
            .WithName("GetInsuranceInformationSheetContent")
            .WithSummary("The PDF of one version")
            .RequireAuthorization(Permissions.CanViewInsurancePolicy.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("products/{productId:guid}/exclusions", SetExclusions)
            .WithName("SetInsuranceProductExclusions")
            .WithSummary("The exclusions shown on screen next to the sheet, one per line")
            .RequireAuthorization(Permissions.CanManageInsuranceProduct.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("holdings/{holdingId:guid}/information-sheet-deliveries", Deliver)
            .WithName("RecordInformationSheetDelivery")
            .WithSummary("Record that the client received the product's current information sheet")
            .WithDescription(
                "Before the signature: a holding on an insurance line cannot be signed without a "
                + "delivery. Records the version, its fingerprint, the channel and the client's "
                + "acknowledgement. Within the agent's perimeter. Requires permission: Ins.Policy.Subscribe.")
            .RequireAuthorization(Permissions.CanSubscribeInsurancePolicy.Code)
            .Produces<InformationSheetDeliveryDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("holdings/{holdingId:guid}/information-sheet-deliveries", ListDeliveries)
            .WithName("ListInformationSheetDeliveries")
            .WithSummary("The information sheets handed over for a holding")
            .RequireAuthorization(Permissions.CanViewInsurancePolicy.Code)
            .Produces<IReadOnlyList<InformationSheetDeliveryDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Upload(
        Guid productId, IFormFile file, IntegrationDbContext db, IFileStore files,
        ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        if (file is null || file.Length == 0 || !string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = NotPdf });

        if (file.Length > MaxBytes)
            return Results.BadRequest(new { error = TooLarge });

        var product = await db.InsuranceProducts.AsTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
            return Results.NotFound(new { error = "INSURANCE_PRODUCT_NOT_FOUND" });

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        // The fingerprint is what a delivery proves was handed over.
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
        buffer.Position = 0;
        var reference = await files.StoreAsync(buffer, file.FileName, ct);

        var version = await db.InformationSheets.Where(s => s.ProductId == productId).MaxAsync(s => (int?)s.Version, ct) ?? 0;
        var sheet = ProductInformationSheet.Create(
            user.TenantId, productId, version + 1, reference, file.FileName, "application/pdf",
            file.Length, sha256, user.Id, clock);

        db.InformationSheets.Add(sheet);
        product.AttachInformationSheet(sheet.Id, sheet.Version, user.Id, clock);
        await db.SaveChangesAsync(ct);

        return Results.Created($"integration/insurance/information-sheets/{sheet.Id}/content", InformationSheetDto.From(sheet, true));
    }

    private static async Task<IResult> ListVersions(Guid productId, IntegrationDbContext db, CancellationToken ct)
    {
        var current = await db.InsuranceProducts.Where(p => p.Id == productId)
            .Select(p => p.CurrentInformationSheetId).FirstOrDefaultAsync(ct);

        var sheets = await db.InformationSheets.Where(s => s.ProductId == productId)
            .OrderByDescending(s => s.Version).ToListAsync(ct);

        return Results.Ok(sheets.Select(s => InformationSheetDto.From(s, s.Id == current)).ToList());
    }

    private static async Task<IResult> Content(Guid sheetId, IntegrationDbContext db, IFileStore files, CancellationToken ct)
    {
        var sheet = await db.InformationSheets.FirstOrDefaultAsync(s => s.Id == sheetId, ct);
        if (sheet is null)
            return Results.NotFound(new { error = SheetMissing });

        var stream = await files.ReadAsync(sheet.FileReference, ct);
        return Results.File(stream, "application/pdf", sheet.FileName);
    }

    private static async Task<IResult> SetExclusions(
        Guid productId, SetExclusionsRequest body, IntegrationDbContext db, ICurrentUser user,
        TimeProvider clock, CancellationToken ct)
    {
        var product = await db.InsuranceProducts.AsTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null)
            return Results.NotFound(new { error = "INSURANCE_PRODUCT_NOT_FOUND" });

        product.SetExclusions(body.Exclusions, user.Id, clock);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Deliver(
        Guid holdingId,
        RecordDeliveryRequest body,
        IntegrationDbContext db,
        ICustomersModule customers,
        ICurrentUser user,
        IOptions<InsuranceRetentionOptions> retention,
        TimeProvider clock,
        CancellationToken ct)
    {
        var holding = await customers.GetHoldingForSyncAsync(user.TenantId, holdingId, ct);

        if (holding is null || !await customers.CanActorAccessClientAsync(user.TenantId, holding.ClientId, user.Id, ct))
            return Results.NotFound(new { error = HoldingNotFound });

        var product = await db.InsuranceProducts.FirstOrDefaultAsync(p => p.Id == body.ProductId, ct);
        if (product is null)
            return Results.NotFound(new { error = "INSURANCE_PRODUCT_NOT_FOUND" });

        if (!string.Equals(product.CrmProductCode?.Trim(), holding.ProductCode.Trim(), StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = ProductNotForHolding });

        if (product.CurrentInformationSheetId is not { } sheetId)
            return Results.Conflict(new { error = SheetMissing });

        var sheet = await db.InformationSheets.FirstAsync(s => s.Id == sheetId, ct);

        InformationSheetDelivery delivery;
        try
        {
            delivery = InformationSheetDelivery.Record(
                user.TenantId, holding.HoldingId, holding.ClientId, sheet, body.Channel,
                body.AcknowledgementReference, user.Id, clock,
                clock.GetUtcNow().AddYears(retention.Value.ResolvedYears));
        }
        catch (DomainException ex)
        {
            return Results.BadRequest(new { error = $"INSURANCE_INFORMATION_SHEET_DELIVERY_INVALID: {ex.Message}" });
        }

        db.InformationSheetDeliveries.Add(delivery);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"integration/insurance/holdings/{holdingId}/information-sheet-deliveries",
            InformationSheetDeliveryDto.From(delivery));
    }

    private static async Task<IResult> ListDeliveries(
        Guid holdingId, IntegrationDbContext db, ICustomersModule customers, ICurrentUser user, CancellationToken ct)
    {
        var holding = await customers.GetHoldingForSyncAsync(user.TenantId, holdingId, ct);

        if (holding is null || !await customers.CanActorAccessClientAsync(user.TenantId, holding.ClientId, user.Id, ct))
            return Results.NotFound(new { error = HoldingNotFound });

        var deliveries = await db.InformationSheetDeliveries
            .Where(d => d.HoldingId == holdingId)
            .OrderByDescending(d => d.DeliveredAt)
            .ToListAsync(ct);

        return Results.Ok(deliveries.Select(InformationSheetDeliveryDto.From).ToList());
    }
}

public sealed record SetExclusionsRequest(string? Exclusions);

public sealed record RecordDeliveryRequest(
    Guid ProductId,
    InformationSheetChannel Channel,
    string AcknowledgementReference);

public sealed record InformationSheetDto(
    Guid Id, Guid ProductId, int Version, string FileName, long SizeBytes, string Sha256,
    DateTimeOffset UploadedAt, bool IsCurrent)
{
    internal static InformationSheetDto From(ProductInformationSheet s, bool isCurrent)
        => new(s.Id, s.ProductId, s.Version, s.FileName, s.SizeBytes, s.Sha256, s.UploadedAt, isCurrent);
}

public sealed record InformationSheetDeliveryDto(
    Guid Id, Guid HoldingId, Guid ProductId, Guid SheetId, int SheetVersion, string SheetSha256,
    string Channel, string AcknowledgementReference, DateTimeOffset DeliveredAt, Guid DeliveredBy)
{
    internal static InformationSheetDeliveryDto From(InformationSheetDelivery d)
        => new(d.Id, d.HoldingId, d.ProductId, d.SheetId, d.SheetVersion, d.SheetSha256,
            d.Channel.ToString(), d.AcknowledgementReference, d.DeliveredAt, d.DeliveredBy);
}
