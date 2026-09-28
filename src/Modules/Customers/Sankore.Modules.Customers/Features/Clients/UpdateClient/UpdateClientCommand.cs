namespace Sankore.Modules.Customers.Features.Clients.UpdateClient;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// US-M01-BE-07 — edits the NON-sensitive part of a client record: the fields an agent
/// may correct without a compliance motive (profession, employer, marital status,
/// declared income, language). Names, identity documents and addresses go through
/// <c>PATCH clients/{id}/sensitive</c>, which demands a reason and is audited separately.
///
/// <see cref="ExpectedVersion"/> is the <c>xmin</c> the caller read on the detail screen.
/// Sending it back turns a concurrent edit into an explicit <c>CONCURRENCY_CONFLICT</c>
/// instead of a silent last-writer-wins overwrite.
///
/// The command is deliberately NOT <c>IAgencyScopedRequest</c>: the client's agency is
/// not in the payload, so the perimeter can only be checked after loading the row — the
/// handler does it and answers <c>CLIENT_NOT_FOUND</c>, never a 403 that would confirm
/// the client exists elsewhere in the tenant.
/// </summary>
/// <remarks>
/// Every field is nullable and "null means leave unchanged" (the aggregate's
/// <c>UpdateNonSensitive</c> enforces that), which is what makes this a PATCH.
/// </remarks>
public sealed record UpdateClientCommand(
    Guid ClientId,
    uint ExpectedVersion,
    string? Profession,
    string? Employer,
    MaritalStatus? MaritalStatus,
    [property: SensitiveData] decimal? DeclaredIncome,
    string? DeclaredIncomeCurrency,
    string? PreferredLanguage
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
