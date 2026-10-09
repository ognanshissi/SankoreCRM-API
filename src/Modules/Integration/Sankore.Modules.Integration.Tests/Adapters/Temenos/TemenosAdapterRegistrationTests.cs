namespace Sankore.Modules.Integration.Tests.Adapters.Temenos;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Adapters.Temenos;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// What <c>AddTemenosAdapter</c> puts in the container.
///
/// <para>
/// Worth its own file because the failure mode is total and silent: the adapter is reached by
/// <c>IntegrationAdapterResolver.ResolveAdapter</c> through a keyed lookup on
/// <c>connection.Kind.ToString()</c>, so one character wrong in the key makes every command of
/// every Temenos tenant answer <c>INTEGRATION_ADAPTER_NOT_REGISTERED</c> — hours later, in a job
/// log, with the whole adapter perfectly correct and never called.
/// </para>
/// </summary>
public sealed class TemenosAdapterRegistrationTests
{
    private static IServiceCollection Registered(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTemenosAdapter(config);

        return services;
    }

    [Fact]
    public void The_adapter_is_registered_under_the_kind_the_resolver_looks_it_up_by()
    {
        var descriptor = Registered().Should().ContainSingle(d =>
            d.ServiceType == typeof(ICbsAdapter)).Subject;

        descriptor.ServiceKey.Should().Be(IntegrationKind.Temenos.ToString());
        descriptor.KeyedImplementationType.Should().Be<TemenosAdapter>();

        // Scoped, like every adapter: it resolves the tenant's connection once per scope and
        // holds it, and a singleton would carry one tenant's binding into the next tenant's job.
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void The_token_cache_is_a_singleton_because_a_cache_per_scope_is_a_token_per_call()
    {
        var descriptor = Registered().Should().ContainSingle(d =>
            d.ServiceType == typeof(TemenosTokenCache)).Subject;

        // The dispatcher creates a scope per command; a cold cache in each would mean one round
        // trip to the authorisation server for every single write.
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void The_named_HttpClient_the_transport_asks_for_is_configured()
    {
        using var provider = Registered().BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(TemenosTransport.HttpClientName);

        // No BaseAddress: one pooled client serves every tenant's connection and each has its own
        // base URL, so every request is built with an absolute URI instead.
        client.BaseAddress.Should().BeNull();

        // Infinite, because the per-call budget is a linked CancellationToken in the transport —
        // which is what lets our own expiry be told apart from the caller giving up.
        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void The_named_HttpClient_carries_no_default_authorisation_header()
    {
        using var provider = Registered().BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(TemenosTransport.HttpClientName);

        // A bearer token on DefaultRequestHeaders here is how one tenant calls with another
        // tenant's credentials: the pool is shared by every connection of every tenant. The token
        // is set on the request message, per call.
        client.DefaultRequestHeaders.Authorization.Should().BeNull();
        client.DefaultRequestHeaders.Should().BeEmpty();
    }

    [Fact]
    public void The_options_bind_from_the_section_the_deployment_configures()
    {
        using var provider = Registered(
            ("Integration:Temenos:TokenRenewalMargin", "00:00:30"),
            ("Integration:Temenos:TransactionPageSize", "25")).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<TemenosAdapterOptions>>().Value;

        options.TokenRenewalMargin.Should().Be(TimeSpan.FromSeconds(30));
        options.TransactionPageSize.Should().Be(25);
    }

    [Theory]
    [InlineData("Integration:Temenos:TokenRenewalMargin", "-00:01:00")]
    [InlineData("Integration:Temenos:DefaultTimeout", "00:00:00")]
    [InlineData("Integration:Temenos:TokenTimeout", "00:00:00")]
    [InlineData("Integration:Temenos:FallbackTokenLifetime", "00:00:00")]
    [InlineData("Integration:Temenos:TransactionPageSize", "0")]
    [InlineData("Integration:Temenos:MaxPagesPerAggregation", "0")]
    [InlineData("Integration:Temenos:MaxResponseBytes", "0")]
    public void A_nonsensical_option_is_refused_and_the_failure_names_the_key(string key, string value)
    {
        using var provider = Registered((key, value)).BuildServiceProvider();

        // Validated rather than clamped: a zero timeout or a negative renewal margin would
        // otherwise surface as a failed customer creation in a Hangfire job, hours after the
        // deployment that caused it.
        var act = () => provider.GetRequiredService<IOptions<TemenosAdapterOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainMatch($"*{key.Split(':')[^1]}*");
    }

    [Fact]
    public void The_defaults_are_usable_with_no_configuration_at_all()
    {
        using var provider = Registered().BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<TemenosAdapterOptions>>().Value;

        options.TokenRenewalMargin.Should().BePositive();
        options.DefaultTimeout.Should().BePositive();
        options.TransactionPageSize.Should().BePositive();
    }
}
