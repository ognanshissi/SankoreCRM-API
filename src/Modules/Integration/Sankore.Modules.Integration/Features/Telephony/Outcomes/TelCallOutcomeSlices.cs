namespace Sankore.Modules.Integration.Features.Telephony.Outcomes;

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// The tenant's call-outcome referential (TEL-B-11): one read for the wrap-up screen and three
/// writes for the settings one.
///
/// <para>
/// <b>Four slices in one file, which departs from the folder-per-slice convention of this
/// repository.</b> They are a referential's CRUD over a seven-field entity whose every rule is on
/// the aggregate: each handler is three lines, and four folders of four files would bury that
/// under structure. The convention earns its keep where a slice has a decision to make — the
/// wrap-up next door has its own folder for that reason.
/// </para>
/// </summary>
internal sealed record ListCallOutcomesQuery(bool IncludeInactive = false)
    : IRequest<Result<IReadOnlyList<CallOutcomeDto>>>;

/// <param name="IsSystem">
/// True for the platform baseline. The screen uses it to hide a delete that does not exist: a
/// seeded outcome may be renamed and deactivated, never removed, because a call wrapped up last
/// year points at its code.
/// </param>
internal sealed record CallOutcomeDto(
    string Code,
    string Label,
    bool CountsAsContact,
    bool RequiresCallbackDate,
    bool IsActive,
    bool IsSystem,
    int DisplayOrder);

internal sealed class ListCallOutcomesHandler(IntegrationDbContext db)
    : IRequestHandler<ListCallOutcomesQuery, Result<IReadOnlyList<CallOutcomeDto>>>
{
    public async Task<Result<IReadOnlyList<CallOutcomeDto>>> Handle(
        ListCallOutcomesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No tenant predicate: the global query filter scopes it.
        var rows = db.TelCallOutcomes.AsQueryable();

        if (!query.IncludeInactive)
            rows = rows.Where(o => o.IsActive);

        return Result.Ok<IReadOnlyList<CallOutcomeDto>>(await rows
            .OrderBy(o => o.DisplayOrder)
            .ThenBy(o => o.Code)
            .Select(o => new CallOutcomeDto(
                o.Code, o.Label, o.CountsAsContact, o.RequiresCallbackDate,
                o.IsActive, o.IsSystem, o.DisplayOrder))
            .ToListAsync(ct));
    }
}

// ── Create ──────────────────────────────────────────────────────────────────

/// <summary>
/// Adds an outcome of the tenant's own.
///
/// <para>
/// <c>IsSystem</c> is NOT a parameter: only the seeder creates baseline rows, and a flag a caller
/// could set would let an administrator mark their own outcome undeletable — or, worse, mark a
/// baseline one deletable.
/// </para>
/// </summary>
internal sealed record CreateCallOutcomeCommand(
    string Code,
    string Label,
    bool CountsAsContact,
    bool RequiresCallbackDate,
    int DisplayOrder)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyCallOutcome";

    public string? ResourceId => Code.Trim().ToUpperInvariant();
}

internal sealed class CreateCallOutcomeValidator : AbstractValidator<CreateCallOutcomeCommand>
{
    public CreateCallOutcomeValidator()
    {
        // Letters, digits and underscores only, upper-cased by the aggregate. A code is stored on
        // every call for years and read by an API: punctuation in one is a value somebody has to
        // quote in a URL later.
        RuleFor(x => x.Code)
            .NotEmpty()
            .MaximumLength(30)
            .Matches("^[A-Za-z0-9_]+$")
            .OverridePropertyName("code")
            .WithMessage("code is required, at most 30 characters, letters digits and underscores.");

        RuleFor(x => x.Label)
            .NotEmpty()
            .MaximumLength(100)
            .OverridePropertyName("label");

        RuleFor(x => x.DisplayOrder)
            .GreaterThanOrEqualTo(0)
            .OverridePropertyName("displayOrder");
    }
}

internal sealed class CreateCallOutcomeHandler(IntegrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<CreateCallOutcomeCommand, Result>
{
    public async Task<Result> Handle(CreateCallOutcomeCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var code = cmd.Code.Trim().ToUpperInvariant();

        // The read is the friendly answer; the composite primary key (tenant, code) is the
        // guarantee. Both, as everywhere in this module — the key alone would answer a bare 500
        // in the ordinary case of somebody re-adding a code they deactivated.
        if (await db.TelCallOutcomes.AnyAsync(o => o.Code == code, ct))
            return Result.Fail(TelephonyErrors.OutcomeCodeAlreadyUsed);

        db.TelCallOutcomes.Add(TelCallOutcome.Create(
            currentUser.TenantId, code, cmd.Label,
            cmd.CountsAsContact, cmd.RequiresCallbackDate, cmd.DisplayOrder));

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}

// ── Update ──────────────────────────────────────────────────────────────────

/// <summary>
/// Renames an outcome and moves it in the list.
///
/// <para>
/// <b>The two behaviour flags are not editable, and that is the point of the slice.</b>
/// <c>CountsAsContact</c> decides whether a call stops a lead's SLA clock; flipping it would
/// retroactively change what every call already wrapped up under that code MEANT — a month of
/// SLA figures recomputed by a settings screen. An institution that wants different behaviour
/// adds an outcome and deactivates the old one, which leaves the history saying what it said.
/// </para>
/// </summary>
internal sealed record UpdateCallOutcomeCommand(string Code, string Label, int DisplayOrder)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyCallOutcome";

    public string? ResourceId => Code.Trim().ToUpperInvariant();
}

internal sealed class UpdateCallOutcomeValidator : AbstractValidator<UpdateCallOutcomeCommand>
{
    public UpdateCallOutcomeValidator()
    {
        RuleFor(x => x.Code).NotEmpty().OverridePropertyName("code");

        RuleFor(x => x.Label)
            .NotEmpty()
            .MaximumLength(100)
            .OverridePropertyName("label");

        RuleFor(x => x.DisplayOrder)
            .GreaterThanOrEqualTo(0)
            .OverridePropertyName("displayOrder");
    }
}

internal sealed class UpdateCallOutcomeHandler(IntegrationDbContext db)
    : IRequestHandler<UpdateCallOutcomeCommand, Result>
{
    public async Task<Result> Handle(UpdateCallOutcomeCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var code = cmd.Code.Trim().ToUpperInvariant();

        var outcome = await db.TelCallOutcomes
            .AsTracking()
            .FirstOrDefaultAsync(o => o.Code == code, ct);

        if (outcome is null)
            return Result.Fail(TelephonyErrors.OutcomeNotFound);

        outcome.Rename(cmd.Label);
        outcome.MoveTo(cmd.DisplayOrder);

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}

// ── Activate / deactivate ───────────────────────────────────────────────────

/// <summary>
/// Takes an outcome out of the wrap-up list, or puts it back.
///
/// <para>
/// Never a delete, for a seeded outcome or any other: a call wrapped up last year points at the
/// code, and removing it would leave that call explained by nothing. The entity says so too.
/// </para>
/// </summary>
internal sealed record SetCallOutcomeActiveCommand(string Code, bool IsActive)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyCallOutcome";

    public string? ResourceId => Code.Trim().ToUpperInvariant();
}

internal sealed class SetCallOutcomeActiveHandler(IntegrationDbContext db)
    : IRequestHandler<SetCallOutcomeActiveCommand, Result>
{
    public async Task<Result> Handle(SetCallOutcomeActiveCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var code = cmd.Code.Trim().ToUpperInvariant();

        var outcome = await db.TelCallOutcomes
            .AsTracking()
            .FirstOrDefaultAsync(o => o.Code == code, ct);

        if (outcome is null)
            return Result.Fail(TelephonyErrors.OutcomeNotFound);

        if (cmd.IsActive) outcome.Reactivate();
        else outcome.Deactivate();

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
