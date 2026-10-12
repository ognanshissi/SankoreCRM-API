using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;

namespace Sankore.Modules.Administration.Features.Users.Security;

/// <summary>
/// Prévient le supérieur d'un utilisateur qu'une connexion a été signalée. Avale ses échecs comme
/// les autres notifieurs : les sessions sont déjà fermées, c'est la protection qui compte.
/// </summary>
internal sealed class SuspiciousLoginNotifier(
    AdministrationDbContext db,
    ModuleEmailSender mail,
    ILogger<SuspiciousLoginNotifier> logger)
{
    public const string Template = "security.suspicious-login-reported";

    public async Task NotifyAsync(AppUser user, UserLoginLocation? login, string? comment, int endedSessions, CancellationToken ct)
    {
        try
        {
            if (user.ReportsToUserId is not { } managerId)
            {
                logger.LogWarning("User {UserId} reported a suspicious login but has no manager to notify.", user.Id);
                return;
            }

            var manager = await db.Users.FirstOrDefaultAsync(u => u.Id == managerId, ct);
            if (manager is null) return;

            await mail.SendAsync(
                user.TenantId,
                manager,
                Template,
                $"{Template}-{user.Id}-{login?.Id.ToString() ?? DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")}",
                new Dictionary<string, object>
                {
                    ["user_name"] = user.FullName,
                    ["user_email"] = user.Email ?? string.Empty,
                    ["login_at"] = login is null ? "non précisée" : $"{login.OccuredAt:dd/MM/yyyy HH:mm} UTC",
                    ["login_ip"] = login?.IpAddress ?? "inconnue",
                    ["login_device"] = login is null ? "non précisé" : $"{login.Browser ?? "Navigateur inconnu"} · {login.Platform}",
                    ["comment"] = string.IsNullOrWhiteSpace(comment) ? "aucun" : comment.Trim(),
                    ["ended_sessions"] = endedSessions,
                },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Suspicious-login notification for user {UserId} could not be queued.", user.Id);
        }
    }
}
