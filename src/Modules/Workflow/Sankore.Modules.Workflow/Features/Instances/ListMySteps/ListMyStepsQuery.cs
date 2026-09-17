using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.ListMySteps;

/// <summary>
/// Returns all <see cref="Domain.WorkflowInstanceStep"/> records that the current
/// user should act on: steps explicitly assigned to them OR steps whose
/// <c>ApproverRoleCode</c> matches one of their JWT roles (and not yet
/// explicitly assigned to someone else).
/// </summary>
internal sealed record ListMyStepsQuery : IRequest<Result<IReadOnlyList<MyStepDto>>>;
