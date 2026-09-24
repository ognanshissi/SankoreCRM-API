namespace Sankore.Hangfire.Infrastructure;

using global::Hangfire.Dashboard;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Registers the Sankore additions to the stock Hangfire dashboard: the Job
/// control page and the pause/resume commands behind it.
/// </summary>
public static class SankoreDashboard
{
    private static bool _registered;

    /// <summary>
    /// Must be called before <c>UseHangfireDashboard</c>. Routes and menu items live
    /// in static Hangfire state, so this is idempotent.
    /// </summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        var routes = DashboardRoutes.Routes;

        routes.AddRazorPage(JobControlPage.PagePath, _ => new JobControlPage());

        // AddBatchCommand reads the "jobs[]" form values the dashboard's own list
        // JavaScript posts, one call per selected id. AddRecurringBatchCommand would
        // hand us Hangfire's IRecurringJobManager instead of the DashboardContext the
        // pause store needs, so it is not the right overload here.
        routes.AddBatchCommand(JobControlPage.PauseCommandPath, (context, jobId) =>
        {
            if (!context.IsReadOnly)
            {
                Store(context).Pause(jobId);
            }
        });

        routes.AddBatchCommand(JobControlPage.ResumeCommandPath, (context, jobId) =>
        {
            if (!context.IsReadOnly)
            {
                Store(context).Resume(jobId);
            }
        });

        routes.AddCommand(JobControlPage.PauseAllCommandPath, context =>
        {
            if (context.IsReadOnly)
            {
                return false;
            }

            Store(context).PauseAll();
            return true;
        });

        routes.AddCommand(JobControlPage.ResumeAllCommandPath, context =>
        {
            if (context.IsReadOnly)
            {
                return false;
            }

            Store(context).ResumeAll();
            return true;
        });

        NavigationMenu.Items.Add(page => new MenuItem("Job control", page.Url.To(JobControlPage.PagePath))
        {
            Active = page.RequestPath.StartsWith(JobControlPage.PagePath, StringComparison.Ordinal),
        });
    }

    private static RecurringJobPauseStore Store(DashboardContext context)
        => new(context.Storage, context.GetRecurringJobManager());
}
