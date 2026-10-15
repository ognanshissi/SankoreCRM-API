namespace Sankore.Modules.Customers.Features.Holdings;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Features.Holdings.Shared;

/// <summary>
/// DI for the holdings zone (BNF-B-14, BNF-B-15). Three zone-specific services:
/// <see cref="HoldingWriteContext"/> sequences the gates every write runs, and
/// <see cref="SubjectLabelResolver"/> keeps the designation reads to one pair of queries.
/// <see cref="DraftHoldingOpener"/> is the third: the one body that opens the draft a lead
/// conversion or a won request leaves behind.
/// </summary>
internal static class HoldingsServiceRegistration
{
    internal static IServiceCollection AddHoldingsServices(this IServiceCollection services)
        => services
            .AddScoped<HoldingWriteContext>()
            .AddScoped<SubjectLabelResolver>()
            .AddScoped<DesignationSubjectValidator>()
            // L'ouverture du brouillon que laisse une issue commerciale — lead converti ou
            // demande gagnée. Partagée par les deux chemins : voir la classe.
            .AddScoped<DraftHoldingOpener>()
            // The back-office side (BNF-B-16). Deliberately NOT behind MediatR — see the class.
            .AddScoped<Sync.HoldingSyncService>();
}
