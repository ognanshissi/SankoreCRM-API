namespace Sankore.Modules.Integration.Features.Insurance.Policies;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

/// <summary>
/// A client's policies, as the insurer holds them (<see cref="PolicyRecord"/>: written at issuance,
/// kept up to date by ASS-07's synchronisation), and the attestation of one of them.
///
/// <para>
/// <b>Within the caller's agency perimeter</b>, unlike the claims list next door: a policy says
/// what a client is covered for and for how much, which is the client record's business, and a
/// client outside the perimeter reads as not found — never as 403, which would confirm it exists.
/// </para>
/// </summary>
internal static class PolicyEndpoints
{
    internal const string ClientNotFound = "CLIENT_NOT_FOUND";
    internal const string PolicyNotFound = "INSURANCE_POLICY_NOT_FOUND";

    internal static IEndpointRouteBuilder MapPolicies(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("integration/insurance/policies")
            .WithTags("Integration")
            .RequireTenantModule(TenantModules.Insurance);

        group.MapGet(string.Empty, List)
            .WithName("ListInsurancePolicies")
            .WithSummary("A client's insurance policies")
            .WithDescription(
                "Every policy of `crmCustomerId`, newest effective date first, with its insurer, "
                + "product, status and premium. Policies are recorded when the insurer issues them "
                + "and then follow the insurer's synchronisation. A client outside the caller's "
                + "agency perimeter answers 404. Requires permission: Ins.Policy.View.")
            .RequireAuthorization(Permissions.CanViewInsurancePolicy.Code)
            .Produces<IReadOnlyList<InsurancePolicyDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{policyId:guid}/certificate", Certificate)
            .WithName("GetInsurancePolicyCertificate")
            .WithSummary("The insurer's attestation of a policy")
            .WithDescription(
                "Asked of the insurer at the moment of the request — the attestation is the "
                + "insurer's document and may change with an avenant. 502 when the insurer cannot "
                + "produce it (its connector does not issue attestations, or it is unreachable). "
                + "Requires permission: Ins.Policy.View.")
            .RequireAuthorization(Permissions.CanViewInsurancePolicy.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status502BadGateway)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> List(
        Guid crmCustomerId,
        IntegrationDbContext db,
        ICustomersModule customers,
        ICurrentUser user,
        CancellationToken ct)
    {
        if (!await customers.CanActorAccessClientAsync(user.TenantId, crmCustomerId, user.Id, ct))
            return Results.NotFound(new { error = ClientNotFound });

        var rows = await db.Policies
            .Where(p => p.CrmCustomerId == crmCustomerId)
            .Join(db.Connections, p => p.ConnectionId, c => c.Id, (p, c) => new { Policy = p, Insurer = c.Name })
            .OrderByDescending(x => x.Policy.EffectiveDate)
            .ToListAsync(ct);

        var productIds = rows.Select(r => r.Policy.ProductId).OfType<Guid>().Distinct().ToList();
        var products = await db.InsuranceProducts
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        // In memory: IsPremiumOutstanding is computed on the saga, and a client has a handful.
        var subscriptions = (await db.InsuranceSubscriptions
                .Where(s => s.CrmCustomerId == crmCustomerId && s.PolicyId != null)
                .ToListAsync(ct))
            .ToDictionary(s => s.PolicyId!.Value);

        return Results.Ok(rows.Select(r => new InsurancePolicyDto(
            Id: r.Policy.Id,
            PolicyNumber: r.Policy.PolicyNumber,
            Insurer: r.Insurer,
            ProductId: r.Policy.ProductId,
            ProductName: r.Policy.ProductId is { } id && products.TryGetValue(id, out var name)
                ? name
                : r.Policy.InsurerProductCode,
            Status: r.Policy.Status.ToString(),
            StatusDetail: r.Policy.StatusDetail,
            EffectiveDate: r.Policy.EffectiveDate,
            ExpiryDate: r.Policy.ExpiryDate,
            NextDueDate: r.Policy.NextDueDate,
            PremiumAmount: r.Policy.PremiumAmount,
            Currency: r.Policy.Currency,
            Periodicity: r.Policy.Periodicity.ToString(),
            InsuredAmount: r.Policy.InsuredAmount,
            HoldingId: subscriptions.TryGetValue(r.Policy.Id, out var subscription) ? subscription.CrmHoldingId : null,
            LastSyncedAt: r.Policy.LastSyncedAt,
            // Issued is not in force: the contract starts once the premium is paid, and the
            // insurer's own status says nothing about that until its next synchronisation.
            PremiumOutstanding: subscription?.IsPremiumOutstanding ?? false)).ToList());
    }

    private static async Task<IResult> Certificate(
        Guid policyId,
        IntegrationDbContext db,
        ICustomersModule customers,
        ICurrentUser user,
        IntegrationAdapterResolver resolver,
        CancellationToken ct)
    {
        var row = await db.Policies
            .Where(p => p.Id == policyId)
            .Join(db.Connections, p => p.ConnectionId, c => c.Id, (p, c) => new { Policy = p, Connection = c })
            .FirstOrDefaultAsync(ct);

        if (row is null
            || !await customers.CanActorAccessClientAsync(user.TenantId, row.Policy.CrmCustomerId, user.Id, ct))
            return Results.NotFound(new { error = PolicyNotFound });

        var adapter = resolver.ResolveFor(row.Connection);
        if (adapter.IsFailure)
            return Unavailable($"{adapter.Code}: {adapter.Detail}");

        var port = resolver.ResolvePort<IInsurancePolicyPort>(adapter.Value, IntegrationCapability.IssueCertificate);
        if (port.IsFailure)
            return Unavailable($"{port.Code}: {port.Detail}");

        var certificate = await port.Value.GetCertificateAsync(new ExternalId(row.Policy.ExternalPolicyId), ct);
        if (certificate.IsFailure)
            return Unavailable($"{certificate.Code}: {certificate.Detail}");

        var file = certificate.Value;
        return Results.File(
            file.Content,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/pdf" : file.ContentType,
            string.IsNullOrWhiteSpace(file.FileName) ? $"attestation-{row.Policy.PolicyNumber}.pdf" : file.FileName);
    }

    private static IResult Unavailable(string error)
        => Results.Json(new { error = $"INSURANCE_CERTIFICATE_UNAVAILABLE: {error}" }, statusCode: StatusCodes.Status502BadGateway);
}

/// <param name="HoldingId">The CRM contract the policy realises, when it was subscribed from one.</param>
public sealed record InsurancePolicyDto(
    Guid Id,
    string PolicyNumber,
    string Insurer,
    Guid? ProductId,
    string ProductName,
    string Status,
    string? StatusDetail,
    DateOnly EffectiveDate,
    DateOnly? ExpiryDate,
    DateOnly? NextDueDate,
    decimal PremiumAmount,
    string Currency,
    string Periodicity,
    decimal? InsuredAmount,
    Guid? HoldingId,
    DateTimeOffset LastSyncedAt,
    bool PremiumOutstanding = false);
