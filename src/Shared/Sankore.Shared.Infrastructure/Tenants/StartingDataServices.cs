namespace Sankore.Shared.Infrastructure.Tenants;

using MediatR;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Sends each command of a starting-data step in a DI scope of its own, as an HTTP request would.
///
/// <para>
/// One scope for a whole step made every command share its DbContexts, and each command opens an
/// ambient transaction (<c>TransactionBehavior</c>): a context enlisted by the first one was then
/// used by the next — « This connection was used with an ambient transaction » — on the first
/// handler that read another module (the client creation reading the institution's currency). The
/// tenant and the author stay those of the step: <c>BackgroundJobContext</c> is ambient, and a new
/// scope builds its <c>ITenantContext</c> and <c>ICurrentUser</c> from it, as « Copier en
/// production » does.
/// </para>
/// </summary>
public sealed class ScopedSender(IServiceScopeFactory scopes) : ISender
{
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, cancellationToken);
    }

    public async Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, cancellationToken);
    }

    public async Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, cancellationToken);
    }

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("A starting-data step sends commands, it does not stream.");

    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("A starting-data step sends commands, it does not stream.");
}

/// <summary>
/// The services a step sees: its own scope's, except <see cref="ISender"/>, which is a
/// <see cref="ScopedSender"/>. A step's direct reads stay in its scope; its commands do not.
/// </summary>
public sealed class StartingDataServices(IServiceProvider inner, ISender sender) : IServiceProvider
{
    public object? GetService(Type serviceType)
        => serviceType == typeof(ISender) ? sender : inner.GetService(serviceType);
}
