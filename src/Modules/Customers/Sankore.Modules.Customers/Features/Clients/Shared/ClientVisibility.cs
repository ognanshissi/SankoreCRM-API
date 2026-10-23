namespace Sankore.Modules.Customers.Features.Clients.Shared;

using System.Linq.Expressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// A read of ONE client's record, by id. <see cref="ClientVisibilityBehavior{TRequest,TResponse}"/>
/// answers <c>CLIENT_NOT_FOUND</c> before the handler runs when the caller may not see that client.
/// The agency perimeter stays each handler's own check; this one only narrows it.
/// </summary>
internal interface IClientReadRequest
{
    Guid ClientId { get; }
}

/// <summary>
/// Which clients a caller sees inside their agency perimeter. Without <c>customers:read:all</c>
/// (Agent, CommercialAgent): the clients they advise, and a client with no advisor yet when they
/// created it. With it: the whole perimeter, as before.
/// </summary>
public sealed class ClientVisibility(ICurrentUser currentUser)
{
    public bool SeesAll => RecordVisibility.SeesAll(currentUser, Permissions.CanReadAllCustomers);

    /// <summary>The caller's clients only, as a SQL predicate — a list filters before it counts.</summary>
    public IQueryable<Client> Apply(IQueryable<Client> clients)
        => SeesAll ? clients : clients.Where(AdvisedBy(currentUser.Id));

    /// <summary>
    /// The user the restriction applies to, or null when the caller sees the whole perimeter — for
    /// a contract that is told who the actor is rather than reading it off the request.
    /// </summary>
    public Guid? RestrictedTo => SeesAll ? null : currentUser.Id;

    /// <summary>Same rule, for a client already loaded.</summary>
    public bool CanSee(Client client) => SeesAll || IsAdvisedBy(client.AdvisorUserId, client.CreatedBy, currentUser.Id);

    internal static bool IsAdvisedBy(Guid? advisorUserId, Guid createdBy, Guid me)
        => advisorUserId == me || (advisorUserId == null && createdBy == me);

    internal static Expression<Func<Client, bool>> AdvisedBy(Guid me)
        => c => c.AdvisorUserId == me || (c.AdvisorUserId == null && c.CreatedBy == me);
}

/// <summary>
/// Refuses an <see cref="IClientReadRequest"/> on a client outside the caller's visibility with
/// <c>CLIENT_NOT_FOUND</c> — the module's answer for « not yours », never a 403.
/// </summary>
internal sealed class ClientVisibilityBehavior<TRequest, TResponse>(IServiceProvider services)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not IClientReadRequest read)
            return await next();

        // Resolved here and not injected: this behavior sits in front of every request of every
        // module, and only a client read needs a CustomersDbContext.
        var visibility = services.GetRequiredService<ClientVisibility>();
        if (visibility.SeesAll)
            return await next();

        var me = services.GetRequiredService<ICurrentUser>().Id;
        var visible = await services.GetRequiredService<CustomersDbContext>().Clients
            .AsNoTracking()
            .Where(c => c.Id == read.ClientId)
            .AnyAsync(ClientVisibility.AdvisedBy(me), cancellationToken);

        return visible ? await next() : ResultFailures.Of<TResponse>(CustomerErrors.ClientNotFound);
    }
}
