namespace Sankore.Modules.Leads.Features.TaskTypes.ListTaskTypes;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ListTaskTypesQuery(bool? ActiveOnly = null)
    : IRequest<Result<IReadOnlyList<TaskTypeDto>>>;
