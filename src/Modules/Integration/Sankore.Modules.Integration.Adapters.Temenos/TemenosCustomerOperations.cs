namespace Sankore.Modules.Integration.Adapters.Temenos;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// <c>ICbsCustomerPort</c> on the Transact Party API (INT-12, criteria 1 and 3).
/// </summary>
internal sealed partial class TemenosAdapter
{
    /// <summary>
    /// Creates a party — after looking for it (INT-12, criterion 3).
    ///
    /// <para>
    /// <b>The search is the mechanism, not a nicety.</b> A creation that times out leaves this
    /// adapter unable to tell "not created" from "created, answer lost", and the dispatcher's
    /// only safe reading of a timeout is "try again". Without a search, that second attempt makes
    /// a second customer file: two references for one person, the second of which every later
    /// command addresses while the first keeps the accounts. Nothing reconciles that — INT-34
    /// cannot tell a duplicate we made from a duplicate the IMF made at a counter.
    /// </para>
    ///
    /// <para>
    /// So the CRM reference is written into the party's <c>mnemonic</c> on creation and is what
    /// the search matches. Three outcomes, and each is a decision:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><b>One match</b> — return it as a SUCCESS carrying that reference. Not a duplicate
    ///   failure: the caller asked for this customer to exist in Transact and it does, which is
    ///   exactly what it asked for. Reporting a duplicate here would park a perfectly completed
    ///   onboarding in a queue a human has to empty.</item>
    /// <item><b>Several matches</b> — a functional duplicate. The far end holds two parties for
    ///   one CRM customer, and creating a third is not the fix; a human must say which one is
    ///   the customer's.</item>
    /// <item><b>The search itself failed</b> — propagate it and DO NOT create. A transient search
    ///   failure means we do not know whether the customer exists, and creating on "do not know"
    ///   is precisely the duplicate this criterion exists to prevent.</item>
    /// </list>
    ///
    /// <para>
    /// The <see cref="IdempotencyKey"/> is also sent as a header, on the chance the installation
    /// honours one. It is a bonus and never the mechanism: installations differ, a header that is
    /// silently ignored leaves no trace, and the search works on every installation.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<ExternalId>> CreateCustomerAsync(
        CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return JournalledAsync<ExternalId>(
            TemenosOperations.CreateCustomer,
            binding => TemenosPaths.Customers(binding.ApiVersion),
            async (binding, ctx, callCt) =>
            {
                var reference = CrmReferenceOf(payload);

                var existing = await FindByCrmReferenceAsync(binding, ctx, reference, callCt);
                if (existing.IsFailure)
                    return IntegrationResultForwarding.Forward<ExternalId>(existing);

                if (existing.Value is { } already)
                {
                    logger.LogInformation(
                        "Temenos already holds a party under our reference; not creating a second "
                        + "one | Connection={ConnectionId} Correlation={Correlation}",
                        binding.ConnectionId, ctx.Correlation);

                    return IntegrationResult.Ok(already);
                }

                var request = await BuildCustomerRequestAsync(binding, payload, reference, callCt);
                if (request.IsFailure)
                    return IntegrationResultForwarding.Forward<ExternalId>(request);

                var url = binding.Url(TemenosPaths.Customers(binding.ApiVersion));

                var created = await transport.SendAsync<TemenosCustomer>(
                    binding, ctx, HttpMethod.Post, url, request.Value, key, callCt);

                if (created.IsFailure)
                    return IntegrationResultForwarding.Forward<ExternalId>(created);

                var customerId = created.Value.First?.CustomerId;

                // A 2xx that does not name the party it created is unusable: the id is what
                // integration_reference stores and what every later command addresses, so an
                // empty one would be a live link to nothing. Refusing is the only safe answer —
                // and the next attempt's pre-create search will find the party if it was made.
                return string.IsNullOrWhiteSpace(customerId)
                    ? IntegrationResultForwarding.Forward<ExternalId>(
                        TemenosErrorClassifier.Unexpected(
                            TemenosOperations.CreateCustomer, "no customerId in the answer"))
                    : IntegrationResult.Ok(new ExternalId(customerId!.Trim()));
            },
            ct);
    }

    /// <summary>
    /// Replaces a party's record.
    ///
    /// <para>
    /// No pre-read, deliberately: a 404 from the <c>PUT</c> is the installation's own answer that
    /// the reference is unknown, and it arrives in one round trip instead of two. What matters is
    /// that it is never answered by creating the party instead — an update that falls back to a
    /// create is how a typo in a reference becomes a second customer file. The classifier maps 404
    /// to <see cref="IntegrationErrors.ExternalEntityNotFound"/> and nothing here overrides it.
    /// </para>
    /// </summary>
    public Task<IntegrationResult> UpdateCustomerAsync(
        ExternalId id, CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return JournalledVoidAsync(
            TemenosOperations.UpdateCustomer,
            binding => TemenosPaths.Customer(binding.ApiVersion, id.Value),
            async (binding, ctx, callCt) =>
            {
                if (!id.HasValue)
                    return MissingExternalId(TemenosOperations.UpdateCustomer);

                var request = await BuildCustomerRequestAsync(
                    binding, payload, CrmReferenceOf(payload), callCt);

                if (request.IsFailure)
                    return IntegrationResultForwarding.Forward(request);

                var url = binding.Url(TemenosPaths.Customer(binding.ApiVersion, id.Value));

                var updated = await transport.SendAsync<TemenosCustomer>(
                    binding, ctx, HttpMethod.Put, url, request.Value, key, callCt);

                return updated.IsFailure
                    ? IntegrationResultForwarding.Forward(updated)
                    : IntegrationResult.Ok();
            },
            ct);
    }

    /// <summary>
    /// Sets the party's KYC grade.
    ///
    /// <para>
    /// Through the KYC sub-resource and not through a partial <c>PUT</c> on the party: see
    /// <c>TemenosPaths.CustomerKyc</c> for why a one-field replace of the whole record is the kind
    /// of damage nobody notices until a controller asks for the file.
    /// </para>
    /// </summary>
    public Task<IntegrationResult> SetKycLevelAsync(
        ExternalId id, KycLevel level, IdempotencyKey key, CancellationToken ct)
        => JournalledVoidAsync(
            TemenosOperations.SetKycLevel,
            binding => TemenosPaths.CustomerKyc(binding.ApiVersion, id.Value),
            async (binding, ctx, callCt) =>
            {
                if (!id.HasValue)
                    return MissingExternalId(TemenosOperations.SetKycLevel);

                var url = binding.Url(TemenosPaths.CustomerKyc(binding.ApiVersion, id.Value));
                var body = new TemenosKycRequest(ToWireKycStatus(level));

                var graded = await transport.SendAsync<TemenosCustomer>(
                    binding, ctx, HttpMethod.Put, url, body, key, callCt);

                return graded.IsFailure
                    ? IntegrationResultForwarding.Forward(graded)
                    : IntegrationResult.Ok();
            },
            ct);

    // ── The pre-create search ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks the party up by OUR reference. Success carrying <c>null</c> means "the installation
    /// answered, and holds no such party".
    ///
    /// <para>
    /// A 404 on the enquiry is read as "no match" rather than as a failure: a collection that
    /// answers 404 for an empty result set is a perfectly ordinary API shape, and treating it as a
    /// refusal would make every first creation fail. An empty <c>body</c> on a 200 means the same
    /// thing.
    /// </para>
    ///
    /// <para>
    /// It shares the surrounding journal row rather than opening its own. The row describes one
    /// logical operation — <c>CreateCustomer</c> — and splitting it would make INT-08's statistics
    /// count two calls for every onboarding and report a creation rate twice the real one.
    /// </para>
    /// </summary>
    private async Task<IntegrationResult<ExternalId?>> FindByCrmReferenceAsync(
        TemenosBinding binding, CallContext ctx, string reference, CancellationToken ct)
    {
        var url = binding.Url(
            TemenosPaths.Customers(binding.ApiVersion),
            $"{TemenosQuery.Mnemonic}={Uri.EscapeDataString(reference)}");

        var found = await transport.SendAsync<TemenosCustomer>(
            binding, ctx, HttpMethod.Get, url, body: null, idempotencyKey: null, ct);

        if (found.IsFailure)
        {
            return found.Code == IntegrationErrors.ExternalEntityNotFound
                ? IntegrationResult.Ok<ExternalId?>(null)
                : IntegrationResultForwarding.Forward<ExternalId?>(found);
        }

        var matches = (found.Value.Body ?? [])
            .Select(c => c.CustomerId)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 0)
            return IntegrationResult.Ok<ExternalId?>(null);

        if (matches.Count > 1)
        {
            logger.LogError(
                "Temenos holds {Count} parties under one CRM reference on connection "
                + "{ConnectionId}; refusing to create a third.",
                matches.Count, binding.ConnectionId);

            return IntegrationResultForwarding.Forward<ExternalId?>(
                IntegrationResult.Functional(
                    IntegrationErrors.Duplicate,
                    $"{TemenosOperations.SearchCustomer}: Temenos holds {matches.Count} parties "
                    + "under this CRM reference; a human must say which one is the customer's."));
        }

        return IntegrationResult.Ok<ExternalId?>(new ExternalId(matches[0]!.Trim()));
    }

    /// <summary>
    /// What the party is searchable by, and what is written into its <c>mnemonic</c>.
    ///
    /// <para>
    /// The CRM's own reference when the payload carries one, and the CRM customer id otherwise.
    /// Never a generated value and never a timestamp: the whole point is that the same customer
    /// computes the same reference on every attempt, including the attempt that follows a timeout
    /// in another process.
    /// </para>
    /// </summary>
    private static string CrmReferenceOf(CbsCustomerPayload payload)
        => string.IsNullOrWhiteSpace(payload.CrmReference)
            ? payload.CrmCustomerId.ToString()
            : payload.CrmReference!.Trim();

    // ── Payload → wire ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the Transact party record, translating every code through
    /// <c>integration_mapping</c>.
    ///
    /// <para>
    /// Seven domains are consulted and any one of them can refuse the whole call. That is the
    /// intent of INT-04: a code the installation has never heard of, passed through, creates a
    /// customer with a profession nobody can read and a row that looks successful for ever. A
    /// null code is not a missing mapping — see <c>TemenosCodeTranslation</c>.
    /// </para>
    /// </summary>
    private async Task<IntegrationResult<TemenosCustomerRequest>> BuildCustomerRequestAsync(
        TemenosBinding binding, CbsCustomerPayload payload, string reference, CancellationToken ct)
    {
        var gender = await codes.OptionalAsync(binding, MappingDomain.Gender, payload.Gender, ct);
        if (gender.IsFailure) return IntegrationResultForwarding.Forward<TemenosCustomerRequest>(gender);

        var marital = await codes.OptionalAsync(
            binding, MappingDomain.MaritalStatus, payload.MaritalStatus, ct);
        if (marital.IsFailure) return IntegrationResultForwarding.Forward<TemenosCustomerRequest>(marital);

        var nationality = await codes.OptionalAsync(
            binding, MappingDomain.Country, payload.Nationality, ct);
        if (nationality.IsFailure)
            return IntegrationResultForwarding.Forward<TemenosCustomerRequest>(nationality);

        var country = await codes.OptionalAsync(binding, MappingDomain.Country, payload.Country, ct);
        if (country.IsFailure) return IntegrationResultForwarding.Forward<TemenosCustomerRequest>(country);

        var docType = await codes.OptionalAsync(
            binding, MappingDomain.IdDocType, payload.IdDocumentType, ct);
        if (docType.IsFailure) return IntegrationResultForwarding.Forward<TemenosCustomerRequest>(docType);

        var profession = await codes.OptionalAsync(
            binding, MappingDomain.Profession, payload.Profession, ct);
        if (profession.IsFailure)
            return IntegrationResultForwarding.Forward<TemenosCustomerRequest>(profession);

        var sector = await codes.OptionalAsync(binding, MappingDomain.Sector, payload.Sector, ct);
        if (sector.IsFailure) return IntegrationResultForwarding.Forward<TemenosCustomerRequest>(sector);

        var agency = await codes.OptionalAsync(binding, MappingDomain.Agency, payload.AgencyCode, ct);
        if (agency.IsFailure) return IntegrationResultForwarding.Forward<TemenosCustomerRequest>(agency);

        return IntegrationResult.Ok(new TemenosCustomerRequest
        {
            Mnemonic = reference,
            ShortName = ShortNameOf(payload),
            Name1 = payload.LegalName ?? ShortNameOf(payload),
            FamilyName = payload.LastName,
            GivenNames = payload.FirstName,
            DateOfBirth = payload.DateOfBirth is { } dob ? TemenosWireFormats.Format(dob) : null,
            Gender = gender.Value,
            MaritalStatus = marital.Value,
            Nationality = nationality.Value,
            Residence = country.Value,
            LegalDocumentType = docType.Value,
            LegalDocumentNumber = payload.IdDocumentNumber,
            Phone = payload.PhoneNumber,
            Email = payload.Email,
            Street = payload.AddressLine,
            Town = payload.City,
            Country = country.Value,

            // The CRM's agency, mapped to the Transact company it corresponds to. The connection's
            // own CompanyId is the DEFAULT and travels as a header on every call; this field is
            // what puts a customer in the branch that onboarded them.
            Company = agency.Value ?? binding.Settings.CompanyId,

            Profession = profession.Value,
            Sector = sector.Value,
            KycStatus = ToWireKycStatus(payload.KycLevel),
        });
    }

    /// <summary>
    /// Transact's <c>shortName</c>, which is an indexed, displayed field.
    ///
    /// <para>
    /// Surname first and upper-case, the same shape as M01's <c>Client.SearchKey</c>: a counter
    /// looking a customer up in Transact and one looking them up in SANKORE should be typing the
    /// same thing. A legal person has only its legal name.
    /// </para>
    /// </summary>
    private static string? ShortNameOf(CbsCustomerPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.LegalName))
            return payload.LegalName!.Trim().ToUpperInvariant();

        var parts = new[] { payload.LastName, payload.FirstName }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim().ToUpperInvariant());

        var joined = string.Join(' ', parts);

        return string.IsNullOrEmpty(joined) ? null : joined;
    }

    /// <summary>
    /// The three KYC tiers onto the installation's own vocabulary. See
    /// <c>TemenosKycStatuses</c> for why these are not routed through <c>integration_mapping</c>.
    /// </summary>
    private static string ToWireKycStatus(KycLevel level) => level switch
    {
        KycLevel.Full => TemenosKycStatuses.Full,
        KycLevel.Simplified => TemenosKycStatuses.Simplified,
        _ => TemenosKycStatuses.None,
    };

    /// <summary>
    /// A caller that passed an empty reference. Technical: the CRM has lost the link, and calling
    /// Transact with an empty path segment would address the collection and update something else
    /// entirely.
    /// </summary>
    private static IntegrationResult MissingExternalId(string operation)
        => IntegrationResult.Technical(
            IntegrationErrors.PayloadInvalid,
            $"{operation} was called with an empty external reference.");
}
