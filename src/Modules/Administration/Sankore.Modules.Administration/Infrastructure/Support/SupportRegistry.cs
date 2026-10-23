namespace Sankore.Modules.Administration.Infrastructure.Support;

using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Features.Support;
using Sankore.Modules.Administration.Infrastructure.PaidModules;

// The registry's answers (Sankore.Admin's Features/Support), read with string enums whose names
// are the API's own (SupportTicketStatus…), so they deserialize straight into them.

internal sealed record RegistrySupportTicket(
    Guid Id,
    long Number,
    string Reference,
    Guid TenantId,
    string Subject,
    SupportTicketCategory Category,
    SupportTicketPriority Priority,
    SupportTicketStatus Status,
    Guid RequesterUserId,
    string RequesterName,
    string RequesterEmail,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

internal sealed record RegistrySupportMessage(
    Guid Id,
    SupportMessageKind Kind,
    SupportAuthorKind AuthorKind,
    Guid? AuthorId,
    string AuthorName,
    string? Body,
    SupportTicketStatus? Status,
    DateTimeOffset CreatedAt);

internal sealed record RegistrySupportTicketDetail(
    RegistrySupportTicket Ticket,
    string? PageUrl,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<RegistrySupportMessage> Messages);

internal sealed record RegistrySupportPage(IReadOnlyList<RegistrySupportTicket> Items, int Total, int Page, int PageSize);

internal sealed record RegistrySupportOpened(Guid Id, long Number, string Reference);

internal sealed record RegistrySupportActivity(
    Guid MessageId,
    Guid TicketId,
    long Number,
    string Reference,
    string Subject,
    Guid RequesterUserId,
    string RequesterEmail,
    SupportMessageKind Kind,
    SupportAuthorKind AuthorKind,
    string AuthorName,
    string? Body,
    SupportTicketStatus? Status,
    DateTimeOffset CreatedAt);

/// <summary>What a tenant user opens a ticket with; the registry stores it as given.</summary>
internal sealed record RegistryOpenTicket(
    Guid UserId,
    string Email,
    string FullName,
    string? Role,
    string Subject,
    SupportTicketCategory Category,
    SupportTicketPriority Priority,
    string Body,
    string? PageUrl,
    string? AppVersion,
    string? UserAgent);

/// <summary>
/// The tenant side of the support tickets in the registry. The tenant and the user are always the
/// caller's own — derived from the token by the handlers — because the service key this client
/// carries can act on any tenant. <c>requesterOnly</c> confines the user to their own tickets.
/// </summary>
internal interface ISupportRegistry
{
    Task<RegistryResult<RegistrySupportPage>> ListAsync(Guid tenantId, Guid? requesterUserId,
        SupportTicketStatus? status, bool openOnly, string? search, int page, int pageSize, CancellationToken ct);

    Task<RegistryResult<RegistrySupportOpened>> OpenAsync(Guid tenantId, RegistryOpenTicket ticket, CancellationToken ct);

    /// <summary>A null value means not found (or not the requester's).</summary>
    Task<RegistryResult<RegistrySupportTicketDetail?>> GetAsync(Guid tenantId, Guid ticketId, Guid? requesterUserId,
        CancellationToken ct);

    Task<RegistryResult<bool>> ReplyAsync(Guid tenantId, Guid ticketId, Guid userId, string fullName, bool requesterOnly,
        string body, CancellationToken ct);

    Task<RegistryResult<bool>> CloseAsync(Guid tenantId, Guid ticketId, Guid userId, string fullName, bool requesterOnly,
        CancellationToken ct);

    Task<RegistryResult<bool>> ReopenAsync(Guid tenantId, Guid ticketId, Guid userId, string fullName, bool requesterOnly,
        CancellationToken ct);

    Task<RegistryResult<IReadOnlyList<RegistrySupportActivity>>> ActivityAsync(Guid tenantId, DateTimeOffset since,
        CancellationToken ct);
}

internal sealed class HttpSupportRegistry(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpSupportRegistry> logger) : ISupportRegistry
{
    private static string Tickets(Guid tenantId) => $"api/v1/tenants/{tenantId}/support/tickets";

    public Task<RegistryResult<RegistrySupportPage>> ListAsync(Guid tenantId, Guid? requesterUserId,
        SupportTicketStatus? status, bool openOnly, string? search, int page, int pageSize, CancellationToken ct)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (requesterUserId is { } requester) query.Add($"requesterUserId={requester}");
        if (status is { } s) query.Add($"status={s}");
        if (openOnly) query.Add("openOnly=true");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search.Trim())}");

        return SendAsync(c => c.GetAsync($"{Tickets(tenantId)}/?{string.Join('&', query)}", ct),
            async r => (await r.Content.ReadFromJsonAsync<RegistrySupportPage>(TenantRegistryHttp.Json, ct))!, ct);
    }

    public Task<RegistryResult<RegistrySupportOpened>> OpenAsync(Guid tenantId, RegistryOpenTicket ticket,
        CancellationToken ct) =>
        SendAsync(c => c.PostAsJsonAsync($"{Tickets(tenantId)}/", ticket, TenantRegistryHttp.Json, ct),
            async r => (await r.Content.ReadFromJsonAsync<RegistrySupportOpened>(TenantRegistryHttp.Json, ct))!, ct);

    public async Task<RegistryResult<RegistrySupportTicketDetail?>> GetAsync(Guid tenantId, Guid ticketId,
        Guid? requesterUserId, CancellationToken ct)
    {
        var url = $"{Tickets(tenantId)}/{ticketId}" + (requesterUserId is { } r ? $"?requesterUserId={r}" : "");
        var answer = await SendAsync(c => c.GetAsync(url, ct),
            async response => await response.Content.ReadFromJsonAsync<RegistrySupportTicketDetail>(TenantRegistryHttp.Json, ct), ct);

        // The registry answers 404 with its code; the caller reads both the same way.
        return answer.Error == SupportTicketErrors.TicketNotFound
            ? new RegistryResult<RegistrySupportTicketDetail?>(null, null)
            : answer;
    }

    public Task<RegistryResult<bool>> ReplyAsync(Guid tenantId, Guid ticketId, Guid userId, string fullName,
        bool requesterOnly, string body, CancellationToken ct) =>
        SendAsync(c => c.PostAsJsonAsync($"{Tickets(tenantId)}/{ticketId}/messages",
                new { userId, fullName, requesterOnly, body }, TenantRegistryHttp.Json, ct),
            _ => Task.FromResult(true), ct);

    public Task<RegistryResult<bool>> CloseAsync(Guid tenantId, Guid ticketId, Guid userId, string fullName,
        bool requesterOnly, CancellationToken ct) =>
        SendAsync(c => c.PostAsJsonAsync($"{Tickets(tenantId)}/{ticketId}/close",
                new { userId, fullName, requesterOnly }, TenantRegistryHttp.Json, ct),
            _ => Task.FromResult(true), ct);

    public Task<RegistryResult<bool>> ReopenAsync(Guid tenantId, Guid ticketId, Guid userId, string fullName,
        bool requesterOnly, CancellationToken ct) =>
        SendAsync(c => c.PostAsJsonAsync($"{Tickets(tenantId)}/{ticketId}/reopen",
                new { userId, fullName, requesterOnly }, TenantRegistryHttp.Json, ct),
            _ => Task.FromResult(true), ct);

    public Task<RegistryResult<IReadOnlyList<RegistrySupportActivity>>> ActivityAsync(Guid tenantId, DateTimeOffset since,
        CancellationToken ct) =>
        SendAsync(c => c.GetAsync(
                $"api/v1/tenants/{tenantId}/support/activity/?since={Uri.EscapeDataString(since.UtcDateTime.ToString("O"))}", ct),
            async r => (IReadOnlyList<RegistrySupportActivity>)
                (await r.Content.ReadFromJsonAsync<List<RegistrySupportActivity>>(TenantRegistryHttp.Json, ct))!, ct);

    private Task<RegistryResult<T>> SendAsync<T>(
        Func<HttpClient, Task<HttpResponseMessage>> send,
        Func<HttpResponseMessage, Task<T>> read,
        CancellationToken ct) =>
        TenantRegistryHttp.SendAsync(httpClientFactory, logger, "a support call", send, read, ct);
}
