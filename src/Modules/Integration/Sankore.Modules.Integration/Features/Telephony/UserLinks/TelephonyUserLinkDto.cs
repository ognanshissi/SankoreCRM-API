namespace Sankore.Modules.Integration.Features.Telephony.UserLinks;

using Sankore.Modules.Integration.Domain;

/// <summary>
/// One CRM user and their account at the provider, as the settings screen reads it (TEL-B-05).
///
/// <para>
/// <c>StatusReason</c> is here and is not an internal note: a link reads <c>Broken</c> because
/// the nightly check could not find the account any more, and an administrator who cannot see
/// WHY has no way to tell "the person left the provider" from "the API key lost its scope" —
/// two very different repairs.
/// </para>
/// </summary>
/// <param name="CrmUserName">
/// Resolved through <c>IAdministrationModule</c>, so a list shows a person and not a Guid. Null
/// when the id no longer resolves — a deleted account leaves a link behind, and the row is what
/// explains why nothing is being attributed.
/// </param>
internal sealed record TelephonyUserLinkDto(
    Guid Id,
    Guid CrmUserId,
    string? CrmUserName,
    string ExternalUserId,
    string? ExternalEmail,
    string? DefaultCallerNumber,
    TelUserLinkStatus Status,
    string? StatusReason,
    DateTimeOffset LinkedAt,
    uint Version);

/// <summary>
/// One account in the provider's directory, with the pairing this module PROPOSES (TEL-B-05).
///
/// <para>
/// <b>A proposal and never an applied link.</b> Matching on an address is a heuristic — an
/// institution whose agents share a generic mailbox, or whose provider account was opened with a
/// personal address, breaks it — and a wrong link attributes one agent's calls to another, with
/// the customer's number and the agent's note travelling to the wrong perimeter. So the import
/// shows what it found and an administrator confirms, which is also what the story asks for in
/// those words: <i>« rapprochement par e-mail à confirmer »</i>.
/// </para>
/// </summary>
/// <param name="IsActiveAtProvider">
/// What the provider says about the account. A disabled one is still listed, because it is the
/// explanation for a link going <c>Broken</c>.
/// </param>
/// <param name="ProposedCrmUserIsDisabled">
/// True when the proposed CRM account exists and is not active. Surfaced rather than filtered out:
/// hidden, the external user would read as having no CRM account at all, and an administrator
/// would create a second one.
/// </param>
internal sealed record TelephonyDirectoryEntryDto(
    string ExternalUserId,
    string? Email,
    string? DisplayName,
    bool IsActiveAtProvider,
    Guid? LinkedCrmUserId,
    Guid? ProposedCrmUserId,
    string? ProposedCrmUserName,
    bool ProposedCrmUserIsDisabled);

/// <summary>
/// The directory, plus what the pairing could not decide.
///
/// <para>
/// <c>AmbiguousEmails</c> carries the addresses several CRM accounts answer to, and those entries
/// come back with no proposal at all. Picking one would be picking which of two people a
/// conversation is filed against.
/// </para>
/// </summary>
internal sealed record TelephonyDirectoryDto(
    IReadOnlyList<TelephonyDirectoryEntryDto> Entries,
    IReadOnlyList<string> AmbiguousEmails);
