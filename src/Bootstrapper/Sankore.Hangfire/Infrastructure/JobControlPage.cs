namespace Sankore.Hangfire.Infrastructure;

using System.Text;
using global::Hangfire.Common;
using global::Hangfire.Dashboard;
using global::Hangfire.Dashboard.Pages;
using global::Hangfire.Storage;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// One screen listing every recurring job — running and paused — with Pause,
/// Resume, and the bulk "Pause all" / "Resume all" the stock dashboard has no
/// equivalent for. The built-in Recurring Jobs page stays available for detail
/// and Trigger-now.
/// </summary>
public sealed class JobControlPage : RazorPage
{
    /// <summary>Route of this page, relative to the dashboard root.</summary>
    public const string PagePath = "/sankore/jobs";

    public const string PauseCommandPath = "/sankore/jobs/pause";
    public const string ResumeCommandPath = "/sankore/jobs/resume";
    public const string PauseAllCommandPath = "/sankore/jobs/pause-all";
    public const string ResumeAllCommandPath = "/sankore/jobs/resume-all";

    public override void Execute()
    {
        Layout = new LayoutPage("Job control");

        var store = new RecurringJobPauseStore(Storage, Context.GetRecurringJobManager());
        var pausedIds = store.GetPausedIds();

        using var connection = Storage.GetConnection();
        var active = connection.GetRecurringJobs().OrderBy(j => j.Id, StringComparer.Ordinal).ToList();

        WriteLiteral("<div class=\"row\"><div class=\"col-md-12\">");
        WriteLiteral("<h1 class=\"page-header\">Job control</h1>");

        WriteLiteral(
            "<p class=\"text-muted\">Pausing removes a recurring job from Hangfire's schedule and "
            + "remembers its definition, so it stays paused across a Sankore.Api restart. "
            + "Resuming re-registers it with its original schedule. "
            + "Jobs already enqueued or running are not affected — manage those from the Jobs pages.</p>");

        WriteBulkActions(active.Count, pausedIds.Count);

        WriteActiveTable(active, store);
        WritePausedTable(pausedIds, store);

        WriteLiteral("</div></div>");
    }

    private void WriteBulkActions(int activeCount, int pausedCount)
    {
        var builder = new StringBuilder();
        builder.Append("<div class=\"btn-toolbar\" style=\"margin-bottom:15px;\">");

        builder.Append("<button class=\"btn btn-sm btn-warning\"")
               .Append(activeCount == 0 || IsReadOnly ? " disabled=\"disabled\"" : string.Empty)
               .Append(" data-ajax=\"").Append(Url.To(PauseAllCommandPath)).Append('"')
               .Append(" data-confirm=\"Pause all ").Append(activeCount)
               .Append(" recurring job(s)? Nothing new will be scheduled until you resume them.\">")
               .Append("<span class=\"glyphicon glyphicon-pause\"></span> Pause all</button> ");

        builder.Append("<button class=\"btn btn-sm btn-success\"")
               .Append(pausedCount == 0 || IsReadOnly ? " disabled=\"disabled\"" : string.Empty)
               .Append(" data-ajax=\"").Append(Url.To(ResumeAllCommandPath)).Append('"')
               .Append(" data-confirm=\"Resume all ").Append(pausedCount).Append(" paused job(s)?\">")
               .Append("<span class=\"glyphicon glyphicon-play\"></span> Resume all</button>");

        builder.Append("</div>");
        WriteLiteral(builder.ToString());
    }

    private void WriteActiveTable(List<RecurringJobDto> active, RecurringJobPauseStore store)
    {
        WriteLiteral($"<h2>Running <small>{active.Count}</small></h2>");

        if (active.Count == 0)
        {
            WriteLiteral("<div class=\"alert alert-info\">No recurring job is scheduled.</div>");
            return;
        }

        var builder = new StringBuilder();
        builder.Append("<div class=\"js-jobs-list\"><div class=\"btn-toolbar\" style=\"margin-bottom:10px;\">")
               .Append("<button class=\"js-jobs-list-command btn btn-sm btn-warning\" ")
               .Append("data-url=\"").Append(Url.To(PauseCommandPath)).Append("\" ")
               .Append("data-loading-text=\"Pausing…\" disabled=\"disabled\">")
               .Append("<span class=\"glyphicon glyphicon-pause\"></span> Pause selected</button></div>");

        builder.Append("<table class=\"table\"><thead><tr>")
               .Append("<th class=\"min-width\"><input type=\"checkbox\" class=\"js-jobs-list-select-all\" /></th>")
               .Append("<th>Id</th><th>Job</th><th>Cron</th><th>Next execution</th><th>Last execution</th><th>Error</th>")
               .Append("</tr></thead><tbody>");

        foreach (var job in active)
        {
            // A job can be in Hangfire's set while this host is mid-pause; render it
            // as paused rather than offering a second Pause that would no-op.
            var paused = store.IsPaused(job.Id);

            builder.Append("<tr class=\"js-jobs-list-row hover\">")
                   .Append("<td><input type=\"checkbox\" class=\"js-jobs-list-checkbox\" name=\"jobs[]\" value=\"")
                   .Append(Html.HtmlEncode(job.Id)).Append('"')
                   .Append(paused ? " disabled=\"disabled\"" : string.Empty)
                   .Append(" /></td>")
                   .Append("<td>").Append(Html.HtmlEncode(job.Id)).Append("</td>")
                   .Append("<td>").Append(DescribeJob(job)).Append("</td>")
                   .Append("<td><code>").Append(Html.HtmlEncode(job.Cron ?? string.Empty)).Append("</code></td>")
                   .Append("<td>").Append(FormatUtc(job.NextExecution)).Append("</td>")
                   .Append("<td>").Append(FormatUtc(job.LastExecution)).Append("</td>")
                   .Append("<td>").Append(
                       string.IsNullOrWhiteSpace(job.Error)
                           ? string.Empty
                           : $"<span class=\"label label-danger\">{Html.HtmlEncode(job.Error)}</span>")
                   .Append("</td></tr>");
        }

        builder.Append("</tbody></table></div>");
        WriteLiteral(builder.ToString());
    }

    private void WritePausedTable(IReadOnlyCollection<string> pausedIds, RecurringJobPauseStore store)
    {
        WriteLiteral($"<h2>Paused <small>{pausedIds.Count}</small></h2>");

        if (pausedIds.Count == 0)
        {
            WriteLiteral("<div class=\"alert alert-info\">No job is paused.</div>");
            return;
        }

        var builder = new StringBuilder();
        builder.Append("<div class=\"js-jobs-list\"><div class=\"btn-toolbar\" style=\"margin-bottom:10px;\">")
               .Append("<button class=\"js-jobs-list-command btn btn-sm btn-success\" ")
               .Append("data-url=\"").Append(Url.To(ResumeCommandPath)).Append("\" ")
               .Append("data-loading-text=\"Resuming…\" disabled=\"disabled\">")
               .Append("<span class=\"glyphicon glyphicon-play\"></span> Resume selected</button></div>");

        builder.Append("<table class=\"table\"><thead><tr>")
               .Append("<th class=\"min-width\"><input type=\"checkbox\" class=\"js-jobs-list-select-all\" /></th>")
               .Append("<th>Id</th><th>Job</th><th>Cron</th>")
               .Append("</tr></thead><tbody>");

        foreach (var id in pausedIds.OrderBy(x => x, StringComparer.Ordinal))
        {
            var snapshot = store.GetSnapshot(id);

            builder.Append("<tr class=\"js-jobs-list-row hover\">")
                   .Append("<td><input type=\"checkbox\" class=\"js-jobs-list-checkbox\" name=\"jobs[]\" value=\"")
                   .Append(Html.HtmlEncode(id)).Append("\" /></td>")
                   .Append("<td>").Append(Html.HtmlEncode(id)).Append("</td>")
                   .Append("<td>").Append(DescribeSnapshot(snapshot)).Append("</td>")
                   .Append("<td><code>")
                   .Append(Html.HtmlEncode(Field(snapshot, "Cron") ?? "unknown"))
                   .Append("</code></td></tr>");
        }

        builder.Append("</tbody></table></div>");
        WriteLiteral(builder.ToString());
    }

    private string DescribeJob(RecurringJobDto job)
        => job.Job is not null
            ? Html.HtmlEncode(Html.JobName(job.Job))
            : $"<span class=\"text-danger\">{Html.HtmlEncode(job.LoadException?.Message ?? "Unknown job type")}</span>";

    private string DescribeSnapshot(IReadOnlyDictionary<string, string>? snapshot)
    {
        var payload = Field(snapshot, "Job");
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "<span class=\"text-muted\">definition not captured</span>";
        }

        try
        {
            var job = InvocationData.DeserializePayload(payload).DeserializeJob();
            return Html.HtmlEncode(Html.JobName(job));
        }
        catch (JobLoadException ex)
        {
            return $"<span class=\"text-danger\">{Html.HtmlEncode(ex.Message)}</span>";
        }
    }

    private static string? Field(IReadOnlyDictionary<string, string>? snapshot, string name)
        => snapshot is not null && snapshot.TryGetValue(name, out var value) ? value : null;

    private static string FormatUtc(DateTime? value)
        => value.HasValue
            ? value.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture)
            : "<span class=\"text-muted\">—</span>";
}
