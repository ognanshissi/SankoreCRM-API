using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.CreateDraft;

/// <summary>
/// Creates a new inactive draft of an existing template at Version + 1.
/// Clones all steps, rules, and custom transitions without touching the source.
/// </summary>
internal sealed record CreateDraftCommand(Guid SourceTemplateId) : IRequest<Result<Guid>>, ICommand;
