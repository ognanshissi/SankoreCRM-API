namespace Sankore.Modules.Administration.Features.Credentials.ExpiringCredentials;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Credentials that have lapsed or are about to, across the tenant.
///
/// <para>
/// This is the view that keeps the dispatching guard from being a surprise. An agent whose card
/// expires silently stops receiving insurance leads — which is the correct behaviour and a
/// terrible one to discover from a drop in volume. The report is read before that happens.
/// </para>
/// </summary>
/// <param name="WithinDays">
/// How far ahead to look. Zero returns only what has already lapsed.
/// </param>
internal sealed record ExpiringCredentialsQuery(int WithinDays)
    : IRequest<Result<IReadOnlyList<ExpiringCredentialDto>>>;

/// <param name="DaysUntilExpiry">Negative for a card that has already lapsed.</param>
/// <param name="IndemnityLapsed">
/// A recorded indemnity cover whose own term has ended. It invalidates the credential even while
/// the card itself is in date, which is the case an institution is least likely to notice.
/// </param>
public sealed record ExpiringCredentialDto(
    Guid CredentialId,
    Guid UserId,
    string Category,
    DateOnly ExpiresOn,
    int DaysUntilExpiry,
    DateOnly? IndemnityExpiresOn,
    bool IndemnityLapsed,
    bool IsValidToday);
