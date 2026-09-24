namespace Sankore.Modules.Leads.Features.Ingestion.Web;

/// <summary>
/// Validates captcha tokens against the configured provider.
/// Stub implementation always returns true when no provider is configured.
/// </summary>
internal interface ICaptchaValidator
{
    Task<bool> ValidateAsync(string provider, string token, string remoteIp, CancellationToken ct);
}

/// <summary>
/// Stub captcha validator for MVP. Always returns true.
/// Replace with reCAPTCHA v3 / Cloudflare Turnstile implementation when ready.
/// </summary>
internal sealed class StubCaptchaValidator : ICaptchaValidator
{
    public Task<bool> ValidateAsync(string provider, string token, string remoteIp, CancellationToken ct)
        => Task.FromResult(true);
}
