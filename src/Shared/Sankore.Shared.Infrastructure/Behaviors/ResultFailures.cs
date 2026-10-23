namespace Sankore.Shared.Infrastructure.Behaviors;

using System.Collections.Concurrent;
using System.Reflection;
using Sankore.Shared.Kernel;

/// <summary>
/// Builds a failed <see cref="Result"/> or <see cref="Result{T}"/> for a pipeline behavior that
/// only knows its response as <c>TResponse</c> — shared by the gates that refuse a request before
/// its handler runs (agency perimeter, record visibility).
/// </summary>
public static class ResultFailures
{
    /// <summary>
    /// Cached <c>Result&lt;T&gt;.Fail(string)</c> per closed response type — reflection is only
    /// paid once per TResponse, not once per request.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, MethodInfo> FailFactories = new();

    /// <summary>
    /// The failure carrying <paramref name="code"/>. A response that is not a Result cannot carry
    /// it, so it is raised as a <see cref="DomainException"/> the exception handler translates.
    /// </summary>
    public static TResponse Of<TResponse>(string code, string? messageKey = null)
    {
        var responseType = typeof(TResponse);

        if (responseType == typeof(Result))
            return (TResponse)(object)Result.Fail(code);

        if (responseType.IsGenericType
            && responseType.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var factory = FailFactories.GetOrAdd(responseType, static t =>
                t.GetMethod(nameof(Result<object>.Fail), BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    [typeof(string)])
                ?? throw new InvalidOperationException(
                    $"Result<T>.Fail(string) not found on {t.Name}."));

            return (TResponse)factory.Invoke(null, [code])!;
        }

        throw messageKey is null ? new DomainException(code) : new DomainException(code, messageKey);
    }
}
