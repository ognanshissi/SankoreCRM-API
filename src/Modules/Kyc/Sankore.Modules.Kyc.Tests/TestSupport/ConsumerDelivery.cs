namespace Sankore.Modules.Kyc.Tests.TestSupport;

using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

/// <summary>What MassTransit hands a consumer once the outbox published the event.</summary>
internal static class Delivered
{
    public static ConsumeContext<T> Of<T>(T evt) where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(evt);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }
}

/// <summary>The consumer creates its own DI scope; here every scope is the same container.</summary>
internal sealed class FixedScopeFactory(IServiceProvider sp) : IServiceScopeFactory
{
    public IServiceScope CreateScope() => new Scope(sp);

    private sealed class Scope(IServiceProvider sp) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = sp;
        public void Dispose() { }
    }
}
