namespace Sankore.Modules.Integration.Adapters.Temenos;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// CRM codes to Transact codes, through <c>integration_mapping</c> and nothing else (INT-04,
/// INT-13 criterion 2).
///
/// <para>
/// <b>A missing mapping refuses the call.</b> It is a <see cref="ErrorFamily.Technical"/> failure
/// whose detail names the domain and the code — <c>MappingResolver</c> owns that sentence so
/// every caller's message reads the same. The alternative, passing the CRM code through, is how a
/// customer is created in the core banking system with a profession nobody there can read, and
/// the row looks like a success for ever.
/// </para>
///
/// <para>
/// <b>Null is not a missing mapping.</b> Every code field of <c>CbsCustomerPayload</c> is
/// nullable, and a null means the CRM holds nothing — a customer with no declared profession. The
/// field is then simply omitted from the request. Only a code that IS present and has no
/// translation refuses the call, which is the difference between "we do not know" and "we know
/// something we cannot say in your language".
/// </para>
///
/// <para>
/// A thin wrapper and not a second resolver: the lookups, the tenant predicate and the failure
/// sentence all belong to <c>MappingResolver</c>. What this adds is the optionality rule above
/// and a name the operations below can read at a glance.
/// </para>
/// </summary>
internal sealed class TemenosCodeTranslation(MappingResolver mappings)
{
    /// <summary>
    /// Translates a code that MUST be present — the product code of an account opening.
    ///
    /// <para>
    /// A blank code is refused here rather than sent, and with the same error as an unmapped one:
    /// INT-13's criterion is that an absent code produces a technical error, and "absent" covers
    /// the caller that passed an empty string as much as the tenant that configured no mapping.
    /// </para>
    /// </summary>
    public async Task<IntegrationResult<string>> RequiredAsync(
        TemenosBinding binding, MappingDomain domain, string? crmCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(crmCode))
        {
            return IntegrationResult.Technical<string>(
                IntegrationErrors.MappingMissing,
                MappingResolver.MissingDetail(domain, crmCode ?? string.Empty));
        }

        return await mappings.ResolveAsync(binding.TenantId, binding.ConnectionId, domain, crmCode, ct);
    }

    /// <summary>
    /// Translates a code that may legitimately be absent. Returns a success carrying <c>null</c>
    /// when there was nothing to translate, and the same technical failure as
    /// <see cref="RequiredAsync"/> when there was something and it does not translate.
    /// </summary>
    public async Task<IntegrationResult<string?>> OptionalAsync(
        TemenosBinding binding, MappingDomain domain, string? crmCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(crmCode))
            return IntegrationResult.Ok<string?>(null);

        var resolved = await mappings.ResolveAsync(
            binding.TenantId, binding.ConnectionId, domain, crmCode, ct);

        return resolved.IsFailure
            ? IntegrationResultForwarding.Forward<string?>(resolved)
            : IntegrationResult.Ok<string?>(resolved.Value);
    }

    /// <summary>
    /// The reverse translation, for a product code coming back on an account.
    ///
    /// <para>
    /// Null rather than a failure, as <c>MappingResolver.ReverseAsync</c> documents: an account
    /// the IMF opened before SANKORE existed carries a product the mapping table has never heard
    /// of, and abandoning the whole account list over it would make Customer 360 empty for exactly
    /// the customers with the longest history. The untranslated external code is kept instead, so
    /// the counter sees what the bank calls it.
    /// </para>
    /// </summary>
    public async Task<string?> ReverseProductAsync(
        TemenosBinding binding, string? externalCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalCode))
            return null;

        var crmCode = await mappings.ReverseAsync(
            binding.TenantId, binding.ConnectionId, MappingDomain.Product, externalCode, ct);

        return crmCode ?? externalCode;
    }
}
