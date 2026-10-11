namespace Sankore.Modules.Customers.Features.Compliance.ProfileNotices;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class RecordProfileNoticeHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    TimeProvider clock)
    : IRequestHandler<RecordProfileNoticeCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        RecordProfileNoticeCommand cmd, CancellationToken ct)
    {
        var client = await db.Clients
            .Where(c => c.Id == cmd.ClientId)
            .Select(c => new { c.AgencyId })
            .FirstOrDefaultAsync(ct);

        // Outside the perimeter reads as NOT FOUND, never forbidden.
        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(
                currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
        {
            return Result.Fail<Guid>(CustomerErrors.ClientNotFound);
        }

        // A notice is recorded on an ARCHIVED client too, deliberately: learning that an archived
        // client has moved is still something an institution learned, and the read-only rule is
        // about editing the file.
        var notice = ClientProfileChangeNotice.Record(
            tenantId: currentUser.TenantId,
            clientId: cmd.ClientId,
            kind: cmd.Kind,
            noticedOn: cmd.NoticedOn,
            recordedBy: currentUser.Id,
            now: clock.GetUtcNow(),
            note: cmd.Note);

        db.ClientProfileChangeNotices.Add(notice);
        await db.SaveChangesAsync(ct);

        return Result.Ok(notice.Id);
    }
}

internal sealed class IntegrateProfileNoticeHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    TimeProvider clock)
    : IRequestHandler<IntegrateProfileNoticeCommand, Result>
{
    public async Task<Result> Handle(IntegrateProfileNoticeCommand cmd, CancellationToken ct)
    {
        var notice = await ProfileNoticeAccess.LoadInPerimeterAsync(
            db, agencyScope, currentUser, cmd.NoticeId, ct);

        if (notice is null)
            return Result.Fail(CustomerErrors.ProfileNoticeNotFound);

        var integrated = notice.Integrate(
            currentUser.Id, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

        if (integrated.IsFailure) return integrated;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal sealed class DismissProfileNoticeHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    TimeProvider clock)
    : IRequestHandler<DismissProfileNoticeCommand, Result>
{
    public async Task<Result> Handle(DismissProfileNoticeCommand cmd, CancellationToken ct)
    {
        var notice = await ProfileNoticeAccess.LoadInPerimeterAsync(
            db, agencyScope, currentUser, cmd.NoticeId, ct);

        if (notice is null)
            return Result.Fail(CustomerErrors.ProfileNoticeNotFound);

        var dismissed = notice.Dismiss(
            cmd.Reason, currentUser.Id, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

        if (dismissed.IsFailure) return dismissed;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal sealed class OverdueProfileNoticesHandler(
    CustomersDbContext db,
    ICustomerSettings settings,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    TimeProvider clock)
    : IRequestHandler<OverdueProfileNoticesQuery, Result<PagedResult<OverdueProfileNoticeDto>>>
{
    public async Task<Result<PagedResult<OverdueProfileNoticeDto>>> Handle(
        OverdueProfileNoticesQuery query, CancellationToken ct)
    {
        var allowedDays = await settings.GetIntAsync(
            currentUser.TenantId, CustomerSettingKeys.ProfileChangeIntegrationDays, ct);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // A notice becomes overdue the day AFTER the allowance runs out, which is what
        // ClientProfileChangeNotice.IsOverdue encodes — the cutoff below is the SQL form of the
        // same comparison, and the two must agree. Expressed as a date rather than a day count so
        // the index on (tenant_id, noticed_on) is usable.
        var cutoff = today.AddDays(-allowedDays);

        // Open notices only: the filtered index covers exactly these rows.
        var overdue = db.ClientProfileChangeNotices
            .Where(n => n.IntegratedOn == null
                     && n.DismissedReason == null
                     && n.NoticedOn < cutoff);

        // ── The agency perimeter ────────────────────────────────────────────
        //
        // Applied here and not left to the tenant filter, because each row carries a CLIENT
        // NUMBER: without it an agent of one branch could enumerate the clients of another, and
        // M01's rule is that the existence of a client must not leak across the perimeter — which
        // is why a single out-of-perimeter read answers CLIENT_NOT_FOUND and never 403.
        //
        // null means UNRESTRICTED (a super-user, or a tenant-wide account) and must be treated as
        // "apply no filter", never as "no access" — the one way to read this contract wrongly.
        var accessible = await agencyScope.GetAccessibleAgencyIdsAsync(
            currentUser.TenantId, currentUser.Id, ct);

        if (accessible is not null)
        {
            // Materialised as a List: on .NET 10 a set's Contains does not translate to SQL the
            // way a List's does.
            var agencyIds = accessible.ToList();

            overdue = overdue.Where(
                n => db.Clients.Any(c => c.Id == n.ClientId && agencyIds.Contains(c.AgencyId)));
        }

        var total = await overdue.CountAsync(ct);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        // Oldest first: the longest breach is the one a supervisor asks about.
        var rows = await overdue
            .OrderBy(n => n.NoticedOn)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Join(
                db.Clients,
                notice => notice.ClientId,
                client => client.Id,
                (notice, client) => new { Notice = notice, client.ClientNumber })
            .ToListAsync(ct);

        var items = rows
            .Select(r => new OverdueProfileNoticeDto(
                NoticeId: r.Notice.Id,
                ClientId: r.Notice.ClientId,
                ClientNumber: r.ClientNumber,
                Kind: r.Notice.Kind.ToString(),
                // Through the aggregate's own definition, not a subtraction repeated here.
                ElapsedDays: r.Notice.ElapsedDays(today),
                AllowedDays: allowedDays,
                NoticedOn: r.Notice.NoticedOn,
                RecordedBy: r.Notice.RecordedBy))
            .ToList();

        return Result.Ok(new PagedResult<OverdueProfileNoticeDto>(items, total, page, pageSize));
    }
}

/// <summary>
/// Loading a notice for a WRITE, inside the caller's agency perimeter.
///
/// <para>
/// Shared by the two closing handlers because the alternative is what this file shipped with: the
/// write that CREATES a notice checked the perimeter and the two that CLOSE it did not. The ids
/// are Guid v4, so blind guessing is unrealistic — and an unguessable id is not an access control,
/// which is the rule this repository already applied to <c>BirthDatesAsync</c>. The effect here is
/// worse than a read: closing a notice removes an overdue compliance obligation from the report,
/// so the asymmetry let an agent of one branch hide another branch's breach.
/// </para>
///
/// <para>
/// Answers <c>null</c> for "not found" AND for "outside the perimeter", deliberately
/// indistinguishable: both reach the caller as <c>PROFILE_NOTICE_NOT_FOUND</c>, because a 403
/// would confirm the notice exists somewhere.
/// </para>
/// </summary>
internal static class ProfileNoticeAccess
{
    internal static async Task<ClientProfileChangeNotice?> LoadInPerimeterAsync(
        CustomersDbContext db,
        IAgencyScopeProvider agencyScope,
        ICurrentUser currentUser,
        Guid noticeId,
        CancellationToken ct)
    {
        // One query for the notice and its client's agency: the perimeter check needs the agency,
        // and loading the notice first would mean a second round trip to find out whether we were
        // allowed to have loaded it.
        var row = await db.ClientProfileChangeNotices
            .AsTracking()
            .Where(n => n.Id == noticeId)
            .Join(
                db.Clients,
                notice => notice.ClientId,
                client => client.Id,
                (notice, client) => new { Notice = notice, client.AgencyId })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        return await agencyScope.CanAccessAgencyAsync(
                currentUser.TenantId, currentUser.Id, row.AgencyId, ct)
            ? row.Notice
            : null;
    }
}
