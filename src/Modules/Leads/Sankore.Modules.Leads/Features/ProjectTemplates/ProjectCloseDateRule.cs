namespace Sankore.Modules.Leads.Features.ProjectTemplates;

/// <summary>
/// A template with a CRITICAL close date (a seasonal crop loan) refuses a project without one: an
/// estimate can be filled in later, a deadline that nobody wrote down cannot be chased.
/// </summary>
internal static class ProjectCloseDateRule
{
    /// <summary>True when <paramref name="product"/>'s template requires a date and none is given.</summary>
    public static async Task<bool> MissingAsync(
        ProjectTemplateResolver? templates, string? product, DateTimeOffset? closeDate, CancellationToken ct)
        => closeDate is null
        && templates is not null
        && (await templates.ResolveAsync(product, ct)).CriticalCloseDate;
}
