using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;

namespace Sankore.Modules.Administration.Features.Users.AgentSettings;

/// <summary>
/// Prévient le supérieur d'un agent de son absence et du remplaçant choisi. Comme les autres
/// notifieurs du module, il avale ses échecs : l'absence est déjà enregistrée, une panne de
/// messagerie ne doit pas la défaire.
/// </summary>
internal sealed class AgentAbsenceNotifier(
    AdministrationDbContext db,
    ModuleEmailSender mail,
    ILogger<AgentAbsenceNotifier> logger)
{
    public const string Template = "agent.absence-declared";

    public async Task NotifyManagerAsync(AppUser agent, AbsenceDeclaration absence, AppUser? replacement, CancellationToken ct)
    {
        try
        {
            if (agent.ReportsToUserId is not { } managerId)
            {
                logger.LogInformation("Agent {AgentId} has no manager; absence notification skipped.", agent.Id);
                return;
            }

            var manager = await db.Users.FirstOrDefaultAsync(u => u.Id == managerId, ct);
            if (manager is null) return;

            await mail.SendAsync(
                agent.TenantId,
                manager,
                Template,
                // Une même absence ne prévient qu'une fois ; la modifier (dates, remplaçant) prévient à nouveau.
                $"{Template}-{agent.Id}-{absence.From:yyyyMMdd}-{absence.To:yyyyMMdd}-{absence.ReplacementUserId}",
                new Dictionary<string, object>
                {
                    ["agent_name"] = agent.FullName,
                    ["absence_from"] = absence.From.ToString("dd/MM/yyyy"),
                    ["absence_to"] = absence.To.ToString("dd/MM/yyyy"),
                    ["replacement_name"] = replacement?.FullName ?? "aucun",
                    ["tasks_transferred"] = absence.TransferTasks ? "oui" : "non",
                },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Absence notification for agent {AgentId} could not be queued.", agent.Id);
        }
    }
}
