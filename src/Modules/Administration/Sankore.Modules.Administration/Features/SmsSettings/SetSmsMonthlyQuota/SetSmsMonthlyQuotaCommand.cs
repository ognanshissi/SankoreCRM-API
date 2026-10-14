namespace Sankore.Modules.Administration.Features.SmsSettings.SetSmsMonthlyQuota;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Sets or clears the tenant's monthly SMS allowance.
///
/// <para>
/// Its own command rather than a field on <c>UpdateSmsSettingsCommand</c>, exactly as on the email
/// side: the two are set by different people. An institution's administrator chooses a provider
/// and a sender identity; a platform administrator caps what the institution may spend, and
/// folding the two into one call would mean either can overwrite the other's decision by
/// submitting a form that happened to include both fields.
/// </para>
/// </summary>
/// <param name="MonthlyQuotaLimit">
/// Null means no ceiling. Unlike email, that is a decision with a bill attached — a text message
/// costs per unit — so the screen says so rather than presenting it as the default.
/// </param>
public sealed record SetSmsMonthlyQuotaCommand(int? MonthlyQuotaLimit)
    : IRequest<Result>, ICommand;
