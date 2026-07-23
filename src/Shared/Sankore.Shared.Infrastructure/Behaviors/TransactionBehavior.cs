namespace Sankore.Shared.Infrastructure.Behaviors;

using System.Transactions;
using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Wraps every command (ICommand) in an ambient System.Transactions scope.
/// This is deliberately NOT tied to a specific module's DbContext type: it
/// uses an ambient TransactionScope so that whichever module's DbContext(s)
/// a handler touches, all SaveChangesAsync calls made during the request
/// commit or roll back together against PostgreSQL.
///
/// The scope only commits (Complete()) when the handler returns a
/// successful Result. A business-rule failure (Result.Fail) rolls back the
/// same as an exception would, so a handler can never leave a half-applied
/// mutation behind — important for financial correctness.
/// </summary>
public sealed class TransactionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not ICommand)
            return await next();

        using var scope = new TransactionScope(
            TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled);

        var response = await next();

        var shouldCommit = response switch
        {
            Result r => r.IsSuccess,
            _ => true
        };

        if (shouldCommit)
            scope.Complete();

        return response;
    }
}
