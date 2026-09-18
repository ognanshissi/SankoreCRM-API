namespace Sankore.Modules.Leads.Features.FindDuplicates;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Search for active leads that may be duplicates of the given probe.
/// At least one of PhoneNumber, Email, NationalId, or CustomerReference must be supplied.
/// FullName, DateOfBirth, and Location are optional additive signals that increase confidence.
/// </summary>
public sealed record FindDuplicatesQuery(
    string? PhoneNumber,
    string? Email,
    string? NationalId,
    string? CustomerReference,
    string? FullName,
    DateOnly? DateOfBirth,
    double? Latitude,
    double? Longitude,
    /// <summary>
    /// Override the minimum confidence threshold (0-100). Defaults to <see cref="IdentityMatchScorer.MinConfidence"/> (30).
    /// Raise this value (e.g. 70) to return only high-confidence matches.
    /// </summary>
    double? MinConfidence = null
) : IRequest<Result<IReadOnlyList<DuplicateMatchResult>>>;
