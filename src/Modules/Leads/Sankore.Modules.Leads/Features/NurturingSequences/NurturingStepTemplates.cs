namespace Sankore.Modules.Leads.Features.NurturingSequences;

using Sankore.Modules.Leads.Features.NurturingSequences.CreateNurturingSequence;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// A step must name an e-mail template that exists: for a key it cannot find, the renderer sends the
/// message's raw JSON as its body — to a prospect, here. French is what the job falls back on.
/// </summary>
internal static class NurturingStepTemplates
{
    public const string NotFound = "NURTURING_TEMPLATE_NOT_FOUND";

    public static async Task<bool> AllExistAsync(
        INotificationsModule notifications, Guid tenantId, IEnumerable<StepInput> steps, CancellationToken ct)
    {
        foreach (var key in steps.Select(s => s.EmailTemplateKey).Distinct())
            if (!await notifications.HasActiveEmailTemplateAsync(tenantId, key, LanguageCode.Default, ct))
                return false;
        return true;
    }
}
