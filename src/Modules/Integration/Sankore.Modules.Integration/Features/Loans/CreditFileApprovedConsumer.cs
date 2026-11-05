namespace Sankore.Modules.Integration.Features.Loans;

using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Queues an approved credit file for the core banking. What it cannot send it leaves unsent rather
/// than failing: the project's page says why, and offers to send again once it is settled. Asking
/// the broker to redeliver would not help against a condition only a person can clear.
/// </summary>
public sealed class CreditFileApprovedConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<CreditFileApprovedConsumer> logger)
    : IConsumer<CreditFileApprovedEvent>
{
    public async Task Consume(ConsumeContext<CreditFileApprovedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;
        if (evt.TenantId == Guid.Empty) return;

        using var bg = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        if (!await sp.GetRequiredService<ITenantModules>().IsEnabledAsync(evt.TenantId, TenantModules.BankingMicrofinance, ct))
            return;

        var gateway = sp.GetRequiredService<IIntegrationModule>().CoreBanking;
        var status = await gateway.GetLoanApplicationStatusAsync(evt.ProjectId, ct);
        if (status.State != LoanApplicationState.NotRequested)
        {
            logger.LogInformation(
                "Credit file {ProjectId} is not queued: its application is {State}.", evt.ProjectId, status.State);
            return;
        }

        var facts = await sp.GetRequiredService<ILeadsModule>().GetCreditApplicationAsync(evt.TenantId, evt.ProjectId, ct);
        var request = facts is null ? null : await RequestOfAsync(facts, sp.GetRequiredService<IAdministrationModule>(), evt.TenantId, ct);
        if (request is null)
        {
            logger.LogWarning("Credit file {ProjectId} is not queued: it is incomplete.", evt.ProjectId);
            return;
        }

        try
        {
            await gateway.RequestLoanApplicationAsync(request, ct);
            // The facade does not save, it enlists in its caller's unit of work: this consumer is the caller.
            await sp.GetRequiredService<IntegrationDbContext>().SaveChangesAsync(ct);
        }
        catch (DomainException ex)
        {
            logger.LogWarning("Credit file {ProjectId} is not queued: {Reason}", evt.ProjectId, ex.Message);
        }
    }

    private static async Task<LoanApplicationRequest?> RequestOfAsync(
        CreditApplicationFacts facts, IAdministrationModule administration, Guid tenantId, CancellationToken ct)
    {
        if (facts is not { ClientId: { } client, ProductLineId: { } line, Amount: { } amount, Currency: { } currency, TermMonths: { } term })
            return null;

        var products = await administration.ListProductsAsync(tenantId, null, ct);
        return products.FirstOrDefault(p => p.Id == line) is { } product
            ? new LoanApplicationRequest(facts.ProjectId, client, product.Code, amount, currency, term, facts.Purpose)
            : null;
    }
}
