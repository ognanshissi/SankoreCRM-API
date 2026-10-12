namespace Sankore.Modules.Integration.Features.Insurance.Claims.RecordIndemnity;

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// ASS-09, fifth criterion: the indemnity reached the customer's account, and the credit's
/// reference is recorded ON the dossier — « le versement apparaît dans le dossier » is then one
/// read and not a search through an account's transaction history.
///
/// <para>
/// <b>A command and not a consumer, deliberately.</b> The insurer tells us the AMOUNT (the sync
/// picks that up) and the core banking system tells us about the CREDIT — but an indemnity is not
/// paid by a queued command of ours: the insurer pays it, by transfer, into an account this
/// platform only observes. So the reference arrives through a human who reconciled it, which is
/// an operator action with an audit row, not an event.
/// </para>
/// </summary>
/// <param name="CbsReference">
/// The core banking reference of the credit. The same class of value as an external policy
/// number — an identifier an operator reads off a statement, not a credential.
/// </param>
internal sealed record RecordClaimIndemnityCommand(
    Guid ClaimId,
    decimal Amount,
    string Currency,
    string? CbsReference)
    : IRequest<Result<ClaimDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "InsuranceClaim";

    public string ResourceId => ClaimId.ToString();
}

public sealed record RecordClaimIndemnityRequest(
    decimal Amount,
    string Currency,
    string? CbsReference);

internal sealed class RecordClaimIndemnityValidator
    : AbstractValidator<RecordClaimIndemnityCommand>
{
    public RecordClaimIndemnityValidator()
    {
        RuleFor(c => c.ClaimId).NotEmpty().OverridePropertyName("claimId");

        RuleFor(c => c.Amount)
            .GreaterThan(0m)
            .WithMessage("An indemnity payment needs a positive amount.")
            .OverridePropertyName("amount");

        // Three letters, as the column is. No platform default: an amount without its currency is
        // not a sum of money, and a guess here would record a West-African payment in the wrong
        // unit — the same rule the subscription's premium follows.
        RuleFor(c => c.Currency)
            .NotEmpty()
            .Length(3)
            .WithMessage("A three-letter ISO currency code is required.")
            .OverridePropertyName("currency");

        RuleFor(c => c.CbsReference)
            .MaximumLength(200)
            .When(c => c.CbsReference is not null)
            .OverridePropertyName("cbsReference");
    }
}

internal sealed class RecordClaimIndemnityHandler(
    IntegrationDbContext db,
    TimeProvider clock,
    ILogger<RecordClaimIndemnityHandler> logger)
    : IRequestHandler<RecordClaimIndemnityCommand, Result<ClaimDto>>
{
    public async Task<Result<ClaimDto>> Handle(
        RecordClaimIndemnityCommand cmd, CancellationToken ct)
    {
        // The tenant query filter does the isolation: another tenant's dossier is simply absent
        // and the endpoint answers NOT_FOUND.
        var claim = await db.Claims
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ClaimId, ct);

        if (claim is null)
            return Result.Fail<ClaimDto>(ClaimErrors.ClaimNotFound);

        // An indemnity on a dossier the insurer REFUSED means two systems disagree about money.
        // Refused rather than honoured silently: whichever side is wrong, the correction is a
        // human decision and recording the payment would hide the contradiction.
        if (claim.Status == ClaimStatus.Refused)
        {
            logger.LogWarning(
                "Indemnity refused on claim {ClaimId}: the insurer reported the claim as refused",
                claim.Id);

            return Result.Fail<ClaimDto>($"{ClaimErrors.IndemnityConflict}:CLAIM_REFUSED");
        }

        // Already paid. The SAME reference succeeds and changes nothing — the operator clicked
        // twice, or two of them reconciled the same credit — and a DIFFERENT one is refused, the
        // same shape as a holding's external reference: two credit references for one indemnity
        // means one of them belongs to another dossier.
        if (claim.IndemnityCbsReference is { } already)
        {
            var incoming = cmd.CbsReference?.Trim();

            if (!string.Equals(already, incoming, StringComparison.Ordinal))
            {
                logger.LogError(
                    "Claim {ClaimId} already carries indemnity credit {Existing}; {Incoming} was "
                    + "submitted. Not overwritten.",
                    claim.Id, already, incoming);

                return Result.Fail<ClaimDto>(
                    $"{ClaimErrors.IndemnityConflict}:REFERENCE_ALREADY_RECORDED");
            }

            return Result.Ok(ClaimDto.From(claim));
        }

        try
        {
            claim.RecordIndemnityPayment(cmd.Amount, cmd.Currency, cmd.CbsReference, clock);
        }
        catch (DomainException ex)
        {
            return Result.Fail<ClaimDto>(ex.Message);
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Claim {ClaimId} indemnified: {Amount} {Currency}, credit {Reference}",
            claim.Id, cmd.Amount, cmd.Currency, cmd.CbsReference ?? "(none)");

        return Result.Ok(ClaimDto.From(claim));
    }
}
