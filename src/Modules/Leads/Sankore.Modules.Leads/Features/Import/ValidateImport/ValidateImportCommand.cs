namespace Sankore.Modules.Leads.Features.Import.ValidateImport;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Dry run: reads and validates a stored import file, capturing nothing.
/// Not an <c>ICommand</c> — it writes no data, so it skips the transaction
/// and audit behaviors.
/// </summary>
internal sealed record ValidateImportCommand(
    string FileReference,
    ImportDefaults Defaults
) : IRequest<Result<ValidateLeadImportResponse>>;
