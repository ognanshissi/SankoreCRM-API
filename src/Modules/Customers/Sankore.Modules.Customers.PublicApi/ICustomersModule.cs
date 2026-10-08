namespace Sankore.Modules.Customers.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// Public contract exposed by module M01 (Customers).
/// Other modules reference ONLY this project — never the main assembly.
///
/// Every method takes an explicit <paramref name="tenantId"/> because callers may
/// run outside an HTTP request (Hangfire jobs, MassTransit consumers) where the
/// ambient <see cref="ITenantContext"/> is not the tenant being operated on. The
/// implementation therefore bypasses the global query filters and re-applies the
/// tenant predicate manually.
///
/// No method on this contract ever returns decrypted sensitive data: a caller that
/// needs a client's phone number, e-mail, document number, date of birth or income
/// must go through the audited reveal endpoint of module M01.
/// </summary>
public interface ICustomersModule
{
    /// <summary>
    /// Lightweight, non-sensitive projection of a client, or <c>null</c> when the
    /// client does not exist in that tenant.
    /// </summary>
    Task<ClientSummary?> GetClientSummaryAsync(Guid tenantId, Guid clientId, CancellationToken ct);

    /// <summary>
    /// The same projection as <see cref="GetClientSummaryAsync"/> for a batch of ids, keyed by the
    /// id that was ASKED for — so a caller holding a page of opaque references can line the answers
    /// up positionally without a second lookup.
    ///
    /// <para>
    /// Ids that do not exist in that tenant are simply absent from the dictionary: a list screen
    /// resolving names must degrade to "unknown" on a dangling reference, not fail the page.
    /// Merge chains are NOT followed, exactly as in the single read — the answer describes the
    /// record pointed at, and a caller that wants the survivor resolves through
    /// <see cref="ResolveClientIdAsync"/> first.
    /// </para>
    ///
    /// <para>
    /// It exists so a cross-module list costs ONE query per page instead of one per row. Callers
    /// must therefore keep the batch bounded to a page's worth of ids: the implementation turns
    /// them into a single SQL <c>IN</c>, which stops being a favour somewhere in the thousands.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ClientSummary>> GetClientSummariesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> clientIds, CancellationToken ct);

    /// <summary>
    /// Follows the merge chain and returns the id of the surviving client record
    /// (the same id when the client was never merged), or <c>null</c> when the
    /// client does not exist. Callers holding a possibly stale client id should
    /// always resolve it through this method before writing a reference.
    /// </summary>
    Task<Guid?> ResolveClientIdAsync(Guid tenantId, Guid clientId, CancellationToken ct);

    /// <summary>
    /// True only when the client exists AND its status is Active — i.e. KYC has been
    /// approved and the record is neither pending, suspended, rejected, archived nor merged.
    /// </summary>
    Task<bool> ExistsAndActiveAsync(Guid tenantId, Guid clientId, CancellationToken ct);

    /// <summary>
    /// Creates (or returns the already-created) client for a converted lead.
    /// Idempotent on <see cref="CreateFromLeadRequest.LeadId"/>: calling it twice for
    /// the same lead returns the same client with <c>AlreadyExisted = true</c>.
    /// </summary>
    Task<Result<CreateFromLeadResult>> CreateFromLeadAsync(CreateFromLeadRequest request, CancellationToken ct);
    
    /// <summary>
    /// Checks whether a customer with the given ID exists for the tenant.
    /// Used by Leads module for convert-to-existing-customer validation (US-M13-171).
    /// </summary>
    Task<bool> ExistsAsync(Guid tenantId, Guid customerId, CancellationToken ct);

    /// <summary>
    /// Returns a lightweight summary of a customer, or null if not found.
    /// </summary>
    Task<CustomerSummary?> GetCustomerAsync(Guid tenantId, Guid customerId, CancellationToken ct);
}

/// <summary>
/// Non-sensitive client projection. Enum-typed domain concepts are exposed as strings
/// so no consumer has to reference the Customers domain assembly.
/// </summary>
public sealed record ClientSummary(
    Guid Id,
    string ClientNumber,
    string ClientType,
    string DisplayName,
    string Status,
    Guid AgencyId,
    Guid? AdvisorUserId,
    string KycStatus,
    string RiskLevel,
    Guid? MergedIntoId);

/// <summary>
/// Lead-to-client conversion payload (US-M01-BE-06). Sensitive values arrive in clear
/// text from the Leads module and are encrypted by module M01 before persistence;
/// they are never echoed back.
/// </summary>
public sealed record CreateFromLeadRequest(
    Guid TenantId,
    Guid LeadId,
    Guid AgencyId,
    Guid ConvertedByUserId,
    string? FirstName,
    string? LastName,
    string? LegalName,
    string? Gender,
    DateOnly? DateOfBirth,
    string? Nationality,
    string? PhoneNumber,
    string? Email,
    string? IdentityDocumentType,
    string? IdentityDocumentNumber,
    string? Profession,
    string? PreferredLanguage,
    Guid? RequestedClientId);

/// <summary>
/// <c>AlreadyExisted</c> is true when the lead had already been converted — the
/// unique filtered index on (tenant, source_lead_id) is what makes this safe.
///
/// <c>BlockingCode</c> is set when conversion could not create a NEW client because an
/// existing record blocks it (a duplicate identity document, for instance): the code is
/// one of the M01 error codes and <c>ClientId</c> then points at the blocking client, so
/// the Leads module can show the operator which record to reconcile instead of just
/// reporting a failure. It stays null on a normal creation.
/// </summary>
public sealed record CreateFromLeadResult(
    Guid ClientId,
    string ClientNumber,
    bool AlreadyExisted,
    string? BlockingCode = null);


public sealed record CustomerSummary(
    Guid Id,
    string DisplayName,
    string? Email,
    string? PhoneNumber,
    Guid? AgencyId);