namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tracks a published SDK version with its SRI hash.
/// Platform-level (no TenantId). One version per Major is marked IsCurrent.
/// Old versions remain servable forever (immutable URLs).
/// </summary>
public sealed class SdkVersion
{
    public Guid Id { get; private set; }

    /// <summary>Semver version string, e.g. "1.0.0".</summary>
    public string Version { get; private set; } = default!;

    /// <summary>Major version number for alias resolution (e.g. 1 for "1.x.x").</summary>
    public int Major { get; private set; }

    /// <summary>SHA-384 SRI hash, e.g. "sha384-abc...".</summary>
    public string SriHash { get; private set; } = default!;

    /// <summary>File name served (always "forms.min.js").</summary>
    public string FileName { get; private set; } = "forms.min.js";

    /// <summary>True = this is the current version for its Major alias.</summary>
    public bool IsCurrent { get; private set; }

    public DateTimeOffset PublishedAt { get; private set; }

    private SdkVersion() { }

    public static SdkVersion Publish(string version, int major, string sriHash)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new DomainException("SDK version is required.");
        if (string.IsNullOrWhiteSpace(sriHash))
            throw new DomainException("SRI hash is required.");

        return new()
        {
            Id          = Guid.NewGuid(),
            Version     = version.Trim(),
            Major       = major,
            SriHash     = sriHash,
            IsCurrent   = true,
            PublishedAt = DateTimeOffset.UtcNow
        };
    }

    public void Revoke() => IsCurrent = false;
}
