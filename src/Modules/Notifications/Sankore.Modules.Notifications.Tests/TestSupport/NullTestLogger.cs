namespace Sankore.Modules.Notifications.Tests.TestSupport;

using Microsoft.Extensions.Logging;

/// <summary>
/// No-op ILogger&lt;T&gt; for use in tests where T is an internal type.
/// NSubstitute.For&lt;ILogger&lt;InternalType&gt;&gt;() fails when the generic argument
/// is internal because Castle.DynamicProxy requires a strong-named InternalsVisibleTo.
/// </summary>
internal sealed class NullTestLogger<T> : ILogger<T>
{
    public static readonly NullTestLogger<T> Instance = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) { }
}
