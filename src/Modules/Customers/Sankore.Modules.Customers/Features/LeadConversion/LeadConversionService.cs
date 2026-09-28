namespace Sankore.Modules.Customers.Features.LeadConversion;

using MediatR;
using Sankore.Modules.Customers.Features.LeadConversion.CreateClientFromLead;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Implements <c>ILeadConversionService</c>, the seam the module facade calls to satisfy
/// <see cref="ICustomersModule.CreateFromLeadAsync"/>.
///
/// <para>
/// It deliberately holds no logic of its own: dispatching through <see cref="ISender"/> is what
/// puts the conversion behind the MediatR pipeline, and that pipeline is where the guarantees
/// live — <c>ValidationBehavior</c> rejects an unusable lead, <c>TransactionBehavior</c> makes
/// the client and its outbox row commit together, and <c>AuditBehavior</c> records who
/// converted what (with the lead's personal data redacted, see the command). A facade that
/// called the handler directly would quietly lose all four.
/// </para>
/// </summary>
internal sealed class LeadConversionService(ISender sender) : ILeadConversionService
{
    public Task<Result<CreateFromLeadResult>> CreateFromLeadAsync(
        CreateFromLeadRequest request, CancellationToken ct)
        => sender.Send(CreateClientFromLeadCommand.From(request), ct);
}
