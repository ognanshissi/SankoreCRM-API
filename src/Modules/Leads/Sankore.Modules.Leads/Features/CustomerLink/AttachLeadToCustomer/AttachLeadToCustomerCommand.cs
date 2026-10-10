namespace Sankore.Modules.Leads.Features.CustomerLink.AttachLeadToCustomer;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Attaches a lead to an existing M01 client, by hand (BNF-B-22). The confirmation step for a
/// suggestion the capture-time matcher raised but would not act on alone — a partial match, or
/// several candidates.
///
/// <para>
/// <c>ICommand</c> brings the transaction and the audit row; <c>IResourceCommand</c> names the lead
/// in it. Both matter here more than for most commands: an attachment decides which client record
/// the entire request then reads its identity, household, PEP and FATCA from (BNF-B-23), so the
/// trail has to say who decided and why.
/// </para>
/// </summary>
/// <param name="Reason">
/// Required. An automatic attachment can justify itself by its rule — "unique exact match on the
/// identity document" — but a person overriding or confirming one cannot, and six months later the
/// reason is the only thing that explains the link.
/// </param>
internal sealed record AttachLeadToCustomerCommand(
    Guid LeadId,
    Guid CustomerId,
    string Reason,
    /// <summary>UpdatedAt the caller last read; null skips the staleness check.</summary>
    DateTimeOffset? ExpectedUpdatedAt = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
