namespace Sankore.Modules.Integration.Features.Insurance.Complaints;

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

// ── Recording a complaint ───────────────────────────────────────────────────

/// <summary>
/// Records a complaint — the entry point of the mechanism the règlement 01/CIMA/2024 requires.
/// </summary>
/// <param name="Body">
/// What the customer said, in clear on the way in and ENCRYPTED in the column. Free text about a
/// named person's money, health or family, written by somebody who is angry, so it is
/// <see cref="SensitiveDataAttribute"/>: the audit row records that the field was written, never
/// what was in it.
/// </param>
/// <param name="ReceivedAt">
/// When the CUSTOMER complained, which may be days before an agent typed it in — a letter, a call
/// logged late. The deadlines run from it, so it is part of the request rather than taken from the
/// clock. A date in the future is refused by the aggregate.
/// </param>
internal sealed record RecordComplaintCommand(
    Guid CrmCustomerId,
    ComplaintChannel Channel,
    ComplaintCategory Category,
    string Subject,
    [property: SensitiveData] string? Body,
    Guid? ConnectionId,
    Guid? PolicyId,
    Guid? ClaimId,
    DateTimeOffset? ReceivedAt)
    : IRequest<Result<ComplaintDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "InsuranceComplaint";

    // The CUSTOMER, because the complaint's own id does not exist until this command succeeds.
    public string ResourceId => CrmCustomerId.ToString();
}

public sealed record RecordComplaintRequest(
    Guid CrmCustomerId,
    ComplaintChannel Channel,
    ComplaintCategory Category,
    string Subject,
    string? Body,
    Guid? ConnectionId,
    Guid? PolicyId,
    Guid? ClaimId,
    DateTimeOffset? ReceivedAt);

internal sealed class RecordComplaintValidator : AbstractValidator<RecordComplaintCommand>
{
    public RecordComplaintValidator()
    {
        RuleFor(c => c.CrmCustomerId)
            .NotEmpty()
            .WithMessage("A complaint needs the customer it answers.")
            .OverridePropertyName("crmCustomerId");

        RuleFor(c => c.Subject)
            .NotEmpty()
            .MaximumLength(200)
            .WithMessage("A one-line subject an operator can read is required, 200 characters at most.")
            .OverridePropertyName("subject");

        // Uncapped in the column — encrypted text — but bounded in a request: an unbounded
        // free-text field reachable by an authenticated agent is a way to fill a disk.
        RuleFor(c => c.Body)
            .MaximumLength(8000)
            .When(c => c.Body is not null)
            .OverridePropertyName("body");

        // A ClaimHandling complaint that names no claim is a report nobody can act on, and the
        // category is what a regulator counts by — so the pair is checked rather than trusted.
        RuleFor(c => c.ClaimId)
            .NotNull()
            .When(c => c.Category == ComplaintCategory.ClaimHandling)
            .WithMessage("A complaint about the handling of a claim must name the claim.")
            .OverridePropertyName("claimId");
    }
}

internal sealed class RecordComplaintHandler(
    IntegrationDbContext db,
    IFieldEncryptor encryptor,
    ICurrentUser currentUser,
    IOptions<ComplaintOptions> options,
    TimeProvider clock,
    ILogger<RecordComplaintHandler> logger)
    : IRequestHandler<RecordComplaintCommand, Result<ComplaintDto>>
{
    public async Task<Result<ComplaintDto>> Handle(
        RecordComplaintCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var now = clock.GetUtcNow();
        var received = cmd.ReceivedAt ?? now;

        var policy = options.Value;

        // Computed once and STORED, from the moment the customer complained. A deadline
        // recomputed from today's setting would move under a complaint already late, which is the
        // one figure the mechanism is judged on.
        var acknowledgementDue = received.AddDays(policy.ResolvedAcknowledgementDays);
        var resolutionDue = received.AddDays(policy.ResolvedResolutionDays);

        var reference = await ComplaintReference.NextAsync(db, tenantId, received.Year, ct);

        InsuranceComplaint complaint;

        try
        {
            complaint = InsuranceComplaint.Create(
                tenantId: tenantId,
                reference: reference,
                crmCustomerId: cmd.CrmCustomerId,
                channel: cmd.Channel,
                category: cmd.Category,
                subject: cmd.Subject,
                receivedBy: currentUser.Id,
                acknowledgementDueAt: acknowledgementDue,
                resolutionDueAt: resolutionDue,
                clock: clock,
                connectionId: cmd.ConnectionId,
                policyId: cmd.PolicyId,
                claimId: cmd.ClaimId,
                // Null stays null: an absent body must not become a decryptable blank.
                bodyEncrypted: string.IsNullOrWhiteSpace(cmd.Body)
                    ? null
                    : encryptor.Encrypt(cmd.Body),
                receivedAt: cmd.ReceivedAt);
        }
        catch (DomainException ex)
        {
            return Result.Fail<ComplaintDto>(ex.Message);
        }

        db.InsuranceComplaints.Add(complaint);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Complaint {Reference} recorded for customer {CustomerId} ({Category}, {Channel}); "
            + "acknowledgement due {AckDue:yyyy-MM-dd}, answer due {ResDue:yyyy-MM-dd}",
            complaint.Reference, cmd.CrmCustomerId, cmd.Category, cmd.Channel,
            acknowledgementDue, resolutionDue);

        return Result.Ok(ComplaintDto.From(complaint, now));
    }
}

// ── Advancing one ───────────────────────────────────────────────────────────

/// <summary>
/// The three ways a recorded complaint moves: acknowledged, escalated, answered.
///
/// <para>
/// One command and not three, because the three differ only by which method of the aggregate they
/// call and what text they carry — and every one of them loads the same row, re-checks the same
/// terminal states and writes the same audit row. Three slices would be three places to forget the
/// perimeter.
/// </para>
/// </summary>
internal enum ComplaintAction
{
    Acknowledge,
    Escalate,
    Resolve,
    Close
}

/// <param name="Text">
/// The escalation target, the answer, or the reason for closing — depending on the action. In
/// CLEAR and deliberately so for an answer: it is the institution's own position, written to be
/// read back, and a controller asking "what did you answer" must not be handed a ciphertext.
/// </param>
internal sealed record AdvanceComplaintCommand(
    Guid ComplaintId,
    ComplaintAction Action,
    string? Text,
    bool WasUpheld)
    : IRequest<Result<ComplaintDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "InsuranceComplaint";

    public string ResourceId => ComplaintId.ToString();
}

public sealed record AcknowledgeComplaintRequest;

public sealed record EscalateComplaintRequest(string EscalatedTo);

public sealed record ResolveComplaintRequest(string Resolution, bool WasUpheld);

public sealed record CloseComplaintRequest(string Reason);

internal sealed class AdvanceComplaintValidator : AbstractValidator<AdvanceComplaintCommand>
{
    public AdvanceComplaintValidator()
    {
        RuleFor(c => c.ComplaintId).NotEmpty().OverridePropertyName("complaintId");

        // Required for everything but the acknowledgement, which carries nothing: the aggregate
        // refuses a blank too, and the validator is what turns it into a 400 naming the field.
        RuleFor(c => c.Text)
            .NotEmpty()
            .When(c => c.Action != ComplaintAction.Acknowledge)
            .WithMessage("An answer, an escalation target or a reason is required.")
            .OverridePropertyName("text");

        RuleFor(c => c.Text)
            .MaximumLength(8000)
            .When(c => c.Text is not null)
            .OverridePropertyName("text");
    }
}

internal sealed class AdvanceComplaintHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<AdvanceComplaintHandler> logger)
    : IRequestHandler<AdvanceComplaintCommand, Result<ComplaintDto>>
{
    public async Task<Result<ComplaintDto>> Handle(
        AdvanceComplaintCommand cmd, CancellationToken ct)
    {
        // The tenant query filter does the isolation: another tenant's complaint is simply absent
        // and the endpoint answers NOT_FOUND rather than FORBIDDEN.
        var complaint = await db.InsuranceComplaints
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ComplaintId, ct);

        if (complaint is null)
            return Result.Fail<ComplaintDto>(ComplaintErrors.NotFound);

        var actor = currentUser.Id;

        var outcome = cmd.Action switch
        {
            ComplaintAction.Acknowledge => complaint.Acknowledge(actor, clock),
            ComplaintAction.Escalate => complaint.Escalate(cmd.Text!, actor, clock),
            ComplaintAction.Resolve => complaint.Resolve(cmd.Text!, cmd.WasUpheld, actor, clock),
            ComplaintAction.Close => complaint.Close(cmd.Text!, actor, clock),
            _ => Result.Fail(ComplaintErrors.NotFound),
        };

        if (outcome.IsFailure)
            return Result.Fail<ComplaintDto>(outcome.Error!);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Complaint {Reference}: {Action} by {Actor}; now {Status}",
            complaint.Reference, cmd.Action, actor, complaint.Status);

        return Result.Ok(ComplaintDto.From(complaint, clock.GetUtcNow()));
    }
}
