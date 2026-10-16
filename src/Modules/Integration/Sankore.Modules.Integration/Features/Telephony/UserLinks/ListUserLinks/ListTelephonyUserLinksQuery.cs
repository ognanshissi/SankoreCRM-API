namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.ListUserLinks;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The tenant's user links (TEL-B-05).
///
/// <para>
/// Unpaged, deliberately. The ceiling is the number of a tenant's employees who hold a telephony
/// account — tens, not thousands — and a settings screen that pages its own staff list makes the
/// one question it exists to answer ("who is not linked") take several requests.
/// </para>
/// </summary>
/// <param name="IncludeDisabled">
/// False by default. A disabled link is history: it explains an old call and is not something the
/// screen asks an administrator to act on.
/// </param>
internal sealed record ListTelephonyUserLinksQuery(bool IncludeDisabled = false)
    : IRequest<Result<IReadOnlyList<TelephonyUserLinkDto>>>;
