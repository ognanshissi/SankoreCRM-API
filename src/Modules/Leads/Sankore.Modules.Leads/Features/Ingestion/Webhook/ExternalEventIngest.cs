namespace Sankore.Modules.Leads.Features.Ingestion.Webhook;

using System.Text.Json;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel;

// ── Endpoint ─────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// <c>POST /api/ingest/hooks/{publicKey}/events</c> (webhooks lot 6): a partner reports something
/// about a lead it sent — « rendez-vous pris », « dossier complété ». Same source, same secret,
/// same IP allow-list as the leads endpoint (<see cref="WebhookSourceGate"/>):
/// <code>
/// { "id": "evt-123", "type": "appointment.booked",
///   "lead": { "externalId": "ext-9" }  // or { "phone": "+225…" }
///   "data": { "agence": "Plateau", "montant": 150000 } }
/// </code>
/// A matched event starts the workflows whose <c>ExternalEvent</c> trigger names its type.
/// </summary>
public static class ExternalEventIngestEndpoint
{
    public static IEndpointRouteBuilder MapExternalEventIngestEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest/hooks/{publicKey}/events", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(WebhookIngestEndpoint.RateLimitPolicy)
            .WithName("ExternalEventIngest")
            .WithTags("Ingest")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> HandleAsync(string publicKey, HttpContext httpContext, ISender sender, CancellationToken ct)
    {
        var body = await WebhookIngestEndpoint.ReadBodyAsync(httpContext, ct);
        if (body is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var result = await sender.Send(new IngestExternalEventCommand(
            publicKey, body, WebhookIngestEndpoint.SignatureHeaders(httpContext),
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"), ct);

        var r = result.Value!;
        return r.Status is null
            ? Results.StatusCode(r.StatusCode)
            : Results.Json(new { status = r.Status, eventId = r.EventId, leadId = r.LeadId, error = r.Error }, statusCode: r.StatusCode);
    }
}

// ── Command ──────────────────────────────────────────────────────────────────────────────────

internal sealed record IngestExternalEventCommand(
    string PublicKey,
    string Body,
    IReadOnlyDictionary<string, string> SignatureHeaders,
    string RemoteIp
) : IRequest<Result<ExternalEventResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadExternalEvent";
    public string? ResourceId => null;
}

/// <param name="Status"><c>Matched</c>, <c>Unmatched</c>, <c>Duplicate</c> or <c>Rejected</c>; null for a bare refusal.</param>
internal sealed record ExternalEventResult(int StatusCode, string? Status, Guid? EventId = null, Guid? LeadId = null, string? Error = null);

internal sealed partial class IngestExternalEventHandler(
    LeadsDbContext db,
    WebhookSourceGate gate,
    IPhoneBlindIndexer phoneIndexer,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    TimeProvider clock)
    : IRequestHandler<IngestExternalEventCommand, Result<ExternalEventResult>>
{
    internal const int MaxDataKeys = 30;
    internal const int MaxDataStringLength = 500;

    public async Task<Result<ExternalEventResult>> Handle(IngestExternalEventCommand cmd, CancellationToken ct)
    {
        var (source, status) = await gate.AuthenticateAsync(cmd.PublicKey, cmd.Body, cmd.SignatureHeaders, cmd.RemoteIp, ct);
        if (source is null)
            return Result.Ok(new ExternalEventResult(status, null));

        var parsed = Parse(cmd.Body);
        if (parsed.Error is not null)
            return Result.Ok(new ExternalEventResult(400, "Rejected", Error: parsed.Error));

        // ── Idempotence: the partner's own event id, per source ─────────────────────────────
        var existing = await db.LeadExternalEvents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.TenantId == source.TenantId && e.SourceId == source.Id
                                      && e.ExternalEventId == parsed.Id, ct);
        if (existing is not null)
            return Result.Ok(new ExternalEventResult(200, "Duplicate", existing.Id, existing.LeadId));

        // ── Which lead ──────────────────────────────────────────────────────────────────────
        var (leadId, matchedBy) = await MatchAsync(source, parsed.ExternalId, parsed.Phone, ct);

        var row = LeadExternalEvent.Record(
            source.TenantId, source.Id, parsed.Id!, parsed.Type!, leadId, matchedBy, cmd.Body, clock.GetUtcNow());
        db.LeadExternalEvents.Add(row);

        if (leadId is { } id)
        {
            var context = new Dictionary<string, object>(parsed.Data!, StringComparer.OrdinalIgnoreCase)
            {
                ["eventType"] = parsed.Type!,
                ["source"] = source.Code,
            };

            // Through the outbox, in this transaction: the event row and the workflow start stand
            // or fall together, and a crash in between cannot start nothing for a recorded event.
            await publisher.PublishAsync(
                new WorkflowTriggerSignal(source.TenantId, "Lead", id, parsed.Type!, context) { IsExternal = true }, ct);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(new ExternalEventResult(202, leadId is null ? "Unmatched" : "Matched", row.Id, leadId));
    }

    /// <summary>
    /// The lead, among those this SOURCE brought in — never the whole tenant: a partner holding a
    /// phone number must not be able to start workflows on a lead it never sent. The external id
    /// is the ingestion's (a duplicate ingestion names the surviving lead); the phone goes through
    /// the blind index, newest lead first.
    /// </summary>
    private async Task<(Guid? LeadId, string? MatchedBy)> MatchAsync(
        LeadSourceConfig source, string? externalId, string? phone, CancellationToken ct)
    {
        if (externalId is not null)
        {
            var leadId = await db.LeadIngestions.IgnoreQueryFilters()
                .Where(i => i.TenantId == source.TenantId && i.SourceId == source.Id && i.ExternalId == externalId
                            && i.LeadId != Guid.Empty
                            && (i.Status == LeadIngestionStatus.Accepted || i.Status == LeadIngestionStatus.Duplicate))
                .Select(i => (Guid?)i.LeadId)
                .FirstOrDefaultAsync(ct);

            if (leadId is not null && await IsLiveAsync(source.TenantId, leadId.Value, ct))
                return (leadId, "externalId");

            return (null, null);
        }

        var index = phoneIndexer.Compute(phone!);
        var byPhone = await db.Leads.IgnoreQueryFilters()
            .Where(l => l.TenantId == source.TenantId && l.LeadSourceConfigId == source.Id
                        && l.PhoneBlindIndex == index && !l.IsAnonymized)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);

        return byPhone is null ? (null, null) : (byPhone, "phone");
    }

    private Task<bool> IsLiveAsync(Guid tenantId, Guid leadId, CancellationToken ct)
        => db.Leads.IgnoreQueryFilters().AnyAsync(l => l.TenantId == tenantId && l.Id == leadId && !l.IsAnonymized, ct);

    // ── Payload ─────────────────────────────────────────────────────────────────────────────

    internal sealed record ParsedEvent(
        string? Id, string? Type, string? ExternalId, string? Phone, Dictionary<string, object>? Data, string? Error);

    /// <summary>
    /// The envelope, checked strictly: every field the workflow engine will read must be a plain
    /// value. <c>data</c> becomes the trigger's condition context, so a nested object there is
    /// refused rather than silently dropped — a condition on it would never be true.
    /// </summary>
    internal static ParsedEvent Parse(string body)
    {
        static ParsedEvent Fail(string error) => new(null, null, null, null, null, error);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return Fail("INVALID_JSON"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("INVALID_JSON");

            var id = Scalar(root, "id");
            if (string.IsNullOrWhiteSpace(id) || id.Length > LeadExternalEvent.EventIdMaxLength) return Fail("EVENT_ID_REQUIRED");

            var type = Scalar(root, "type")?.Trim().ToLowerInvariant();
            if (type is null || !EventTypePattern().IsMatch(type)) return Fail("EVENT_TYPE_INVALID");

            string? externalId = null, phone = null;
            if (root.TryGetProperty("lead", out var lead) && lead.ValueKind == JsonValueKind.Object)
            {
                externalId = Scalar(lead, "externalId")?.Trim();
                phone = Scalar(lead, "phone")?.Trim();
            }
            if (string.IsNullOrEmpty(externalId) && string.IsNullOrEmpty(phone)) return Fail("LEAD_REFERENCE_REQUIRED");

            var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("data", out var payload) && payload.ValueKind != JsonValueKind.Null)
            {
                if (payload.ValueKind != JsonValueKind.Object) return Fail("DATA_NOT_AN_OBJECT");
                foreach (var p in payload.EnumerateObject())
                {
                    if (data.Count == MaxDataKeys) return Fail("DATA_TOO_MANY_KEYS");
                    object? value = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString()!.Length <= MaxDataStringLength ? p.Value.GetString()! : null,
                        JsonValueKind.Number => p.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,
                    };
                    if (value is null) return Fail("DATA_NOT_FLAT");
                    data[p.Name] = value;
                }
            }

            return new ParsedEvent(id.Trim(), type, string.IsNullOrEmpty(externalId) ? null : externalId,
                string.IsNullOrEmpty(phone) ? null : phone, data, null);
        }
    }

    private static string? Scalar(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>Lower-case, dotted or dashed, like <c>appointment.booked</c>: what a trigger's event name is typed as.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex EventTypePattern();
}
