namespace Sankore.Modules.Integration.Features.Insurance.AfterSale;

using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using KycStatus = Sankore.Modules.Kyc.PublicApi.KycStatus;

/// <summary>
/// Insurance lot 3 — what happens once the proposal has left: the insurer's underwriting answer and
/// the client's answer to it (recorded by the agent, product owner 2026-10-25), the requests on a
/// policy (renunciation, surrender, advance), the insurer's documents and their relay to the client,
/// and the evidence journal of a digital sale. All within the agent's perimeter.
/// </summary>
internal static class AfterSaleEndpoints
{
    internal const string SubscriptionNotFound = "INSURANCE_SUBSCRIPTION_NOT_FOUND";
    internal const string PolicyNotFound = "INSURANCE_POLICY_NOT_FOUND";
    internal const string OperationNotFound = "INSURANCE_OPERATION_NOT_FOUND";
    internal const string DocumentNotFound = "INSURANCE_POLICY_DOCUMENT_NOT_FOUND";
    internal const string RenunciationNotLife = "INSURANCE_RENUNCIATION_NOT_LIFE";
    internal const string RenunciationPeriodOver = "INSURANCE_RENUNCIATION_PERIOD_OVER";
    internal const string OperationInProgress = "INSURANCE_OPERATION_IN_PROGRESS";
    internal const string RefundBlockedByKyc = "INSURANCE_REFUND_BLOCKED_KYC";
    internal const string Invalid = "INSURANCE_AFTER_SALE_INVALID";
    internal const string PayoutInstrumentInvalid = "INSURANCE_PAYOUT_INSTRUMENT_INVALID";

    internal static IEndpointRouteBuilder MapAfterSale(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("integration/insurance")
            .WithTags("Integration")
            .RequireTenantModule(TenantModules.Insurance);

        group.MapPost("subscriptions/{subscriptionId:guid}/underwriting", RecordUnderwriting)
            .WithName("RecordInsuranceUnderwriting")
            .WithSummary("Record the insurer's underwriting answer")
            .WithDescription(
                "Read from the insurer's letter or portal: accepted, with a surcharge (amount), with "
                + "exclusions (wording), medical exams required (list), refused, or — once 15 days have "
                + "passed without answer — deemed accepted by silence. A refusal ends the subscription "
                + "(a premium collected is owed back) and the contract. A surcharge or exclusions then "
                + "need the client's answer. Requires permission: Ins.Policy.Subscribe.")
            .RequireAuthorization(Permissions.CanSubscribeInsurancePolicy.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("subscriptions/{subscriptionId:guid}/client-answer", RecordClientAnswer)
            .WithName("RecordInsuranceClientAnswer")
            .WithSummary("Record the client's answer to a surcharge or exclusions")
            .RequireAuthorization(Permissions.CanSubscribeInsurancePolicy.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("policies/{policyId:guid}/operations", RequestOperation)
            .WithName("RequestInsurancePolicyOperation")
            .WithSummary("Receive a renunciation, surrender or advance request")
            .WithDescription(
                "Timestamped on receipt. A renunciation is for a life product and within 30 days of "
                + "the first premium; the insurer then refunds within 30 days. Requires permission: "
                + "Ins.Policy.Subscribe.")
            .RequireAuthorization(Permissions.CanSubscribeInsurancePolicy.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("policies/{policyId:guid}/operations", ListOperations)
            .WithName("ListInsurancePolicyOperations")
            .WithSummary("The requests on a policy, newest first")
            .RequireAuthorization(Permissions.CanViewInsurancePolicy.Code)
            .Produces<IReadOnlyList<PolicyOperationDto>>(StatusCodes.Status200OK)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("policy-operations/{operationId:guid}/status", MoveOperation)
            .WithName("UpdateInsurancePolicyOperationStatus")
            .WithSummary("Report what the insurer did with a request")
            .WithDescription(
                "Sent to the insurer, accepted, refused, paid (with its date), or cancelled before "
                + "sending. A renunciation refund is never recorded paid while the client's KYC is "
                + "incomplete (409 INSURANCE_REFUND_BLOCKED_KYC). Requires permission: Ins.Policy.Subscribe.")
            .RequireAuthorization(Permissions.CanSubscribeInsurancePolicy.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("policies/{policyId:guid}/documents", UploadDocument)
            .WithName("UploadInsurancePolicyDocument")
            .WithSummary("File one of the insurer's documents for a policy (CG, CP, attestation, avenant, annual information)")
            .RequireAuthorization(Permissions.CanSubscribeInsurancePolicy.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .DisableAntiforgery()
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("policies/{policyId:guid}/documents", ListDocuments)
            .WithName("ListInsurancePolicyDocuments")
            .WithSummary("The insurer's documents filed for a policy")
            .RequireAuthorization(Permissions.CanViewInsurancePolicy.Code)
            .Produces<IReadOnlyList<PolicyDocumentDto>>(StatusCodes.Status200OK)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("policy-documents/{documentId:guid}/content", DocumentContent)
            .WithName("GetInsurancePolicyDocumentContent")
            .WithSummary("The file of a policy document")
            .RequireAuthorization(Permissions.CanViewInsurancePolicy.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("policy-documents/{documentId:guid}/sent", MarkDocumentSent)
            .WithName("MarkInsurancePolicyDocumentSent")
            .WithSummary("Record that the document was handed to the client (channel, now)")
            .RequireAuthorization(Permissions.CanSubscribeInsurancePolicy.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("products/{productId:guid}/life-insurance", SetLifeInsurance)
            .WithName("SetInsuranceProductLifeInsurance")
            .WithSummary("Whether the product is life insurance (30-day renunciation)")
            .RequireAuthorization(Permissions.CanManageInsuranceProduct.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("holdings/{holdingId:guid}/evidence", Evidence)
            .WithName("GetInsuranceSubscriptionEvidence")
            .WithSummary("The evidence journal of a subscription: every step, timestamped")
            .WithDescription(
                "Assembled from the records themselves — information sheet deliveries, signature, "
                + "consent, payment, sending, the insurer's answer, the client's answer, issue, "
                + "documents handed over, requests on the policy. What proves a digital sale. "
                + "Requires permission: Ins.Policy.View.")
            .RequireAuthorization(Permissions.CanViewInsurancePolicy.Code)
            .Produces<IReadOnlyList<EvidenceEventDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    // ── The insurer's answer and the client's ───────────────────────────────

    private static async Task<IResult> RecordUnderwriting(
        Guid subscriptionId, RecordUnderwritingRequest body, IntegrationDbContext db, ICustomersModule customers,
        ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var saga = await LoadSagaAsync(db, customers, user, subscriptionId, ct);
        if (saga is null) return Results.NotFound(new { error = SubscriptionNotFound });

        try
        {
            saga.RecordUnderwriting(body.Outcome, body.SurchargeAmount, body.Exclusions, body.MedicalExams,
                body.Reference, body.ReceivedOn, user.Id, clock);

            if (body.Outcome == UnderwritingOutcome.Refused)
                saga.EndWithoutCover("INSURER_REFUSED", body.Reference, clock);
        }
        catch (DomainException ex)
        {
            return Results.BadRequest(new { error = $"{Invalid}: {ex.Message}" });
        }

        await db.SaveChangesAsync(ct);

        if (body.Outcome == UnderwritingOutcome.Refused)
            await EndHoldingAsync(customers, saga, "Refusée par l'assureur", ct);

        return Results.NoContent();
    }

    private static async Task<IResult> RecordClientAnswer(
        Guid subscriptionId, RecordClientAnswerRequest body, IntegrationDbContext db, ICustomersModule customers,
        ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var saga = await LoadSagaAsync(db, customers, user, subscriptionId, ct);
        if (saga is null) return Results.NotFound(new { error = SubscriptionNotFound });

        try
        {
            saga.RecordClientAnswer(body.Answer, body.Reference, clock);

            if (body.Answer == ClientUnderwritingAnswer.Declined)
                saga.EndWithoutCover("CLIENT_DECLINED_UNDERWRITING", body.Reference, clock);
        }
        catch (DomainException ex)
        {
            return Results.BadRequest(new { error = $"{Invalid}: {ex.Message}" });
        }

        await db.SaveChangesAsync(ct);

        if (body.Answer == ClientUnderwritingAnswer.Declined)
            await EndHoldingAsync(customers, saga, "Le client a refusé les conditions de l'assureur", ct);

        return Results.NoContent();
    }

    /// <summary>The contract ends with the subscription: rejected if never in force, closed otherwise.</summary>
    private static async Task EndHoldingAsync(ICustomersModule customers, InsuranceSubscription saga, string reason, CancellationToken ct)
    {
        if (saga.CrmHoldingId is not { } holdingId) return;

        var rejected = await customers.RejectHoldingSyncAsync(saga.TenantId, holdingId, reason, ct);
        if (rejected.IsFailure)
            await customers.CloseHoldingAsync(saga.TenantId, holdingId, reason, ct);
    }

    // ── Requests on a policy ────────────────────────────────────────────────

    private static async Task<IResult> RequestOperation(
        Guid policyId, RequestOperationRequest body, IntegrationDbContext db, ICustomersModule customers,
        ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var policy = await LoadPolicyAsync(db, customers, user, policyId, ct);
        if (policy is null) return Results.NotFound(new { error = PolicyNotFound });

        if (await db.PolicyOperations.AnyAsync(o => o.PolicyId == policyId && o.Kind == body.Kind
                && (o.Status == PolicyOperationStatus.Requested || o.Status == PolicyOperationStatus.SentToInsurer
                    || o.Status == PolicyOperationStatus.Accepted), ct))
            return Results.Conflict(new { error = OperationInProgress });

        decimal? amount = body.Amount;
        if (body.Kind == PolicyOperationKind.Renunciation)
        {
            var product = policy.ProductId is { } productId
                ? await db.InsuranceProducts.FirstOrDefaultAsync(p => p.Id == productId, ct)
                : null;
            if (product?.IsLifeInsurance != true)
                return Results.Conflict(new { error = RenunciationNotLife });

            // 30 days from the first premium paid (art. 65), the policy's start when not known here.
            var saga = policy.SubscriptionId is { } sid
                ? await db.InsuranceSubscriptions.FirstOrDefaultAsync(s => s.Id == sid, ct)
                : null;
            var start = saga?.PremiumPaidAt is { } paid
                ? DateOnly.FromDateTime(paid.UtcDateTime)
                : policy.EffectiveDate;
            if (DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) > start.AddDays(30))
                return Results.Conflict(new { error = RenunciationPeriodOver });

            // What the insurer must give back: the premium paid.
            amount ??= saga?.PremiumAmount ?? policy.PremiumAmount;
        }

        // The money goes to the policy holder's own instrument, never to one a caller names freely.
        if (body.PayoutInstrumentId is { } instrumentId
            && !await customers.IsActivePaymentInstrumentOfAsync(user.TenantId, instrumentId, policy.CrmCustomerId, ct))
            return Results.BadRequest(new { error = PayoutInstrumentInvalid });

        PolicyOperation operation;
        try
        {
            operation = PolicyOperation.Request(user.TenantId, policyId, policy.CrmCustomerId, body.Kind,
                amount, policy.Currency, body.Reason, body.PayoutInstrumentId, user.Id, clock);
        }
        catch (DomainException ex)
        {
            return Results.BadRequest(new { error = $"{Invalid}: {ex.Message}" });
        }

        db.PolicyOperations.Add(operation);
        await db.SaveChangesAsync(ct);
        return Results.Created($"integration/insurance/policies/{policyId}/operations", operation.Id);
    }

    private static async Task<IResult> ListOperations(
        Guid policyId, IntegrationDbContext db, ICustomersModule customers, IKycModule kyc, ICurrentUser user, CancellationToken ct)
    {
        var policy = await LoadPolicyAsync(db, customers, user, policyId, ct);
        if (policy is null) return Results.NotFound(new { error = PolicyNotFound });

        var kycComplete = await kyc.GetStatusAsync(user.TenantId, policy.CrmCustomerId, ct) == KycStatus.Approved;
        var rows = await db.PolicyOperations.Where(o => o.PolicyId == policyId)
            .OrderByDescending(o => o.RequestedAt).ToListAsync(ct);

        return Results.Ok(rows.Select(o => PolicyOperationDto.From(o, kycComplete)).ToList());
    }

    private static async Task<IResult> MoveOperation(
        Guid operationId, MoveOperationRequest body, IntegrationDbContext db, ICustomersModule customers, IKycModule kyc,
        ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var operation = await db.PolicyOperations.AsTracking().FirstOrDefaultAsync(o => o.Id == operationId, ct);
        if (operation is null || !await customers.CanActorAccessClientAsync(user.TenantId, operation.CrmCustomerId, user.Id, ct))
            return Results.NotFound(new { error = OperationNotFound });

        // LBC/FT: no refund leaves for a client whose identification is not complete.
        if (body.Status == PolicyOperationStatus.Paid
            && operation.Kind == PolicyOperationKind.Renunciation
            && await kyc.GetStatusAsync(user.TenantId, operation.CrmCustomerId, ct) != KycStatus.Approved)
            return Results.Conflict(new { error = RefundBlockedByKyc });

        try
        {
            operation.MoveTo(body.Status, body.InsurerReference, body.Amount, body.PaidOn, user.Id, clock);
        }
        catch (DomainException ex)
        {
            return Results.BadRequest(new { error = $"{Invalid}: {ex.Message}" });
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── The insurer's documents ─────────────────────────────────────────────

    private static async Task<IResult> UploadDocument(
        Guid policyId, [FromForm] PolicyDocumentKind kind, [FromForm] string? title, IFormFile file,
        IntegrationDbContext db, ICustomersModule customers, IFileStore files, ICurrentUser user,
        TimeProvider clock, CancellationToken ct)
    {
        var policy = await LoadPolicyAsync(db, customers, user, policyId, ct);
        if (policy is null) return Results.NotFound(new { error = PolicyNotFound });

        if (file is null || file.Length == 0 || file.Length > 20L * 1024 * 1024
            || !string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "INSURANCE_POLICY_DOCUMENT_NOT_PDF" });

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
        buffer.Position = 0;
        var reference = await files.StoreAsync(buffer, file.FileName, ct);

        var document = PolicyDocument.Upload(user.TenantId, policyId, policy.CrmCustomerId, kind, title ?? string.Empty,
            reference, file.FileName, "application/pdf", sha256, user.Id, clock);
        db.PolicyDocuments.Add(document);
        await db.SaveChangesAsync(ct);

        return Results.Created($"integration/insurance/policy-documents/{document.Id}/content", document.Id);
    }

    private static async Task<IResult> ListDocuments(
        Guid policyId, IntegrationDbContext db, ICustomersModule customers, ICurrentUser user, CancellationToken ct)
    {
        var policy = await LoadPolicyAsync(db, customers, user, policyId, ct);
        if (policy is null) return Results.NotFound(new { error = PolicyNotFound });

        var rows = await db.PolicyDocuments.Where(d => d.PolicyId == policyId)
            .OrderByDescending(d => d.UploadedAt).ToListAsync(ct);
        return Results.Ok(rows.Select(PolicyDocumentDto.From).ToList());
    }

    private static async Task<IResult> DocumentContent(
        Guid documentId, IntegrationDbContext db, ICustomersModule customers, IFileStore files, ICurrentUser user, CancellationToken ct)
    {
        var document = await db.PolicyDocuments.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null || !await customers.CanActorAccessClientAsync(user.TenantId, document.CrmCustomerId, user.Id, ct))
            return Results.NotFound(new { error = DocumentNotFound });

        return Results.File(await files.ReadAsync(document.FileReference, ct), document.ContentType, document.FileName);
    }

    private static async Task<IResult> MarkDocumentSent(
        Guid documentId, MarkDocumentSentRequest body, IntegrationDbContext db, ICustomersModule customers,
        ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var document = await db.PolicyDocuments.AsTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null || !await customers.CanActorAccessClientAsync(user.TenantId, document.CrmCustomerId, user.Id, ct))
            return Results.NotFound(new { error = DocumentNotFound });

        document.MarkSentToClient(body.Channel, user.Id, clock);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetLifeInsurance(
        Guid productId, SetLifeInsuranceRequest body, IntegrationDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var product = await db.InsuranceProducts.AsTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null) return Results.NotFound(new { error = "INSURANCE_PRODUCT_NOT_FOUND" });

        product.SetLifeInsurance(body.IsLifeInsurance, user.Id, clock);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── The evidence journal ────────────────────────────────────────────────

    private static async Task<IResult> Evidence(
        Guid holdingId, IntegrationDbContext db, ICustomersModule customers, ICurrentUser user, CancellationToken ct)
    {
        var holding = await customers.GetHoldingForSyncAsync(user.TenantId, holdingId, ct);
        if (holding is null || !await customers.CanActorAccessClientAsync(user.TenantId, holding.ClientId, user.Id, ct))
            return Results.NotFound(new { error = "INSURANCE_HOLDING_NOT_FOUND" });

        var events = new List<EvidenceEventDto>();

        foreach (var d in await db.InformationSheetDeliveries.Where(d => d.HoldingId == holdingId).ToListAsync(ct))
            events.Add(new(d.DeliveredAt, "InformationSheetDelivered",
                $"Fiche d'information v{d.SheetVersion} remise ({EvidenceLabels.Of(d.Channel)})", $"Accusé {d.AcknowledgementReference} · empreinte {d.SheetSha256[..12]}", d.DeliveredBy));

        if (holding.SignedAt is { } signedAt)
            events.Add(new(signedAt, "Signed", "Proposition signée par le client", holding.SignatureReference, null));

        var sagas = await db.InsuranceSubscriptions.Where(s => s.CrmHoldingId == holdingId).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        var sagaIds = sagas.Select(s => s.Id).ToList();
        var proofs = await db.ConsentProofs.Where(p => sagaIds.Contains(p.SubscriptionId)).ToListAsync(ct);

        foreach (var s in sagas)
        {
            foreach (var p in proofs.Where(p => p.SubscriptionId == s.Id))
                events.Add(new(p.ConsentedAt, "Consented", $"Consentement recueilli ({EvidenceLabels.Of(p.Channel)})", $"Texte {p.ConsentTextVersion}", p.CapturedBy));

            events.Add(new(s.CreatedAt, "Prepared", "Souscription préparée", $"Paiement : {EvidenceLabels.Of(s.PaymentMode)}", s.CreatedBy));
            if (s.PremiumPaidAt is { } paid)
                events.Add(new(paid, "PremiumPaid", "Prime encaissée", s.CbsDebitReference ?? s.RecordedPaymentReference, null));
            if (s.SentToInsurerAt is { } sent)
                events.Add(new(sent, "SentToInsurer", "Proposition transmise à l'assureur", null, null));
            if (s.UnderwritingRecordedAt is { } uw)
                events.Add(new(uw, "Underwriting", $"Réponse de l'assureur : {EvidenceLabels.Of(s.Underwriting)}", s.UnderwritingReference, s.UnderwritingRecordedBy));
            if (s.ClientAnsweredAt is { } answered)
                events.Add(new(answered, "ClientAnswer", $"Réponse du client : {EvidenceLabels.Of(s.ClientAnswer)}", s.ClientAnswerReference, null));
            if (s.IssuedAt is { } issued)
                events.Add(new(issued, "Issued", "Police émise", s.InsurerPolicyReference, null));
            if (s.IsEnded)
                events.Add(new(s.UpdatedAt, "Ended", $"Souscription close : {EvidenceLabels.Of(s.Status)}", s.FailureDetail, null));
        }

        var policyIds = sagas.Where(s => s.PolicyId != null).Select(s => s.PolicyId!.Value).ToList();
        foreach (var doc in await db.PolicyDocuments.Where(d => policyIds.Contains(d.PolicyId) && d.SentToClientAt != null).ToListAsync(ct))
            events.Add(new(doc.SentToClientAt!.Value, "DocumentSent", $"Document remis au client : {doc.Title}", $"{EvidenceLabels.Of(doc.SentChannel)} · empreinte {doc.Sha256[..12]}", doc.SentBy));
        foreach (var op in await db.PolicyOperations.Where(o => policyIds.Contains(o.PolicyId)).ToListAsync(ct))
            events.Add(new(op.RequestedAt, "OperationRequested", $"Demande reçue : {EvidenceLabels.Of(op.Kind)}", op.Reason, op.RequestedBy));

        return Results.Ok(events.OrderBy(e => e.At).ToList());
    }

    // ── Loading within the perimeter ────────────────────────────────────────

    private static async Task<InsuranceSubscription?> LoadSagaAsync(
        IntegrationDbContext db, ICustomersModule customers, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var saga = await db.InsuranceSubscriptions.AsTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        return saga is not null && await customers.CanActorAccessClientAsync(user.TenantId, saga.CrmCustomerId, user.Id, ct)
            ? saga : null;
    }

    private static async Task<PolicyRecord?> LoadPolicyAsync(
        IntegrationDbContext db, ICustomersModule customers, ICurrentUser user, Guid id, CancellationToken ct)
    {
        var policy = await db.Policies.FirstOrDefaultAsync(p => p.Id == id, ct);
        return policy is not null && await customers.CanActorAccessClientAsync(user.TenantId, policy.CrmCustomerId, user.Id, ct)
            ? policy : null;
    }
}

public sealed record RecordUnderwritingRequest(
    UnderwritingOutcome Outcome, string Reference, DateOnly ReceivedOn,
    decimal? SurchargeAmount = null, string? Exclusions = null, string? MedicalExams = null);

public sealed record RecordClientAnswerRequest(ClientUnderwritingAnswer Answer, string Reference);

public sealed record RequestOperationRequest(
    PolicyOperationKind Kind, decimal? Amount = null, string? Reason = null, Guid? PayoutInstrumentId = null);

public sealed record MoveOperationRequest(
    PolicyOperationStatus Status, string? InsurerReference = null, decimal? Amount = null, DateOnly? PaidOn = null);

public sealed record MarkDocumentSentRequest(InformationSheetChannel Channel);

public sealed record SetLifeInsuranceRequest(bool IsLifeInsurance);

/// <param name="RefundBlocked">A renunciation whose refund cannot be recorded while the client's KYC is incomplete.</param>
public sealed record PolicyOperationDto(
    Guid Id, Guid PolicyId, string Kind, string Status, DateTimeOffset RequestedAt, string? Reason,
    decimal? Amount, string? Currency, DateOnly? RefundDueBy, Guid? PayoutInstrumentId, string? InsurerReference,
    DateTimeOffset? StatusChangedAt, DateOnly? PaidOn, bool RefundBlocked)
{
    internal static PolicyOperationDto From(PolicyOperation o, bool kycComplete) => new(
        o.Id, o.PolicyId, o.Kind.ToString(), o.Status.ToString(), o.RequestedAt, o.Reason, o.Amount, o.Currency,
        o.RefundDueBy, o.PayoutInstrumentId, o.InsurerReference, o.StatusChangedAt, o.PaidOn,
        o.Kind == PolicyOperationKind.Renunciation && o.IsOpen && !kycComplete);
}

public sealed record PolicyDocumentDto(
    Guid Id, Guid PolicyId, string Kind, string Title, string FileName, string Sha256, DateTimeOffset UploadedAt,
    DateTimeOffset? SentToClientAt, string? SentChannel)
{
    internal static PolicyDocumentDto From(PolicyDocument d) => new(
        d.Id, d.PolicyId, d.Kind.ToString(), d.Title, d.FileName, d.Sha256, d.UploadedAt, d.SentToClientAt, d.SentChannel?.ToString());
}

/// <param name="Kind">InformationSheetDelivered, Signed, Consented, Prepared, PremiumPaid, SentToInsurer, Underwriting, ClientAnswer, Issued, Ended, DocumentSent, OperationRequested.</param>
public sealed record EvidenceEventDto(DateTimeOffset At, string Kind, string Label, string? Detail, Guid? ActorId);

/// <summary>
/// The journal is read by an agent and handed to a regulator: its lines are French sentences, not
/// enum names (« remise (HandedOnPaper) », « Paiement : InsurerCollects » before this). A value added
/// to an enum later falls back to its name rather than to nothing.
/// </summary>
internal static class EvidenceLabels
{
    public static string Of(InformationSheetChannel? channel) => channel switch
    {
        InformationSheetChannel.HandedOnPaper => "remise en main propre",
        InformationSheetChannel.ShownOnScreen => "présentée à l'écran",
        InformationSheetChannel.SentByEmail => "envoyée par e-mail",
        InformationSheetChannel.CustomerSpace => "déposée dans l'espace client",
        null => "canal non précisé",
        _ => channel.ToString()!,
    };

    public static string Of(ConsentChannel channel) => channel switch
    {
        ConsentChannel.PaperAtCounter => "bulletin papier signé au guichet",
        ConsentChannel.ElectronicSignaturePad => "signature électronique sur tablette",
        ConsentChannel.OneTimeCode => "code à usage unique",
        ConsentChannel.CustomerChannel => "canal client",
        _ => channel.ToString(),
    };

    public static string Of(PremiumPaymentMode? mode) => mode switch
    {
        PremiumPaymentMode.CoreBankingDebit => "prélèvement sur le compte",
        PremiumPaymentMode.RecordedByAgent => "encaissée par l'agent",
        PremiumPaymentMode.InsurerCollects => "encaissée par l'assureur",
        PremiumPaymentMode.DeferredByDerogation => "paiement sous 60 jours (dérogation)",
        null => "non précisé",
        _ => mode.ToString()!,
    };

    public static string Of(UnderwritingOutcome? outcome) => outcome switch
    {
        UnderwritingOutcome.Accepted => "acceptée",
        UnderwritingOutcome.AcceptedWithSurcharge => "acceptée avec surprime",
        UnderwritingOutcome.AcceptedWithExclusions => "acceptée avec exclusions",
        UnderwritingOutcome.MedicalExamsRequired => "examens médicaux demandés",
        UnderwritingOutcome.Refused => "refusée",
        UnderwritingOutcome.DeemedAcceptedBySilence => "réputée acceptée (silence de 15 jours)",
        _ => outcome?.ToString() ?? "—",
    };

    public static string Of(ClientUnderwritingAnswer? answer) => answer switch
    {
        ClientUnderwritingAnswer.Accepted => "accepte",
        ClientUnderwritingAnswer.Declined => "refuse",
        _ => answer?.ToString() ?? "—",
    };

    public static string Of(SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.DebitFailed => "prélèvement échoué",
        SubscriptionStatus.Reversed => "prime restituée",
        SubscriptionStatus.PremiumRefundDue => "prime à rembourser",
        SubscriptionStatus.Cancelled => "annulée",
        SubscriptionStatus.Refused => "refusée par l'assureur",
        _ => status.ToString(),
    };

    public static string Of(PolicyOperationKind kind) => kind switch
    {
        PolicyOperationKind.Renunciation => "renonciation",
        PolicyOperationKind.Surrender => "rachat",
        PolicyOperationKind.Advance => "avance sur contrat",
        _ => kind.ToString(),
    };
}
