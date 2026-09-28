namespace Sankore.Modules.Customers.Features.Compliance;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Features.Compliance.Export;
using Sankore.Modules.Customers.Features.Compliance.Retention;

/// <summary>
/// DI registrations owned by the Compliance zone. Called from
/// <c>CustomersModule.AddCustomersModule(...)</c>.
/// <para>
/// Handlers and validators need nothing here — MediatR and FluentValidation discover them by
/// assembly scan. Only the Hangfire jobs do: Hangfire resolves a job type from the container, so
/// an unregistered job type fails at execution time rather than at startup.
/// </para>
/// <para>
/// <c>AddTransient</c> is the right lifetime: each job opens its own
/// <see cref="IServiceScopeFactory"/> scope for the DbContext, so the job object itself is
/// stateless and must not be shared across executions.
/// </para>
/// <para>
/// The cron schedules are NOT declared here — recurring-job registration lives in
/// <c>Program.cs</c> (it needs <c>ITenantStore</c> and the pause store). The schedules this zone
/// expects are:
/// <list type="bullet">
///   <item><c>customers-retention-scan</c> → <see cref="IdentifyRetentionCandidatesOrchestratorJob"/>, cron <c>0 4 1 * *</c> (global, monthly).</item>
/// </list>
/// <see cref="GenerateClientExportJob"/> is fire-and-forget, enqueued by the request handler, and
/// has no schedule.
/// </para>
/// </summary>
internal static class ComplianceServiceRegistration
{
    internal static IServiceCollection AddComplianceServices(this IServiceCollection s)
    {
        // Retention (US-M01-BE-29)
        s.AddTransient<IdentifyRetentionCandidatesOrchestratorJob>();
        s.AddTransient<IdentifyRetentionCandidatesJob>();

        // Export (US-M01-BE-30)
        s.AddTransient<GenerateClientExportJob>();

        return s;
    }
}
