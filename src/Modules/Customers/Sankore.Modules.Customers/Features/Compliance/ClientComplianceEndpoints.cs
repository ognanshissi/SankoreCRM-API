namespace Sankore.Modules.Customers.Features.Compliance;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Insurance lot 4, on the client: the Mobile Money numbers and bank accounts a premium is collected
/// from and a refund paid to, and the confidential LBC/FT suspicion alert.
///
/// <para>
/// <b>The alert's endpoints are thin, not MediatR commands, on purpose</b>: <c>AuditBehavior</c>
/// writes a command's payload into the audit trail, which administrators read — and the facts of a
/// suspicion must reach the compliance officer and nobody else. The filing itself is still traced on
/// the alert (who, when).
/// </para>
/// </summary>
public static class ClientComplianceEndpoints
{
    internal const string ClientNotFound = "CLIENT_NOT_FOUND";

    public static IEndpointRouteBuilder MapClientComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        var instruments = app.MapGroup("clients/{clientId:guid}/payment-instruments").WithTags("Clients");

        instruments.MapGet(string.Empty, ListInstruments)
            .WithName("ListClientPaymentInstruments")
            .WithSummary("The client's Mobile Money numbers and bank accounts (masked)")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<IReadOnlyList<PaymentInstrumentDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        instruments.MapPost(string.Empty, RegisterInstrument)
            .WithName("RegisterClientPaymentInstrument")
            .WithSummary("Record a Mobile Money number or a bank account (RIB) for the client")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPost("payment-instruments/{instrumentId:guid}/deactivation", DeactivateInstrument)
            .WithTags("Clients")
            .WithName("DeactivateClientPaymentInstrument")
            .WithSummary("Stop using a payment instrument (kept for the history)")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPost("clients/{clientId:guid}/suspicion-alerts", RaiseAlert)
            .WithTags("Compliance")
            .WithName("RaiseSuspicionAlert")
            .WithSummary("File a confidential LBC/FT suspicion alert")
            .WithDescription(
                "Answers 202 and nothing else: from now on only the compliance officer reads it. "
                + "Nothing appears on the client's record and nothing is said to the client. "
                + "Requires permission: compliance:suspicion:raise.")
            .RequireAuthorization(Permissions.CanRaiseSuspicionAlert.Code)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        var alerts = app.MapGroup("suspicion-alerts").WithTags("Compliance");

        alerts.MapGet(string.Empty, ListAlerts)
            .WithName("ListSuspicionAlerts")
            .WithSummary("The suspicion alerts — the compliance officer only")
            .RequireAuthorization(Permissions.CanManageSuspicionAlerts.Code)
            .Produces<IReadOnlyList<SuspicionAlertDto>>(StatusCodes.Status200OK)
            .WithOpenApi()
            .WithTenantHeader();

        alerts.MapPost("{alertId:guid}/handling", HandleAlert)
            .WithName("HandleSuspicionAlert")
            .WithSummary("Examine, declare to the CENTIF (date, reference) or close with a reason")
            .RequireAuthorization(Permissions.CanManageSuspicionAlerts.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    // ── Payment instruments ─────────────────────────────────────────────────

    private static async Task<IResult> ListInstruments(
        Guid clientId, CustomersDbContext db, ICustomersModule customers, ICurrentUser user, CancellationToken ct)
    {
        if (!await customers.CanActorAccessClientAsync(user.TenantId, clientId, user.Id, ct))
            return Results.NotFound(new { error = ClientNotFound });

        var rows = await db.PaymentInstruments.Where(p => p.ClientId == clientId)
            .OrderBy(p => p.DeactivatedAt != null).ThenByDescending(p => p.CreatedAt).ToListAsync(ct);

        return Results.Ok(rows.Select(p => new PaymentInstrumentDto(
            p.Id, p.Kind.ToString(), p.Provider, p.MaskedIdentifier, p.HolderName, p.IsActive, p.CreatedAt)).ToList());
    }

    private static async Task<IResult> RegisterInstrument(
        Guid clientId, RegisterPaymentInstrumentRequest body, CustomersDbContext db, ICustomersModule customers,
        IFieldEncryptor encryptor, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        if (!await customers.CanActorAccessClientAsync(user.TenantId, clientId, user.Id, ct))
            return Results.NotFound(new { error = ClientNotFound });

        ClientPaymentInstrument instrument;
        try
        {
            var clear = (body.Identifier ?? string.Empty).Trim();
            instrument = ClientPaymentInstrument.Register(user.TenantId, clientId, body.Kind, body.Provider, clear,
                encryptor.Encrypt(clear)!, body.HolderName, user.Id, clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        db.PaymentInstruments.Add(instrument);
        await db.SaveChangesAsync(ct);
        return Results.Created($"clients/{clientId}/payment-instruments", instrument.Id);
    }

    private static async Task<IResult> DeactivateInstrument(
        Guid instrumentId, CustomersDbContext db, ICustomersModule customers, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var instrument = await db.PaymentInstruments.AsTracking().FirstOrDefaultAsync(p => p.Id == instrumentId, ct);
        if (instrument is null || !await customers.CanActorAccessClientAsync(user.TenantId, instrument.ClientId, user.Id, ct))
            return Results.NotFound(new { error = "PAYMENT_INSTRUMENT_NOT_FOUND" });

        instrument.Deactivate(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── Suspicion alerts ────────────────────────────────────────────────────

    private static async Task<IResult> RaiseAlert(
        Guid clientId, RaiseSuspicionAlertRequest body, CustomersDbContext db, ICustomersModule customers,
        IFieldEncryptor encryptor, ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        if (!await customers.CanActorAccessClientAsync(user.TenantId, clientId, user.Id, ct))
            return Results.NotFound(new { error = ClientNotFound });

        if (string.IsNullOrWhiteSpace(body.Facts) || body.Facts.Length > 8000)
            return Results.BadRequest(new { error = "SUSPICION_ALERT_FACTS_REQUIRED" });

        db.SuspicionAlerts.Add(SuspicionAlert.Raise(
            user.TenantId, clientId, encryptor.Encrypt(body.Facts.Trim())!, user.Id, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);

        return Results.Accepted();
    }

    private static async Task<IResult> ListAlerts(
        CustomersDbContext db, ICustomersModule customers, IAdministrationModule administration,
        IFieldEncryptor encryptor, ICurrentUser user, CancellationToken ct, SuspicionAlertStatus? status = null)
    {
        var query = db.SuspicionAlerts.AsQueryable();
        if (status is { } s) query = query.Where(a => a.Status == s);

        var rows = await query.OrderByDescending(a => a.RaisedAt).Take(500).ToListAsync(ct);
        var clients = await customers.GetClientSummariesAsync(user.TenantId, rows.Select(r => r.ClientId).Distinct().ToList(), ct);

        // The names come with the list: the officer must know whom to question, and the user
        // directory the screen could read is not theirs to have (no user:read for that role).
        var names = await administration.GetUserNamesAsync(
            user.TenantId,
            rows.Select(r => r.RaisedBy).Concat(rows.Where(r => r.HandledBy != null).Select(r => r.HandledBy!.Value)).Distinct().ToList(),
            ct);

        return Results.Ok(rows.Select(a => new SuspicionAlertDto(
            a.Id, a.ClientId,
            clients.TryGetValue(a.ClientId, out var c) ? c.DisplayName : "—",
            clients.TryGetValue(a.ClientId, out var n) ? n.ClientNumber : null,
            encryptor.Decrypt(a.EncryptedFacts) ?? string.Empty,
            a.RaisedBy, a.RaisedAt, a.Status.ToString(), a.HandledBy, a.HandledAt, a.DeclaredOn,
            a.DeclarationReference, a.EncryptedNotes is null ? null : encryptor.Decrypt(a.EncryptedNotes),
            names.GetValueOrDefault(a.RaisedBy),
            a.HandledBy is { } by ? names.GetValueOrDefault(by) : null)).ToList());
    }

    private static async Task<IResult> HandleAlert(
        Guid alertId, HandleSuspicionAlertRequest body, CustomersDbContext db, IFieldEncryptor encryptor,
        ICurrentUser user, TimeProvider clock, CancellationToken ct)
    {
        var alert = await db.SuspicionAlerts.AsTracking().FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null) return Results.NotFound(new { error = "SUSPICION_ALERT_NOT_FOUND" });

        var handled = alert.Handle(body.Status, body.DeclaredOn, body.DeclarationReference,
            string.IsNullOrWhiteSpace(body.Notes) ? null : encryptor.Encrypt(body.Notes.Trim()),
            user.Id, clock.GetUtcNow());
        if (handled.IsFailure) return Results.BadRequest(new { error = handled.Error });

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

public sealed record RegisterPaymentInstrumentRequest(
    PaymentInstrumentKind Kind, string Provider, string Identifier, string? HolderName = null);

public sealed record PaymentInstrumentDto(
    Guid Id, string Kind, string Provider, string MaskedIdentifier, string? HolderName, bool IsActive, DateTimeOffset CreatedAt);

public sealed record RaiseSuspicionAlertRequest(string Facts);

public sealed record HandleSuspicionAlertRequest(
    SuspicionAlertStatus Status, DateOnly? DeclaredOn = null, string? DeclarationReference = null, string? Notes = null);

public sealed record SuspicionAlertDto(
    Guid Id, Guid ClientId, string ClientName, string? ClientNumber, string Facts, Guid RaisedBy,
    DateTimeOffset RaisedAt, string Status, Guid? HandledBy, DateTimeOffset? HandledAt, DateOnly? DeclaredOn,
    string? DeclarationReference, string? Notes, string? RaisedByName = null, string? HandledByName = null);
