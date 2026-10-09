namespace Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Outcome of a call to an external system, carrying WHY it failed and not only that it did.
///
/// <para>
/// Deliberately not <c>Sankore.Shared.Kernel.Result</c>: that type answers "did the business
/// rule hold", and every failure of it is the caller's problem. Here the failure's family
/// decides what the platform does next — retry with backoff, park in the rejection queue, or
/// wake an administrator — so the family is part of the type rather than a convention on the
/// error string (INT-02).
/// </para>
///
/// <para>
/// An outage is never recorded as a refusal. That distinction is the one M02 learned on
/// biometry: rejecting an honest client over our own downtime is worse than waiting.
/// </para>
/// </summary>
public class IntegrationResult
{
    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    /// <summary>Stable error code, never a sentence. Null on success.</summary>
    public string? Code { get; }

    /// <summary>Operator-facing detail. Never carries a payload or personal data.</summary>
    public string? Detail { get; }

    /// <summary>Null on success; set on every failure.</summary>
    public ErrorFamily? Family { get; }

    protected IntegrationResult(bool isSuccess, ErrorFamily? family, string? code, string? detail)
    {
        if (isSuccess && (family is not null || code is not null))
            throw new InvalidOperationException("A successful IntegrationResult carries no error.");

        if (!isSuccess && (family is null || string.IsNullOrWhiteSpace(code)))
            throw new InvalidOperationException("A failed IntegrationResult needs a family and a code.");

        IsSuccess = isSuccess;
        Family = family;
        Code = code;
        Detail = detail;
    }

    /// <summary>True when the dispatcher should schedule another attempt.</summary>
    public bool IsRetryable => Family == ErrorFamily.Transient;

    public static IntegrationResult Ok() => new(true, null, null, null);

    public static IntegrationResult Transient(string code, string? detail = null)
        => new(false, ErrorFamily.Transient, code, detail);

    public static IntegrationResult Functional(string code, string? detail = null)
        => new(false, ErrorFamily.Functional, code, detail);

    public static IntegrationResult Technical(string code, string? detail = null)
        => new(false, ErrorFamily.Technical, code, detail);

    public static IntegrationResult<T> Ok<T>(T value) => IntegrationResult<T>.Ok(value);

    public static IntegrationResult<T> Transient<T>(string code, string? detail = null)
        => IntegrationResult<T>.Transient(code, detail);

    public static IntegrationResult<T> Functional<T>(string code, string? detail = null)
        => IntegrationResult<T>.Functional(code, detail);

    public static IntegrationResult<T> Technical<T>(string code, string? detail = null)
        => IntegrationResult<T>.Technical(code, detail);
}

/// <summary>Same, carrying a value on success.</summary>
public sealed class IntegrationResult<T> : IntegrationResult
{
    private readonly T? _value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Cannot read Value of a failed IntegrationResult. {Family}/{Code}");

    private IntegrationResult(bool isSuccess, T? value, ErrorFamily? family, string? code, string? detail)
        : base(isSuccess, family, code, detail)
        => _value = value;

    public static IntegrationResult<T> Ok(T value) => new(true, value, null, null, null);

    public static new IntegrationResult<T> Transient(string code, string? detail = null)
        => new(false, default, ErrorFamily.Transient, code, detail);

    public static new IntegrationResult<T> Functional(string code, string? detail = null)
        => new(false, default, ErrorFamily.Functional, code, detail);

    public static new IntegrationResult<T> Technical(string code, string? detail = null)
        => new(false, default, ErrorFamily.Technical, code, detail);
}
