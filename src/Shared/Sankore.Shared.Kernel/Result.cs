namespace Sankore.Shared.Kernel;

/// <summary>
/// Non-generic Result: represents the outcome of an operation that has no
/// return value, only success/failure with an optional error code.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string? Error { get; }

    protected Result(bool isSuccess, string? error)
    {
        if (isSuccess && error is not null)
            throw new InvalidOperationException("A successful result cannot have an error.");
        if (!isSuccess && error is null)
            throw new InvalidOperationException("A failed result must have an error.");

        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Ok() => new(true, null);
    public static Result Fail(string error) => new(false, error);

    public static Result<T> Ok<T>(T value) => Result<T>.Ok(value);
    public static Result<T> Fail<T>(string error) => Result<T>.Fail(error);
}

/// <summary>
/// Generic Result: same as Result but carries a value on success.
/// Handlers should always return Result&lt;T&gt; rather than throwing exceptions
/// for expected business failures (not found, invalid state, rule violation).
/// Exceptions remain reserved for truly exceptional / programmer-error cases.
/// </summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot access Value of a failed result. Error: {Error}");

    private Result(bool isSuccess, T? value, string? error) : base(isSuccess, error)
    {
        _value = value;
    }

    public static Result<T> Ok(T value) => new(true, value, null);
    public new static Result<T> Fail(string error) => new(false, default, error);
}
