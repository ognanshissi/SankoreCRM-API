using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.NotificationSettings.SetMonthlyQuota;

/// <summary>Platform-admin only: sets or clears the monthly email quota for the tenant.</summary>
public sealed record SetMonthlyQuotaCommand(
    /// <summary>Null = unlimited.</summary>
    int? MonthlyQuotaLimit) : IRequest<Result>, ICommand;
