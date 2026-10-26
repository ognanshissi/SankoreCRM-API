namespace Sankore.Modules.Customers.Features.Documents;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Documents.Files;
using Sankore.Modules.Customers.Features.Documents.ListDocuments;
using Sankore.Modules.Customers.Infrastructure.Documents;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The client's documents — what they gave and what they were given, from every module that keeps
/// some (product owner, 2026-10-26).
/// </summary>
public static class DocumentsEndpoints
{
    public static IEndpointRouteBuilder MapDocumentsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("clients/{clientId:guid}/documents", List)
            .WithTags("Client Documents")
            .WithName("ListClientDocuments")
            .WithSummary("Every document the client gave or received")
            .WithDescription(
                "KYC images, insurance documents (information sheets handed over, the insurer's "
                + "policy documents, claim pieces, consent proofs), project and task attachments, "
                + "WhatsApp media — newest first. Every line is listed; a line the caller may not "
                + "open (canOpen false) comes without its file name and its content path. Each "
                + "contentPath is the source's own route, which keeps its permission and access "
                + "log. A module that cannot be read is named in unavailableSources.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<ClientDocumentsDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPost("clients/{clientId:guid}/documents", Upload)
            .WithTags("Client Documents")
            .WithName("UploadClientDocument")
            .WithSummary("File a document on the client record (PDF, image, Office, text; 10 MB), stored encrypted")
            .WithDescription(
                "Form fields: file, kind (Correspondence | Proof | Other), title, direction "
                + "(Collected = received from the client | Delivered = handed to them), documentDate "
                + "(yyyy-MM-dd, the document's own date). The type is read from the file's bytes. "
                + "503 CLIENT_DOCUMENTS_UNAVAILABLE when no encryption key is configured.")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .DisableAntiforgery()
            .Produces<UploadClientDocumentResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapGet("clients/{clientId:guid}/documents/{documentId:guid}/content", Content)
            .WithTags("Client Documents")
            .WithName("GetClientDocumentContent")
            .WithSummary("Open a document filed on the client record")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        // A POST on a noun, like a holding's closure: the removal carries a reason, and it is
        // logical — the row and the file stay, the list stops showing them.
        app.MapPost("clients/{clientId:guid}/documents/{documentId:guid}/removal", Remove)
            .WithTags("Client Documents")
            .WithName("RemoveClientDocument")
            .WithSummary("Remove a document filed on the client record, with a reason (logical, audited)")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Upload(
        Guid clientId,
        IFormFile file,
        [FromForm] string kind,
        [FromForm] string title,
        [FromForm] string direction,
        [FromForm] DateOnly documentDate,
        IOptions<ClientDocumentStorageOptions> options,
        ISender sender,
        CancellationToken ct)
    {
        if (!Enum.TryParse<ClientDocumentKind>(kind, ignoreCase: true, out var parsedKind)
            || !Enum.TryParse<CustomerDocumentDirection>(direction, ignoreCase: true, out var parsedDirection))
            return Results.Problem("kind or direction is not a known value", statusCode: StatusCodes.Status400BadRequest);

        // Refused before reading: the size is known from the request, the bytes are not needed.
        if (file.Length > options.Value.MaxBytes) return ToProblem(CustomerErrors.ClientDocumentTooLarge);

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, ct);

        var result = await sender.Send(new UploadClientDocument(
            clientId, parsedKind, title, parsedDirection, documentDate, file.FileName, buffer.ToArray()), ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/clients/{clientId}/documents/{result.Value}/content",
                new UploadClientDocumentResponse(result.Value))
            : ToProblem(result.Error);
    }

    private static async Task<IResult> Content(Guid clientId, Guid documentId, HttpContext http, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetClientDocumentContentQuery(clientId, documentId), ct);
        if (result.IsFailure) return ToProblem(result.Error);

        // Never cached by a browser or a proxy: a client's document, under a permission that may be
        // withdrawn tomorrow.
        http.Response.Headers.CacheControl = "no-store";
        var file = result.Value!;
        return Results.File(file.Content, file.ContentType, file.FileName);
    }

    private static async Task<IResult> Remove(
        Guid clientId, Guid documentId, RemoveClientDocumentRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RemoveClientDocumentCommand(clientId, documentId, req.Reason), ct);
        return result.IsSuccess ? Results.NoContent() : ToProblem(result.Error);
    }

    private static IResult ToProblem(string? error) => error switch
    {
        CustomerErrors.ClientNotFound or CustomerErrors.ClientDocumentNotFound
            => Results.NotFound(new { error }),
        CustomerErrors.ClientReadOnly
            => Results.Conflict(new { error }),
        CustomerErrors.ClientDocumentsUnavailable
            => Results.Json(new { error }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.UnprocessableEntity(new { error }),
    };

    private static async Task<IResult> List(Guid clientId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ListClientDocumentsQuery(clientId), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : ToProblem(result.Error);
    }
}

public sealed record UploadClientDocumentResponse(Guid DocumentId);

public sealed record RemoveClientDocumentRequest(string Reason);
