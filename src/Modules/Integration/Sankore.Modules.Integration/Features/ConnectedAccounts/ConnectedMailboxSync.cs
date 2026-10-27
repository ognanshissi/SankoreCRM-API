namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

internal sealed record MailSyncOutcome(int Kept, int Updated, int Removed, IReadOnlyList<string> Failures);

/// <summary>
/// Connected mailbox, lot 2: reads what changed in a mailbox and keeps ONLY the e-mails exchanged
/// with a lead or a client (owner's decision, 2026-10-27).
///
/// <para>
/// <b>Headers first, content only when kept.</b> Each page of changes is matched as a whole — one
/// batch question to M13 and one to M01 for every address on the page — and only a message naming
/// a known address has its body fetched, encrypted and stored. Everything else was read and is
/// forgotten: no row, no log line naming it. A message already kept is refreshed (read, folder)
/// without a second fetch.
/// </para>
///
/// <para>
/// <b>The cursor moves only after its page is saved.</b> A failure mid-page leaves the cursor where
/// it was and the next run reads the page again; the unique index on (account, external id) makes
/// that replay harmless. A content fetch that fails therefore fails the PAGE — skipping the message
/// would advance past it and lose it for good.
/// </para>
///
/// <para>
/// <b>Not retroactive.</b> An e-mail exchanged before its address became a lead's was read then and
/// not kept; it is not looked for again when the lead is created. A later improvement, deliberately
/// out of this lot.
/// </para>
/// </summary>
internal sealed class ConnectedMailboxSync(
    IntegrationDbContext db,
    IEnumerable<IMailboxReader> readers,
    CalendarEventProtector protector,
    TimeProvider clock,
    ILogger<ConnectedMailboxSync> logger,
    ILeadsModule? leads = null,
    ICustomersModule? customers = null)
{
    /// <summary>Pages per stream per run: a backlog finishes over the following runs instead of holding a worker.</summary>
    internal const int MaxPages = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<MailSyncOutcome>> SyncAccountAsync(ConnectedAccount account, CancellationToken ct)
    {
        if (!account.IsMailEnabled || account.Status == ConnectedAccountStatus.NeedsReconnect)
            return Result.Ok(new MailSyncOutcome(0, 0, 0, []));

        var reader = readers.FirstOrDefault(r => r.CanRead(account));
        if (reader is null) return Result.Fail<MailSyncOutcome>(ConnectedAccountErrors.ProviderUnavailable);

        var opened = await reader.OpenAsync(account, ct);
        if (opened.IsFailure)
        {
            // A refused password is the IMAP twin of a revoked token: the user has to act.
            if (opened.Error == MailServerErrors.AuthenticationFailed(MailServerStep.Imap)) account.MarkNeedsReconnect(opened.Error!);
            account.RecordMailFailure(opened.Error!);
            await db.SaveChangesAsync(ct);
            return Result.Fail<MailSyncOutcome>(opened.Error!);
        }

        await using var session = opened.Value!;

        var since = clock.GetUtcNow() - MailboxSyncState.Window;
        var (kept, updated, removed) = (0, 0, 0);
        var failures = new List<string>();

        foreach (var stream in reader.Streams)
        {
            var state = await db.MailboxSyncStates.AsTracking().FirstOrDefaultAsync(s => s.AccountId == account.Id && s.Stream == stream, ct);
            if (state is null)
            {
                state = MailboxSyncState.Start(account.TenantId, account.Id, stream);
                db.MailboxSyncStates.Add(state);
            }

            try
            {
                for (var page = 0; page < MaxPages; page++)
                {
                    MailChangePage changes;

                    try
                    {
                        changes = await reader.ReadChangesAsync(session, stream, state.Cursor, since, ct);
                    }
                    catch (MailCursorExpiredException) when (state.Cursor is not null)
                    {
                        logger.LogInformation("Mailbox {AccountId} ({Stream}): cursor expired, reading the window again.", account.Id, stream);
                        state.Reset();
                        continue;
                    }

                    var (k, u, r) = await ApplyAsync(account, reader, session, changes, ct);
                    (kept, updated, removed) = (kept + k, updated + u, removed + r);

                    state.Advance(changes.Cursor, clock.GetUtcNow());
                    await db.SaveChangesAsync(ct);

                    if (!changes.HasMore) break;
                }
            }
            catch (ProviderAuthException ex)
            {
                logger.LogWarning("Mailbox {AccountId} ({Stream}): read failed ({Code}).", account.Id, stream, ex.Code);
                DropUnsaved();
                state.RecordFailure(ex.Code);
                if (ex.Revoked) account.MarkNeedsReconnect(ex.Code);
                failures.Add(ex.Code);
                await db.SaveChangesAsync(ct);
                if (ex.Revoked) break;
            }
            catch (Exception ex) when (ex is IOException or MailKit.ProtocolException or MailKit.CommandException or TimeoutException)
            {
                logger.LogWarning("Mailbox {AccountId} ({Stream}): read failed ({Reason}).", account.Id, stream, ex.GetType().Name);
                DropUnsaved();
                state.RecordFailure("mail_server_error");
                failures.Add("mail_server_error");
                await db.SaveChangesAsync(ct);
            }
        }

        if (failures.Count == 0) account.RecordMailSync(clock.GetUtcNow());
        else account.RecordMailFailure(failures[0]);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new MailSyncOutcome(kept, updated, removed, failures));
    }

    /// <summary>A page that failed half-way: what it had added is not saved, the cursor stays.</summary>
    private void DropUnsaved()
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added
                     && e.Entity is ConnectedMailMessage or ConnectedMailLink).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task<(int Kept, int Updated, int Removed)> ApplyAsync(
        ConnectedAccount account, IMailboxReader reader, MailboxSession session, MailChangePage changes, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var removed = changes.Removed.Count == 0 ? 0 : await ForgetAsync(db, account.Id, changes.Removed, ct);

        if (changes.Upserts.Count == 0) return (0, 0, removed);

        var ids = changes.Upserts.Select(h => h.ExternalId).Distinct().ToList();
        var existing = await db.ConnectedMailMessages.AsTracking()
            .Where(m => m.AccountId == account.Id && ids.Contains(m.ExternalId))
            .ToDictionaryAsync(m => m.ExternalId, ct);

        var updated = 0;
        foreach (var header in changes.Upserts.Where(h => existing.ContainsKey(h.ExternalId)))
        {
            if (existing[header.ExternalId].Refresh(header.IsRead, header.Folder, now)) updated++;
        }

        var candidates = changes.Upserts.Where(h => !existing.ContainsKey(h.ExternalId)).DistinctBy(h => h.ExternalId).ToList();
        if (candidates.Count == 0) return (0, updated, removed);

        var own = account.Email.Trim().ToLowerInvariant();
        var addresses = candidates
            .SelectMany(h => h.Parties)
            .Select(p => p.Address.Trim().ToLowerInvariant())
            .Where(a => a.Length > 0 && a != own)
            .ToHashSet(StringComparer.Ordinal);

        var known = await MatchAsync(account.TenantId, addresses, ct);
        if (known.Count == 0) return (0, updated, removed);

        var kept = 0;
        foreach (var header in candidates)
        {
            var links = header.Parties
                .Select(p => p.Address.Trim().ToLowerInvariant())
                .Where(a => a != own)
                .SelectMany(a => known.GetValueOrDefault(a) ?? [])
                .Distinct()
                .ToList();

            if (links.Count == 0) continue;

            var content = await reader.ReadContentAsync(session, header.ExternalId, ct);
            if (content is null) continue;

            var direction = header.Folder == MailFolderKind.Sent
                            || string.Equals(header.From?.Address, account.Email, StringComparison.OrdinalIgnoreCase)
                ? MailDirection.Sent
                : MailDirection.Received;

            var message = ConnectedMailMessage.Create(
                account.TenantId, account.Id, account.UserId, header.ExternalId, header.InternetMessageId, header.ThreadKey,
                header.Folder, direction, header.SentAt, header.IsRead || direction == MailDirection.Sent,
                content.Attachments.Any(a => !a.IsInline), now);

            var body = content.Body;
            var truncated = body is { Length: > ConnectedMailMessage.BodyMaxLength };
            if (truncated) body = body![..ConnectedMailMessage.BodyMaxLength];

            message.SetContent(
                Protect(header.From),
                Protect(header.To),
                Protect(header.Cc),
                protector.Protect(header.Subject),
                protector.Protect(Snippet(header.Snippet, content)),
                protector.Protect(body),
                content.IsHtml,
                truncated,
                content.Attachments.Count == 0 ? null : Protect(content.Attachments));

            db.ConnectedMailMessages.Add(message);
            foreach (var (type, entityId) in links) db.ConnectedMailLinks.Add(ConnectedMailLink.For(message, type, entityId));
            kept++;
        }

        return (kept, updated, removed);
    }

    /// <summary>Address → the leads and clients holding it. A module that cannot answer answers nothing: no match, nothing kept.</summary>
    private async Task<Dictionary<string, List<(MailLinkEntity, Guid)>>> MatchAsync(Guid tenantId, HashSet<string> addresses, CancellationToken ct)
    {
        var known = new Dictionary<string, List<(MailLinkEntity, Guid)>>(StringComparer.Ordinal);
        if (addresses.Count == 0) return known;

        void Add(string email, MailLinkEntity type, Guid id)
        {
            if (!known.TryGetValue(email, out var list)) known[email] = list = [];
            list.Add((type, id));
        }

        // The cursor must not move past messages nobody could match: an unreachable module fails the page.
        if (leads is not null)
        {
            foreach (var m in await leads.MatchLeadsByEmailsAsync(tenantId, addresses, ct)) Add(m.Email, MailLinkEntity.Lead, m.LeadId);
        }

        if (customers is not null)
        {
            foreach (var m in await customers.MatchClientsByEmailsAsync(tenantId, addresses, ct)) Add(m.Email, MailLinkEntity.Client, m.ClientId);
        }

        return known;
    }

    /// <summary>Graph and Gmail give a preview; IMAP does not, so one is cut from the body.</summary>
    private static string? Snippet(string? snippet, MailContent content)
    {
        if (!string.IsNullOrWhiteSpace(snippet)) return snippet.Trim();
        if (string.IsNullOrWhiteSpace(content.Body)) return null;

        var text = content.IsHtml ? MailText.FromHtml(content.Body) : content.Body;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 200 ? text : text[..200];
    }

    private string? Protect<T>(T? value) =>
        value is null || (value is System.Collections.ICollection { Count: 0 }) ? null : protector.Protect(JsonSerializer.Serialize(value, Json));

    /// <summary>Subject, preview and body are stored as plain ciphertext; addresses and attachments as ciphertext of JSON.</summary>
    internal string? RevealText(string? stored) => protector.Reveal(stored);

    internal T? Reveal<T>(string? stored) =>
        protector.Reveal(stored) is { } json ? JsonSerializer.Deserialize<T>(json, Json) : default;

    /// <summary>
    /// Deletes kept messages and their links — the provider removed them, the mailbox was turned
    /// off, the account removed. Links are deleted explicitly: the database cascades, the test
    /// provider does not.
    /// </summary>
    internal static async Task<int> ForgetAsync(IntegrationDbContext db, Guid accountId, IReadOnlyCollection<string>? externalIds, CancellationToken ct)
    {
        var query = db.ConnectedMailMessages.AsTracking().Where(m => m.AccountId == accountId);
        if (externalIds is not null) query = query.Where(m => externalIds.Contains(m.ExternalId));

        var messages = await query.ToListAsync(ct);
        if (messages.Count == 0) return 0;

        var ids = messages.Select(m => m.Id).ToList();
        db.ConnectedMailLinks.RemoveRange(await db.ConnectedMailLinks.AsTracking().Where(l => ids.Contains(l.MessageId)).ToListAsync(ct));
        db.ConnectedMailMessages.RemoveRange(messages);
        return messages.Count;
    }

    /// <summary>Everything a mailbox brought: its messages, links and cursors.</summary>
    internal static async Task ForgetMailboxAsync(IntegrationDbContext db, Guid accountId, CancellationToken ct)
    {
        await ForgetAsync(db, accountId, null, ct);
        db.MailboxSyncStates.RemoveRange(await db.MailboxSyncStates.AsTracking().Where(s => s.AccountId == accountId).ToListAsync(ct));
    }
}

// ── Jobs ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>Every 5 minutes, GLOBAL: one <see cref="SyncConnectedMailboxesJob"/> per active tenant — the agendas' shape.</summary>
public sealed class SyncConnectedMailboxesOrchestrator(
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<SyncConnectedMailboxesOrchestrator> logger)
{
    public const string CronExpression = "*/5 * * * *";
    public const string RecurringJobId = "integration-connected-mailboxes-sync";

    public async Task ExecuteAsync()
    {
        var enqueued = 0;

        foreach (var tenant in await tenantStore.GetAllActiveAsync(CancellationToken.None))
        {
            var tenantId = tenant.Id;
            if (tenantId == Guid.Empty) continue;

            hangfire.Enqueue<SyncConnectedMailboxesJob>(job => job.ExecuteAsync(tenantId));
            enqueued++;
        }

        logger.LogDebug("Connected mailboxes: {Enqueued} tenant run(s) enqueued.", enqueued);
    }
}

/// <summary>One tenant's mailboxes, one after the other, each in its own scope.</summary>
public sealed class SyncConnectedMailboxesJob(IServiceScopeFactory scopeFactory, ILogger<SyncConnectedMailboxesJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        List<Guid> ids;

        using (var scope = scopeFactory.CreateScope())
        {
            ids = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().ConnectedAccounts
                .Where(a => a.IsMailEnabled && a.Status == ConnectedAccountStatus.Active)
                .Select(a => a.Id)
                .ToListAsync();
        }

        foreach (var id in ids) await SyncConnectedMailboxJob.RunAsync(scopeFactory, logger, id);
    }
}

/// <summary>Reads one mailbox now — after it is connected or turned on, or on « Synchroniser ».</summary>
public sealed class SyncConnectedMailboxJob(IServiceScopeFactory scopeFactory, ILogger<SyncConnectedMailboxJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task ExecuteAsync(Guid tenantId, Guid accountId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        await RunAsync(scopeFactory, logger, accountId);
    }

    internal static async Task RunAsync(IServiceScopeFactory scopeFactory, ILogger logger, Guid accountId)
    {
        // A scope per account: one mailbox's tracked entities and failures never leak into the next.
        using var scope = scopeFactory.CreateScope();

        try
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var account = await db.ConnectedAccounts.AsTracking().FirstOrDefaultAsync(a => a.Id == accountId);
            if (account is null) return;

            var result = await scope.ServiceProvider.GetRequiredService<ConnectedMailboxSync>().SyncAccountAsync(account, CancellationToken.None);
            if (result.IsFailure) logger.LogInformation("Mailbox {AccountId}: sync not done ({Code}).", accountId, result.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Mailbox {AccountId}: sync failed.", accountId);
        }
    }
}
