namespace Sankore.Modules.Integration.Features.Insurance.CreateInsuranceProduct;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Adds one product to the tenant's catalogue (ASS-03).
///
/// <para>
/// An <c>ICommand</c>, so <c>TransactionBehavior</c> and <c>AuditBehavior</c> apply — a catalogue
/// entry carries a commission rate and a premium that will be debited from customers' accounts,
/// so who created it and when is worth an audit row.
/// </para>
///
/// <para>
/// The connection and the insurer's product code are here and NOT on the update command: they are
/// the product's identity. Changing either would re-point every subscription, policy and
/// statement line that names this product at a different contract at a different insurer, which
/// is a new product and not an edit.
/// </para>
/// </summary>
internal sealed record CreateInsuranceProductCommand(
    Guid ConnectionId,
    string InsurerProductCode,
    InsuranceProductWriteRequest Body)
    : IRequest<Result<InsuranceProductDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "InsuranceProduct";

    // The connection: the product's own id does not exist yet, and the insurer is what an
    // auditor filters on. Same choice UpsertMappingCommand makes.
    public string? ResourceId => ConnectionId.ToString();
}
