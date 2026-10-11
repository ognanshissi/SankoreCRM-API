namespace Sankore.Modules.Integration.Features.Insurance.ProductRouting;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Which insurer each of the tenant's insurance product lines would be subscribed with — the
/// preflight of BNF-B-16.
///
/// <para>
/// It exists because a missing mapping is INVISIBLE today. <c>ResolveInsurerForProductAsync</c>
/// throws <c>MappingMissing</c>, <c>HoldingSubscriptionConsumer</c> catches it and writes one
/// warning line, and the holding stays in <c>PendingSync</c> for ever with nothing to move it.
/// The administrator who could fix it is never told, and the symptom reaches the agent as a
/// subscription that simply never completes.
/// </para>
///
/// <para>
/// <c>GET integration/mappings/Product/unmapped?connectionId=</c> already answers the neighbouring
/// question — which CRM codes this ONE connection has no mapping for. What it cannot say is
/// whether a line is routable AT ALL, which is the question that decides whether a subscription
/// will be refused: with a single active insurance connection an unmapped line still routes, by
/// fallback, and with several it does not.
/// </para>
/// </summary>
internal sealed record ProductRoutingQuery : IRequest<Result<ProductRoutingDto>>;

/// <summary>How a product line resolves to an insurer, named rather than left as a boolean.</summary>
public enum ProductRoutability
{
    /// <summary>An <c>integration_mapping</c> row names the connection. The target state.</summary>
    Mapped,

    /// <summary>
    /// No mapping, but the tenant has exactly ONE active insurance connection, so resolution
    /// falls back to it. Subscriptions work — and will start failing the day a second insurer is
    /// connected, which is precisely why this is reported as its own state rather than as
    /// "routable".
    /// </summary>
    SingleConnectionFallback,

    /// <summary>
    /// No mapping and several active insurance connections: the insurer cannot be determined and
    /// every subscription of this line is refused with <c>MappingMissing</c>.
    /// </summary>
    Unroutable,

    /// <summary>
    /// The tenant has no active insurance connection at all. Nothing about this line is
    /// misconfigured — the installation is.
    /// </summary>
    NoInsuranceConnection,
}

/// <param name="Code">The catalogue code an administrator recognises.</param>
/// <param name="CrmCode">
/// What a mapping row must carry in <c>crm_code</c> for this line: the product's <b>id</b>, not
/// its code. Exposed because it is the value a CSV import has to contain, and getting it wrong
/// produces a mapping that resolves nothing while looking configured.
/// </param>
/// <param name="ConnectionId">The insurer that would receive it, or null when none would.</param>
public sealed record ProductRoutingLineDto(
    Guid ProductLineId,
    string Code,
    string Name,
    string Category,
    string CrmCode,
    ProductRoutability Routability,
    Guid? ConnectionId,
    string? ConnectionName);

/// <param name="ActiveInsuranceConnections">
/// How many active insurance connections the tenant has. It is what makes the difference between
/// a fallback and a refusal, so the figure travels with the lines rather than being inferred.
/// </param>
/// <param name="UnroutableCount">
/// Lines whose subscriptions are refused today. Non-zero is an operational fault, not a warning.
/// </param>
public sealed record ProductRoutingDto(
    int ActiveInsuranceConnections,
    int UnroutableCount,
    IReadOnlyList<ProductRoutingLineDto> Lines);
