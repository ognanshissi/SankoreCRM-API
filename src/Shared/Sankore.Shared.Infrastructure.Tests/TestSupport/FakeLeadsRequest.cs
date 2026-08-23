// Declared in this namespace so ExtractModule resolves the "Leads" segment
// from "Sankore.Modules.Leads.Features.Fake" when the test runs.
namespace Sankore.Modules.Leads.Features.Fake
{
    using MediatR;
    using Sankore.Shared.Kernel;

    public record FakeLeadsRequest : IRequest<Result>;
}
