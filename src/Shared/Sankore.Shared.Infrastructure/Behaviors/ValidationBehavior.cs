namespace Sankore.Shared.Infrastructure.Behaviors;

using FluentValidation;
using MediatR;

/// <summary>
/// Runs every registered FluentValidation validator for TRequest before the
/// handler executes. If any validator fails, the pipeline short-circuits
/// with a ValidationException — the handler body never runs, so business
/// logic can assume its input is already well-formed.
///
/// Works uniformly across every module: a module only needs to add a
/// `FooValidator : AbstractValidator&lt;FooCommand&gt;` next to its command,
/// with zero extra wiring per feature.
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);

        var failures = (await Task.WhenAll(
                validators.Select(v => v.ValidateAsync(context, cancellationToken))))
            .SelectMany(result => result.Errors)
            .Where(failure => failure is not null)
            .ToList();

        if (failures.Count > 0)
            throw new ValidationException(failures);

        return await next();
    }
}
