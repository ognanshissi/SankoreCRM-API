namespace Sankore.Modules.Integration.Features.Insurance;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Infrastructure.ClaimStorage;
using Sankore.Shared.ObjectStorage;
using Sankore.Modules.Integration.Features.Insurance.Complaints;
using Sankore.Modules.Integration.Features.Insurance.Retention;

/// <summary>
/// Services of the insurance area. One line in <c>AddIntegrationModule</c>, the shape every other
/// area uses, so two areas written at once cannot collide in the module root.
/// </summary>
internal static class InsuranceServiceRegistration
{
    internal static IServiceCollection AddInsuranceServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ASS-12's retention period. A HOST setting, never a tenant parameter — see the options
        // type for why a tenant able to edit a regulatory floor is a tenant able to destroy
        // evidence a controller may still demand. Bound without validation at boot on purpose:
        // the options type clamps an absurd value to the floor rather than refusing to start, so
        // a typo in a configuration file cannot take the API down.
        services.Configure<InsuranceRetentionOptions>(
            configuration.GetSection(InsuranceRetentionOptions.SectionName));

        // The complaint deadlines of the règlement 01/CIMA/2024. Same shape and same reasoning as
        // the retention floor above, including the defaults being a placeholder rather than a
        // quotation of the text — see the options type.
        services.Configure<ComplaintOptions>(
            configuration.GetSection(ComplaintOptions.SectionName));

        // ── Claim pieces (ASS-09) ───────────────────────────────────────────
        services.Configure<ClaimStorageOptions>(
            configuration.GetSection(ClaimStorageOptions.SectionName));

        services.Configure<ClamAvOptions>(
            configuration.GetSection(ClamAvOptions.SectionName));

        // BasePath is resolved against the content root, which only the host can see and an
        // IOptions binding cannot. PostConfigure is where that becomes possible — the same shape
        // the batch socle and M02's document store both use.
        services.AddOptions<ClaimStorageOptions>()
            .PostConfigure<IHostEnvironment>((opts, env) =>
            {
                if (string.IsNullOrWhiteSpace(opts.BasePath))
                    opts.BasePath = Path.Combine(env.ContentRootPath, ClaimDocumentStorage.Concern);
            });

        // The medium, keyed to this concern so it cannot steal — or be stolen by — the batch
        // files' or the KYC images' backend. TryAdd, so this is only the FALLBACK: a host that
        // configures a bucket registers an R2 backend under the same key first and this does
        // nothing.
        services.TryAddKeyedSingleton<IObjectBackend>(ClaimDocumentStorage.Concern, (sp, _) =>
        {
            var options = sp.GetRequiredService<IOptions<ClaimStorageOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<LocalObjectBackend>>();

            return new LocalObjectBackend(
                ClaimStorageOptions.ResolveBasePath(options, logger), logger);
        });

        // Resolved from a factory so its eager key read happens on the first upload rather than at
        // boot — a deployment that distributes no insurance needs no claim-storage key.
        services.TryAddSingleton<IClaimDocumentStore>(sp => new EncryptedClaimDocumentStore(
            sp.GetRequiredKeyedService<IObjectBackend>(ClaimDocumentStorage.Concern),
            sp.GetRequiredService<IOptions<ClaimStorageOptions>>(),
            sp.GetRequiredService<ILogger<EncryptedClaimDocumentStore>>()));

        // The antivirus, chosen by whether an address is configured — and the UNCONFIGURED one
        // fails closed, so a deployment that has not set one transmits nothing rather than
        // transmitting everything. Decided here, once, at registration: a scanner picked per call
        // would be a decision somebody could get wrong per call site.
        var antivirusAddress = configuration[$"{ClamAvOptions.SectionName}:Address"];

        if (ClamAvClaimDocumentScanner.TryParseAddress(antivirusAddress, out _, out _))
        {
            services.TryAddSingleton<IClaimDocumentScanner>(sp =>
                new ClamAvClaimDocumentScanner(
                    sp.GetRequiredService<IOptions<ClamAvOptions>>(),
                    sp.GetRequiredService<ILogger<ClamAvClaimDocumentScanner>>()));
        }
        else
        {
            services.TryAddSingleton<IClaimDocumentScanner, UnconfiguredClaimDocumentScanner>();
        }

        // Scoped, and that matters: the probe caches one adapter lookup per connection for the
        // lifetime of a request, which is what keeps a page of fifty products from resolving the
        // same adapter fifty times. A singleton would carry a verdict across requests, and the
        // capability depends on the connection's own settings, which an administrator can edit.
        services.AddScoped<InsurerPricingProbe>();

        // Reaches M12 through IAdministrationModule — a PublicApi contract, never its assembly.
        services.AddScoped<LinkedCreditProductCheck>();

        // Scoped for the same reason as the pricing probe, one level up: it caches one catalogue
        // lookup per product code for the lifetime of a request, and a singleton would carry a
        // verdict across requests while an administrator retires the entry behind it.
        services.AddScoped<CrmProductCatalogue>();
        services.AddScoped<HoldingSubscription.HoldingSubscriptionChain>();

        return services;
    }
}
