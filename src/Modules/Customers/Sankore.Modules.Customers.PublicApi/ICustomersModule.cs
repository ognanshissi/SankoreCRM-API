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
