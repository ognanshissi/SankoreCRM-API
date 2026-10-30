namespace Sankore.Modules.Administration.Features.InsuranceDistribution;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The institution as an insurance distributor (insurance lot 2): status, art. 540 mention, annual
/// SMIG, and the pieces — agrément, garantie financière, RC pro — whose expiry blocks new
/// subscriptions. Read by every signed-in user (the subscription screen prints the mention and
/// explains a block); written under the compliance permission.
///
/// <para>
/// Thin endpoints like the claim pieces': a multipart upload does not bind to a MediatR command,
/// and the two writes are one row each.
/// </para>
/// </summary>
internal static class InsuranceDistributionEndpoints
{
    private const long MaxFileBytes = 10L * 1024 * 1024;

    private static readonly string[] AllowedContentTypes = ["application/pdf", "image/jpeg", "image/png"];

    public static IEndpointRouteBuilder MapInsuranceDistributionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("insurance-distribution").WithTags("Insurance distribution").LiveModeWritesOnly();

        group.MapGet(string.Empty, Get)
            .WithName("GetInsuranceDistributor")
            .WithSummary("The institution's distributor profile, its pieces and its standing")
            .RequireAuthorization()
            .Produces<InsuranceDistributorDto>(StatusCodes.Status200OK)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut(string.Empty, Update)
            .WithName("UpdateInsuranceDistributor")
            .WithSummary("Set the distributor status, registration number, legal mention and annual SMIG")
            .WithDescription(
                "A broker must carry the legal mention of art. 540, printed on every document "
                + "handed to a client. The annual SMIG enables the 60-day payment derogation for "
                + "premiums above 80 times it. Requires permission: admin:compliance:manage.")
            .RequireAuthorization(Permissions.CanManageCompliance.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("documents", AddDocument)
            .WithName("AddInsuranceDistributorDocument")
            .WithSummary("Record a piece (agrément, garantie financière, RC pro), optionally with its scan")
            .WithDescription(
                "A new piece supersedes the current one of the same kind; the old one stays, it "
                + "covered the contracts signed while in force. PDF, JPEG or PNG, 10 MB at most. "
                + "Requires permission: admin:compliance:manage.")
            .RequireAuthorization(Permissions.CanManageCompliance.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .DisableAntiforgery()
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("documents/{documentId:guid}/content", Content)
            .WithName("GetInsuranceDistributorDocumentContent")
            .WithSummary("The scan of a piece")
            .RequireAuthorization(Permissions.CanManageCompliance.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Get(
        AdministrationDbContext db, IAdministrationModule administration, ICurrentUser user,
        TimeProvider clock, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var profile = await db.InsuranceDistributorProfiles.FirstOrDefaultAsync(ct);
        var documents = await db.DistributorDocuments
            .OrderBy(d => d.Kind).ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        var standing = await administration.GetInsuranceDistributorStandingAsync(user.TeamTenantId, today, ct);

        return Results.Ok(new InsuranceDistributorDto(
            Status: profile?.Status.ToString(),
            RegistrationNumber: profile?.RegistrationNumber,
            LegalMention: profile?.LegalMention,
            AnnualSmig: profile?.AnnualSmig,
            DerogationThreshold: profile?.AnnualSmig * 80m,
            RequiredDocuments: profile is null
                ? []
                : [.. InsuranceDistributorProfile.RequiredKinds(profile.Status).Select(k => k.ToString())],
            InGoodStanding: standing.InGoodStanding,
            MissingDocuments: standing.MissingDocuments,
            ExpiredDocuments: standing.ExpiredDocuments,
            Documents: [.. documents.Select(d => new DistributorDocumentDto(
                d.Id, d.Kind.ToString(), d.Reference, d.Issuer, d.IssuedOn, d.ExpiresOn,
                d.ExpiresOn is { } e ? e.DayNumber - today.DayNumber : null,
                d.FileReference is not null, d.FileName, d.IsCurrent, d.CreatedAt))]));
    }

    private static async Task<IResult> Update(
        UpdateInsuranceDistributorRequest body, AdministrationDbContext db, ICurrentUser user,
        TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var profile = await db.InsuranceDistributorProfiles.AsTracking().FirstOrDefaultAsync(ct);

        if (profile is null)
        {
            profile = InsuranceDistributorProfile.Create(user.TeamTenantId, user.Id, now);
            db.InsuranceDistributorProfiles.Add(profile);
        }

        var updated = profile.Update(
            body.Status, body.RegistrationNumber, body.LegalMention, body.AnnualSmig, user.Id, now);

        if (updated.IsFailure)
            return Results.BadRequest(new { error = updated.Error });

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AddDocument(
        [Microsoft.AspNetCore.Mvc.FromForm] DistributorDocumentKind kind,
        [Microsoft.AspNetCore.Mvc.FromForm] string reference,
        [Microsoft.AspNetCore.Mvc.FromForm] string? issuer,
        [Microsoft.AspNetCore.Mvc.FromForm] DateOnly? issuedOn,
        [Microsoft.AspNetCore.Mvc.FromForm] DateOnly? expiresOn,
        IFormFile? file,
        AdministrationDbContext db,
        IFileStore files,
        ICurrentUser user,
        TimeProvider clock,
        CancellationToken ct)
    {
        string? fileReference = null;

        if (file is { Length: > 0 })
        {
            if (file.Length > MaxFileBytes)
                return Results.BadRequest(new { error = InsuranceDistributionErrors.FileTooLarge });

            if (!AllowedContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = InsuranceDistributionErrors.FileTypeNotAllowed });

            await using var stream = file.OpenReadStream();
            fileReference = await files.StoreAsync(stream, file.FileName, ct);
        }

        var now = clock.GetUtcNow();
        DistributorDocument document;

        try
        {
            document = DistributorDocument.Record(
                user.TeamTenantId, kind, reference, issuer, issuedOn, expiresOn,
                fileReference, file?.FileName, file?.ContentType, user.Id, now);
        }
        catch (DomainException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        // The renewal supersedes the piece in force; both stay on file.
        var current = await db.DistributorDocuments
            .AsTracking()
            .Where(d => d.Kind == kind && d.SupersededAt == null)
            .ToListAsync(ct);

        foreach (var previous in current)
            previous.Supersede(now);

        db.DistributorDocuments.Add(document);
        await db.SaveChangesAsync(ct);

        return Results.Created($"insurance-distribution/documents/{document.Id}", document.Id);
    }

    private static async Task<IResult> Content(
        Guid documentId, AdministrationDbContext db, IFileStore files, CancellationToken ct)
    {
        var document = await db.DistributorDocuments.FirstOrDefaultAsync(d => d.Id == documentId, ct);

        if (document?.FileReference is null)
            return Results.NotFound(new { error = InsuranceDistributionErrors.DocumentNotFound });

        var stream = await files.ReadAsync(document.FileReference, ct);
        return Results.File(stream, document.ContentType ?? "application/octet-stream", document.FileName);
    }
}

public sealed record UpdateInsuranceDistributorRequest(
    InsuranceDistributorStatus Status,
    string? RegistrationNumber,
    string? LegalMention,
    decimal? AnnualSmig);

/// <param name="DerogationThreshold">80 × the annual SMIG: above it, a premium may be paid within 60 days.</param>
public sealed record InsuranceDistributorDto(
    string? Status,
    string? RegistrationNumber,
    string? LegalMention,
    decimal? AnnualSmig,
    decimal? DerogationThreshold,
    IReadOnlyList<string> RequiredDocuments,
    bool InGoodStanding,
    IReadOnlyList<string> MissingDocuments,
    IReadOnlyList<string> ExpiredDocuments,
    IReadOnlyList<DistributorDocumentDto> Documents);

/// <param name="DaysLeft">Days until expiry, negative once expired; null when it does not expire.</param>
public sealed record DistributorDocumentDto(
    Guid Id,
    string Kind,
    string Reference,
    string? Issuer,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    int? DaysLeft,
    bool HasFile,
    string? FileName,
    bool IsCurrent,
    DateTimeOffset CreatedAt);
