namespace Sankore.Modules.Administration.Features.Credentials.IssueCredential;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Records a user's professional card (CIMA articles 503, 510) — a first issue or a renewal.
///
/// <para>
/// A mutation, so <see cref="ICommand"/> and <see cref="IResourceCommand"/>. The audit row is
/// half the point: the card is what makes a named person allowed to present insurance, and "who
/// recorded this licence, and when" is a question a national directorate asks directly.
/// </para>
///
/// <para>
/// <see cref="CardNumber"/> is marked sensitive so the audit trail records the FIELD NAME and
/// never the value, the convention every encrypted field in this solution follows.
/// </para>
/// </summary>
internal sealed record IssueCredentialCommand(
    Guid UserId,
    string Category,
    [property: SensitiveData] string CardNumber,
    string IssuingAuthority,
    DateOnly IssuedOn,
    DateOnly ExpiresOn,
    string? IndemnityInsurer,
    string? IndemnityPolicyReference,
    DateOnly? IndemnityExpiresOn,
    int? TrainingHours,
    DateOnly? TrainingCompletedOn,
    /// <summary>
    /// Why the previous card is being closed, when there is one. Required for a renewal: a
    /// register that cannot say whether a card lapsed, was replaced or was withdrawn answers none
    /// of the questions it exists for.
    /// </summary>
    string? SupersedeReason
) : IRequest<Result<CredentialDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "DistributionCredential";
    public string? ResourceId => UserId.ToString();
}

/// <summary>The user id travels in the route, so the body carries everything else.</summary>
internal sealed record IssueCredentialRequest(
    string Category,
    string CardNumber,
    string IssuingAuthority,
    DateOnly IssuedOn,
    DateOnly ExpiresOn,
    string? IndemnityInsurer = null,
    string? IndemnityPolicyReference = null,
    DateOnly? IndemnityExpiresOn = null,
    int? TrainingHours = null,
    DateOnly? TrainingCompletedOn = null,
    string? SupersedeReason = null);
