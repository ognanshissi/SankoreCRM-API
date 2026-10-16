namespace Sankore.Modules.Integration.Features.Telephony.Recordings;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The tenant's recording policy: one read and one write (TEL-B-15).
///
/// <para>
/// Both in one file, like the call-outcome referential next door and for the same reason: the
/// decisions are all on the aggregate, and two folders of four files would bury two three-line
/// handlers under structure.
/// </para>
/// </summary>
internal sealed record GetRecordingPolicyQuery : IRequest<Result<RecordingPolicyDto>>;

/// <param name="Mode">
/// <c>Disabled</c> | <c>AllCalls</c> | <c>OnDemand</c>. <c>Disabled</c> for a tenant that has
/// never configured one, which is every tenant until a compliance officer decides otherwise.
/// </param>
/// <param name="UpdatedBy">
/// Null for a tenant with no row — there is nobody to name, because nobody has decided anything.
/// </param>
internal sealed record RecordingPolicyDto(
    TelRecordingMode Mode,
    string? LegalBasis,
    string? Purpose,
    int RetentionDays,
    bool DeleteAtProvider,
    int MaxDurationSeconds,
    Guid? UpdatedBy,
    DateTimeOffset? UpdatedAt);

internal sealed class GetRecordingPolicyHandler(
    TelephonyRecordingPolicyReader reader, IntegrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetRecordingPolicyQuery, Result<RecordingPolicyDto>>
{
    public async Task<Result<RecordingPolicyDto>> Handle(
        GetRecordingPolicyQuery query, CancellationToken ct)
    {
        var policy = await reader.ReadAsync(currentUser.TenantId, ct);

        // Whether a ROW exists, which the reader deliberately hides from its callers: the jobs do
        // not care, and the screen does — "nobody has decided" and "somebody decided Disabled" are
        // different things to show a compliance officer.
        var configured = await db.TelRecordingPolicies
            .IgnoreQueryFilters()
            .AnyAsync(p => p.TenantId == currentUser.TenantId, ct);

        return Result.Ok(new RecordingPolicyDto(
            policy.Mode,
            policy.LegalBasis,
            policy.Purpose,
            policy.RetentionDays,
            policy.DeleteAtProvider,
            policy.MaxDurationSeconds,
            configured ? policy.UpdatedBy : null,
            configured ? policy.UpdatedAt : null));
    }
}

/// <summary>
/// A compliance officer's decision about recording (TEL-B-15).
///
/// <para>
/// <c>IResourceCommand</c>, and the resource is the TENANT: what an audit of this is asked
/// afterwards is who switched recording on, when, and under what stated basis — and
/// <c>AuditBehavior</c> serialises the payload, so the basis and the purpose are in the audit row
/// as they were written.
/// </para>
/// </summary>
internal sealed record SetRecordingPolicyCommand(
    TelRecordingMode Mode,
    string? LegalBasis,
    string? Purpose,
    int RetentionDays,
    bool DeleteAtProvider,
    int MaxDurationSeconds)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyRecordingPolicy";

    public string? ResourceId => null;
}

internal sealed class SetRecordingPolicyValidator : AbstractValidator<SetRecordingPolicyCommand>
{
    public SetRecordingPolicyValidator()
    {
        RuleFor(x => x.Mode).IsInEnum().OverridePropertyName("mode");

        // The basis and the purpose are NOT required here, although the aggregate requires them
        // for any mode but Disabled. Deliberate: the aggregate's refusal carries a code the screen
        // branches on (TELEPHONY_RECORDING_LEGAL_BASIS_REQUIRED), while a validator would answer a
        // field-shaped 422 — and this is the one rule a compliance officer most needs named rather
        // than rendered as "this field is required".
        RuleFor(x => x.LegalBasis)
            .MaximumLength(1000)
            .When(x => x.LegalBasis is not null)
            .OverridePropertyName("legalBasis");

        RuleFor(x => x.Purpose)
            .MaximumLength(1000)
            .When(x => x.Purpose is not null)
            .OverridePropertyName("purpose");

        // Ten years. Not a legal limit — there is none this platform can assert for every country
        // — but a ceiling that makes a typo visible: 36500 entered for 3650 is a century of
        // somebody's voice, and nothing downstream would question it.
        RuleFor(x => x.RetentionDays)
            .InclusiveBetween(1, 3650)
            .OverridePropertyName("retentionDays");

        // Twelve hours. Longer than any single call anybody has, and short enough that a ceiling
        // of 86400 cannot be set by accident on a feature whose whole exposure is file size.
        RuleFor(x => x.MaxDurationSeconds)
            .InclusiveBetween(30, 12 * 60 * 60)
            .OverridePropertyName("maxDurationSeconds");
    }
}

internal sealed class SetRecordingPolicyHandler(
    IntegrationDbContext db, ICurrentUser currentUser, TimeProvider clock)
    : IRequestHandler<SetRecordingPolicyCommand, Result>
{
    public async Task<Result> Handle(SetRecordingPolicyCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var now = clock.GetUtcNow();

        var policy = await db.TelRecordingPolicies
            .AsTracking()
            .FirstOrDefaultAsync(p => p.TenantId == currentUser.TenantId, ct);

        if (policy is null)
        {
            policy = TelRecordingPolicy.Create(currentUser.TenantId, currentUser.Id, now);
            db.TelRecordingPolicies.Add(policy);
        }

        var applied = policy.Apply(
            cmd.Mode, cmd.LegalBasis, cmd.Purpose, cmd.RetentionDays,
            cmd.DeleteAtProvider, cmd.MaxDurationSeconds, currentUser.Id, now);

        if (applied.IsFailure)
            return applied;

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}

internal static class RecordingPolicyEndpoints
{
    internal static IEndpointRouteBuilder MapRecordingPolicy(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("recording-policy", GetAsync)
            .WithName("GetTelephonyRecordingPolicy")
            .WithSummary("Whether this institution keeps call recordings, and under what basis")
            .WithDescription(
                "Disabled for a tenant that has never configured one, which is every tenant until "
                + "a compliance officer decides otherwise — the feature ships built and switched "
                + "OFF, because a recording is a person's voice and the lawful basis for keeping "
                + "one differs by country and by what the customer was told before the tone. "
                + "updatedBy is null when nobody has decided anything, which is a different thing "
                + "from somebody having chosen Disabled. "
                + "Requires permission: admin:compliance:manage.")
            .RequireAuthorization(Permissions.CanManageCompliance.Code)
            .Produces<RecordingPolicyDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPut("recording-policy", SetAsync)
            .WithName("SetTelephonyRecordingPolicy")
            .WithSummary("Decide whether and which calls are recorded")
            .WithDescription(
                "Any mode but Disabled REQUIRES a legal basis and a purpose: 422 "
                + "TELEPHONY_RECORDING_LEGAL_BASIS_REQUIRED / "
                + "TELEPHONY_RECORDING_PURPOSE_REQUIRED. They are free text in your own words — "
                + "the bases differ by regulator and an enum would make the platform's list a "
                + "constraint on what you may claim — and each is copied onto every recording at "
                + "retrieval and FROZEN there, so shortening a retention never reaches back and "
                + "lengthening one never extends a file already taken. "
                + "Switching back to Disabled keeps the words, because the recordings already "
                + "taken are still held under them. "
                + "OnDemand keeps only the calls an agent asked to keep — the mode for a basis of "
                + "consent, which is given per conversation. "
                + "deleteAtProvider is honoured only where the provider supports deletion. "
                + "Audited, payload included. Requires permission: admin:compliance:manage.")
            .RequireAuthorization(Permissions.CanManageCompliance.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> GetAsync(ISender sender, CancellationToken ct)
        => Results.Ok((await sender.Send(new GetRecordingPolicyQuery(), ct)).Value);

    private static async Task<IResult> SetAsync(
        SetRecordingPolicyRequest request, ISender sender, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(new SetRecordingPolicyCommand(
            request.Mode,
            request.LegalBasis,
            request.Purpose,
            request.RetentionDays ?? TelRecordingPolicy.DefaultRetentionDays,
            request.DeleteAtProvider ?? false,
            request.MaxDurationSeconds ?? TelRecordingPolicy.DefaultMaxDurationSeconds), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.UnprocessableEntity(new { error = result.Error });
    }
}

/// <param name="RetentionDays">
/// Null takes the platform default of ninety days. Nullable rather than required so a compliance
/// officer switching the feature on does not have to answer four questions to answer one.
/// </param>
internal sealed record SetRecordingPolicyRequest(
    TelRecordingMode Mode,
    string? LegalBasis,
    string? Purpose,
    int? RetentionDays,
    bool? DeleteAtProvider,
    int? MaxDurationSeconds);
