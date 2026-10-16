namespace Sankore.Modules.Integration.Features.Telephony.ClickToCall;

using System.Transactions;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Kernel;

// M01 and M13 each declare a PhoneSlot of their own, which is deliberate — their slots mean
// different things. Both namespaces are needed here, so neither name is imported unqualified:
// this file speaks TelephonyPhoneSlot and translates at the call.
using CustomerPhoneSlot = Sankore.Modules.Customers.PublicApi.PhoneSlot;
using LeadPhoneSlot = Sankore.Modules.Leads.PublicApi.PhoneSlot;

/// <summary>
/// Turns "the Primary number of this lead" into a number a provider can dial (TEL-B-06).
///
/// <para>
/// <b>The whole reason this exists on the server.</b> The agent's browser names a RECORD and a
/// SLOT and never a number, because a request body carrying a number is a request body somebody
/// can be made to post a stranger's number to — and the resulting call would be placed by the
/// institution, from the institution's line, against a record of the institution's choosing. So
/// the number is read here, by asking the module that owns it, and it goes straight into the
/// provider call.
/// </para>
///
/// <para>
/// <b>It asks; it does not decrypt.</b> M01 holds the client's number under M01's key and M13
/// holds the lead's; this module could not open either. Each contract also applies its own
/// perimeter — the agency scope for a client, the ownership rule for a lead — which is the half
/// that matters: an agent of one branch must not be able to ring another branch's customer, and
/// only the owning module can answer that.
/// </para>
/// </summary>
internal sealed class DialableNumberResolver(
    ICustomersModule customers,
    ILeadsModule leads,
    ILogger<DialableNumberResolver> logger)
{
    /// <summary>
    /// The number, or the owning module's refusal.
    ///
    /// <para>
    /// <b>Wrapped in <c>TransactionScopeOption.Suppress</c>.</b> <c>TransactionBehavior</c> holds
    /// a <c>System.Transactions</c> scope and the other module reads through its own DbContext; a
    /// second connection enlisting promotes the ambient transaction to a distributed one, which
    /// Npgsql does not support and which fails at run time. Repo-wide rule, same as
    /// <c>CaptureLeadHandler.TryMatchCustomerAsync</c>.
    /// </para>
    ///
    /// <para>
    /// <b>A failure is NOT swallowed here, unlike the best-effort lookups at capture.</b> The two
    /// postures are deliberate and should not be unified: losing a prospect because M01 is down is
    /// worse than not recognising a customer, whereas placing a call to a number this module
    /// guessed, or reporting success for a call nobody made, is worse than telling the agent to
    /// try again. The agent is sitting in front of the screen and can.
    /// </para>
    /// </summary>
    internal async Task<Result<string>> ResolveAsync(
        Guid tenantId, string entityType, Guid entityId, TelephonyPhoneSlot slot, Guid actorUserId,
        CancellationToken ct)
    {
        if (!TelEntityTypes.IsKnown(entityType))
            return Result.Fail<string>(TelephonyErrors.EntityTypeUnknown);

        if (entityId == Guid.Empty)
            return Result.Fail<string>(TelephonyErrors.EntityReferenceIncomplete);

        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            // The two slot enums are deliberately per module and deliberately not mapped through
            // a shared one — M01's slots are contact points, M13's are the prospect's number and
            // their company's. The translation is one line and it is here, in the open.
            return entityType == TelEntityTypes.Customer
                ? await customers.ResolvePhoneForCallAsync(
                    tenantId, entityId, ToCustomerSlot(slot), actorUserId, ct)
                : await leads.ResolvePhoneForCallAsync(
                    tenantId, entityId, ToLeadSlot(slot), actorUserId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The entity id is logged and the number is not — there is none to log on this path,
            // and saying so keeps the next reader from adding one.
            logger.LogWarning(
                ex, "Could not resolve the {Slot} number of {EntityType} {EntityId} in tenant "
                  + "{TenantId} for a click-to-call.", slot, entityType, entityId, tenantId);

            return Result.Fail<string>(TelephonyErrors.NumberUnavailable);
        }
    }

    private static LeadPhoneSlot ToLeadSlot(TelephonyPhoneSlot slot) => slot switch
    {
        TelephonyPhoneSlot.Secondary => LeadPhoneSlot.Secondary,
        _ => LeadPhoneSlot.Primary,
    };

    private static CustomerPhoneSlot ToCustomerSlot(TelephonyPhoneSlot slot) => slot switch
    {
        TelephonyPhoneSlot.Secondary => CustomerPhoneSlot.Secondary,
        _ => CustomerPhoneSlot.Primary,
    };
}
