namespace Sankore.Modules.Customers.Features.LeadConversion.CreateClientFromLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// US-M01-BE-06 — opens the client record of a lead module M13 has just converted.
///
/// <para>
/// <b>Design note: no <c>LeadConvertedIntegrationEvent</c> consumer.</b> §10 of the slice spec
/// contemplated a MassTransit consumer and then ruled it out, and the code confirms why:
/// <c>LeadConvertedIntegrationEvent</c> is declared in
/// <c>Sankore.Modules.Leads/Features/ConvertLead/Events/</c> — the Leads MAIN assembly.
/// Referencing it from here would break the module-isolation rule (a module may reference
/// another module's <c>*.PublicApi</c> project and nothing else), and moving the event into
/// <c>Sankore.Modules.Leads.PublicApi</c> is not this slice's call. Conversion therefore
/// happens through exactly one door: the synchronous
/// <see cref="ICustomersModule.CreateFromLeadAsync"/> call that <c>ILeadConversionService</c>
/// forwards to this handler. That is also the better contract for the caller — M13 needs the
/// blocking-duplicate answer <i>before</i> it tells the operator the lead is converted, which a
/// fire-and-forget event could never give it.
/// </para>
///
/// <para>
/// <b>Tenant scoping.</b> Every read below is <c>IgnoreQueryFilters()</c> + an explicit
/// <c>TenantId == cmd.TenantId</c>. The caller may be a Hangfire job or a consumer with no
/// ambient tenant, so the global query filter cannot be trusted here — the explicit predicate
/// is the isolation boundary, and it is what the multi-tenant test pins down.
/// </para>
///
/// <para>
/// Order of operations: idempotence → blocking duplicate → agency → advisor parameter →
/// client number → encryption → aggregate → requested id → contact points → outbox → commit.
/// </para>
/// </summary>
internal sealed class CreateClientFromLeadHandler(
    CustomersDbContext db,
    IDuplicateProbe duplicateProbe,
    IAgencyDirectory agencyDirectory,
    IClientNumberGenerator clientNumberGenerator,
    ICustomerSettings settings,
    IFieldEncryptor encryptor,
    IBlindIndexer indexer,
    IPhoneticKeyCalculator phoneticKeys,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher
) : IRequestHandler<CreateClientFromLeadCommand, Result<CreateFromLeadResult>>
{
    /// <summary>PostgreSQL SQLSTATE for "unique_violation".</summary>
    private const string UniqueViolationSqlState = "23505";

    /// <summary>Unique filtered index on <c>(tenant_id, source_lead_id)</c>.</summary>
    private const string SourceLeadIndex = "ux_clients_source_lead";

    public async Task<Result<CreateFromLeadResult>> Handle(
        CreateClientFromLeadCommand cmd, CancellationToken ct)
    {
        var tenantId = cmd.TenantId;
        var actor = cmd.ConvertedByUserId;

        // 1. Idempotence, first line of defence: this lead may already have been converted
        //    (operator double-click, M13 retry, replayed outbox message). Returning the
        //    existing client is a SUCCESS — the caller asked for a converted lead's client and
        //    that is what it gets.
        var already = await FindByLeadAsync(tenantId, cmd.LeadId, ct);
        if (already is not null)
            return Result.Ok(new CreateFromLeadResult(already.Id, already.ClientNumber, true));

        // 2. Blocking duplicate on the identity document — EQUALITY ON THE BLIND INDEX, never a
        //    decryption. This is not a failure: M13 needs the offending client's id and number
        //    to offer "attach this lead to the existing client instead", which a Result.Fail
        //    (a bare string code) could not carry. Hence BlockingCode on the result.
        string? documentBlindIndex = null;
        if (!string.IsNullOrWhiteSpace(cmd.IdentityDocumentNumber))
        {
            var documentNumber = cmd.IdentityDocumentNumber.Trim();
            documentBlindIndex = indexer.Compute(
                BlindIndexPurpose.IdentityDocument,
                SensitiveValueNormalizer.NormalizeDocumentNumber(documentNumber));

            var hit = await duplicateProbe.FindByIdentityDocumentAsync(
                tenantId, documentBlindIndex, excludeClientId: null, ct);

            if (hit is not null)
            {
                return Result.Ok(new CreateFromLeadResult(
                    hit.ClientId,
                    hit.ClientNumber,
                    AlreadyExisted: false,
                    BlockingCode: CustomerErrors.DuplicateIdentityDocument));
            }
        }

        // 3. Agency code — a segment of the client number. An agency the directory cannot
        //    resolve (missing, soft-deleted, inactive) is out of scope by definition.
        var agencyCode = await agencyDirectory.GetAgencyCodeAsync(tenantId, cmd.AgencyId, ct);
        if (string.IsNullOrWhiteSpace(agencyCode))
            return Result.Fail<CreateFromLeadResult>(CustomerErrors.AgencyOutOfScope);

        // 4. Tenant parameter `advisor-from-converting-agent`: some networks want the agent who
        //    converted the lead to keep the relationship, others assign portfolios centrally
        //    afterwards and want the client to arrive unassigned.
        var advisorFromConvertingAgent = await settings.GetBoolAsync(
            tenantId, CustomerSettingKeys.AdvisorFromConvertingAgent, ct);

        var advisorUserId = advisorFromConvertingAgent ? cmd.ConvertedByUserId : (Guid?)null;

        // 5. Client number (tenant + agency + year sequence).
        var clientNumber = await clientNumberGenerator.NextAsync(tenantId, agencyCode, ct);

        // 6. Protect everything protected BEFORE the aggregate sees it: the domain only ever
        //    holds ciphertext and blind indexes.
        var isIndividual = !string.IsNullOrWhiteSpace(cmd.FirstName)
                           && !string.IsNullOrWhiteSpace(cmd.LastName);

        Client client;

        if (isIndividual)
        {
            var birthDate = cmd.BirthDate;

            var normalizedBirthDate = birthDate.HasValue
                ? SensitiveValueNormalizer.NormalizeDateOfBirth(birthDate.Value)
                : null;

            var encryptedDateOfBirth = normalizedBirthDate is null
                ? null
                : encryptor.Encrypt(normalizedBirthDate);

            var dateOfBirthBlindIndex = normalizedBirthDate is null
                ? null
                : indexer.Compute(BlindIndexPurpose.DateOfBirth, normalizedBirthDate);

            var encryptedDocumentNumber = string.IsNullOrWhiteSpace(cmd.IdentityDocumentNumber)
                ? null
                : encryptor.Encrypt(cmd.IdentityDocumentNumber.Trim());

            client = Client.CreateIndividual(
                tenantId: tenantId,
                clientNumber: clientNumber,
                agencyId: cmd.AgencyId,
                agencyCode: agencyCode,
                advisorUserId: advisorUserId,
                firstName: cmd.FirstName!.Trim(),
                lastName: cmd.LastName!.Trim(),
                maidenName: null,
                // The lead form rarely asks for gender; Other is the neutral placeholder the
                // operator corrects during KYC rather than a claim about the person.
                gender: ParseGender(cmd.Gender),
                encryptedDateOfBirth: encryptedDateOfBirth,
                dateOfBirthBlindIndex: dateOfBirthBlindIndex,
                birthPlace: null,
                nationality: cmd.Nationality,
                maritalStatus: null,
                fatherName: null,
                motherName: null,
                profession: cmd.Profession,
                employer: null,
                encryptedDeclaredIncome: null,
                declaredIncomeCurrency: null,
                // Blank falls back to the module default inside the aggregate.
                preferredLanguage: cmd.PreferredLanguage ?? string.Empty,
                docType: ParseDocumentType(cmd.IdentityDocumentType, encryptedDocumentNumber),
                encryptedDocNumber: encryptedDocumentNumber,
                docNumberBlindIndex: documentBlindIndex,
                docIssuedOn: null,
                docExpiresOn: null,
                createdBy: actor,
                sourceLeadId: cmd.LeadId,
                id: cmd.RequestedClientId);

            // Surname first: it is the discriminating half of a West-African name, so the
            // primary key is the one duplicate detection leans on.
            client.SetPhoneticKeys(
                phoneticKeys.Compute(cmd.LastName),
                phoneticKeys.Compute(cmd.FirstName));
        }
        else if (!string.IsNullOrWhiteSpace(cmd.LegalName))
        {
            // A lead form carries no legal form (SARL, SA, GIE...). Rather than invent one we
            // take the tenant's first active entry and let the operator correct it through
            // UpdateLegalIdentity; a tenant with no legal form configured cannot register a
            // company at all, which is exactly LEGAL_FORM_UNKNOWN.
            var legalFormCode = await FirstActiveLegalFormAsync(tenantId, ct);
            if (legalFormCode is null)
                return Result.Fail<CreateFromLeadResult>(CustomerErrors.LegalFormUnknown);

            client = Client.CreateLegal(
                tenantId: tenantId,
                clientNumber: clientNumber,
                agencyId: cmd.AgencyId,
                agencyCode: agencyCode,
                advisorUserId: advisorUserId,
                legalName: cmd.LegalName.Trim(),
                legalFormCode: legalFormCode,
                encryptedRegistrationNumber: null,
                registrationNumberBlindIndex: null,
                encryptedTaxIdNumber: null,
                incorporationDate: null,
                preferredLanguage: cmd.PreferredLanguage ?? string.Empty,
                createdBy: actor,
                sourceLeadId: cmd.LeadId,
                id: cmd.RequestedClientId);

            // One name, so only the primary phonetic key is meaningful.
            client.SetPhoneticKeys(phoneticKeys.Compute(cmd.LegalName), null);
        }
        else
        {
            // The validator already rejects this; kept so a direct in-process dispatch that
            // bypassed the pipeline cannot reach Client.CreateIndividual with a blank name.
            return Result.Fail<CreateFromLeadResult>(LeadConversionErrorCodes.LeadIdentityIncomplete);
        }

        // 7. Contact points from what the lead already collected. The phone is the primary
        //    channel: in this market it is how an agency reaches a client at all.
        var now = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(cmd.PhoneNumber))
        {
            var phone = cmd.PhoneNumber.Trim();
            client.AddContactPoint(
                ContactPointType.Phone,
                encryptor.Encrypt(phone)!,
                indexer.Compute(BlindIndexPurpose.Phone, SensitiveValueNormalizer.NormalizePhone(phone)),
                label: null,
                isPrimary: true,
                validFrom: now,
                actor: actor);
        }

        if (!string.IsNullOrWhiteSpace(cmd.Email))
        {
            var email = cmd.Email.Trim();
            client.AddContactPoint(
                ContactPointType.Email,
                encryptor.Encrypt(email)!,
                indexer.Compute(BlindIndexPurpose.Email, SensitiveValueNormalizer.NormalizeEmail(email)),
                label: null,
                isPrimary: true,
                validFrom: now,
                actor: actor);
        }

        db.Clients.Add(client);

        // 9. Outbox BEFORE SaveChanges: the publisher only enqueues the row, so the event and
        //    the client commit together. SourceLeadId on the event is load-bearing — module M02
        //    opens the KYC file from it and the timeline slice (§8.1) imports the lead's history
        //    with it. Dropping it would silently break both.
        await publisher.PublishAsync(
            new ClientCreatedEvent(
                TenantId: tenantId,
                ClientId: client.Id,
                ClientNumber: client.ClientNumber,
                ClientType: client.Type.ToString(),
                AgencyId: client.AgencyId,
                AdvisorUserId: client.AdvisorUserId,
                SourceLeadId: cmd.LeadId,
                CreatedBy: actor),
            ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsSourceLeadViolation(ex))
        {
            // Idempotence, second line of defence. Two concurrent CreateFromLeadAsync calls for
            // the same lead both pass step 1 and both reach here; the unique index lets exactly
            // one through. The loser re-reads and returns the winner's client.
            return await ResolveConcurrentWinnerAsync(tenantId, cmd.LeadId, ct);
        }

        return Result.Ok(new CreateFromLeadResult(client.Id, client.ClientNumber, false));
    }

    /// <summary>
    /// The client already opened for this lead, or null. Projected to the two fields the result
    /// needs so nothing encrypted is even loaded.
    /// </summary>
    private Task<ExistingClient?> FindByLeadAsync(Guid tenantId, Guid leadId, CancellationToken ct)
        => db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.SourceLeadId == leadId)
            .Select(c => new ExistingClient(c.Id, c.ClientNumber))
            .FirstOrDefaultAsync(ct);

    private async Task<string?> FirstActiveLegalFormAsync(Guid tenantId, CancellationToken ct)
        => await db.LegalForms
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.IsActive)
            .OrderBy(f => f.DisplayOrder)
            .Select(f => f.Code)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Recovers the client written by the concurrent call that won the unique index.
    ///
    /// <para>
    /// The change tracker is cleared first: the rejected <see cref="Client"/> is still marked
    /// Added, and leaving it there would make the re-read return our own doomed entity from the
    /// identity map. The re-read itself is guarded because <c>TransactionBehavior</c> wraps this
    /// handler in an ambient <see cref="System.Transactions.TransactionScope"/> — on PostgreSQL
    /// the failed statement aborts that transaction, so the follow-up query may itself throw
    /// (SQLSTATE 25P02). When it does we answer CONCURRENCY_CONFLICT: the caller retries and
    /// step 1 then returns the existing client on a clean transaction, which is the same
    /// outcome one round-trip later.
    /// </para>
    /// </summary>
    private async Task<Result<CreateFromLeadResult>> ResolveConcurrentWinnerAsync(
        Guid tenantId, Guid leadId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();

        try
        {
            var winner = await FindByLeadAsync(tenantId, leadId, ct);

            return winner is not null
                ? Result.Ok(new CreateFromLeadResult(winner.Id, winner.ClientNumber, true))
                : Result.Fail<CreateFromLeadResult>(CustomerErrors.ConcurrencyConflict);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Result.Fail<CreateFromLeadResult>(CustomerErrors.ConcurrencyConflict);
        }
    }

    /// <summary>
    /// True when the failure is the unique violation of <c>ux_clients_source_lead</c>.
    /// Falls back to a message match so the path stays reachable under providers that do not
    /// surface a <see cref="PostgresException"/> (EF InMemory in tests, a future provider).
    /// </summary>
    private static bool IsSourceLeadViolation(DbUpdateException ex)
    {
        if (ex.InnerException is PostgresException { SqlState: UniqueViolationSqlState } pg)
        {
            return pg.ConstraintName is null
                   || pg.ConstraintName.Contains(SourceLeadIndex, StringComparison.OrdinalIgnoreCase);
        }

        return Mentions(ex.Message) || Mentions(ex.InnerException?.Message);
    }

    private static bool Mentions(string? message)
        => message is not null && message.Contains(SourceLeadIndex, StringComparison.OrdinalIgnoreCase);

    /// <summary>Unknown or absent gender is <c>Other</c>, never a guess.</summary>
    private static Gender ParseGender(string? value)
        => Enum.TryParse<Gender>(value, ignoreCase: true, out var gender) ? gender : Gender.Other;

    /// <summary>
    /// A document number with an unrecognised type is still worth keeping — it is the strongest
    /// duplicate signal there is — so it is filed under <c>Other</c>. No number, no type.
    /// </summary>
    private static IdentityDocumentType? ParseDocumentType(string? value, string? encryptedNumber)
    {
        if (encryptedNumber is null) return null;

        return Enum.TryParse<IdentityDocumentType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : IdentityDocumentType.Other;
    }

    private sealed record ExistingClient(Guid Id, string ClientNumber);
}
