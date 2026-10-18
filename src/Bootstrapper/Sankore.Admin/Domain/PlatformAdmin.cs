namespace Sankore.Admin.Domain;

/// <summary>
/// An operator of the platform itself — the person who creates and suspends tenants from the
/// admin front-end.
///
/// <para>
/// Deliberately NOT a tenant user (<c>AppUser</c> in Sankore.Api): those live in a tenant and
/// sign in through it, whereas this account must keep working when every tenant is suspended,
/// and must never be reachable from a tenant's own login page. Separate table, separate signing
/// key, separate audience.
/// </para>
/// </summary>
public sealed class PlatformAdmin
{
    /// <summary>Consecutive failures after which the account locks.</summary>
    public const int MaxFailedLogins = 5;

    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public Guid Id { get; private set; }
    public string Email { get; private set; } = null!;
    public string NormalizedEmail { get; private set; } = null!;
    public string FullName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public bool IsActive { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private PlatformAdmin() { } // EF Core

    /// <summary>The hash is computed by the caller: the entity never sees a clear password.</summary>
    public static PlatformAdmin Create(string email, string fullName, string passwordHash)
    {
        var now = DateTimeOffset.UtcNow;
        return new PlatformAdmin
        {
            Id = Guid.NewGuid(),
            Email = email.Trim(),
            NormalizedEmail = Normalize(email),
            FullName = fullName.Trim(),
            PasswordHash = passwordHash,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = now;
        UpdatedAt = now;
    }

    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= MaxFailedLogins)
        {
            LockedUntil = now.Add(LockoutDuration);
            FailedLoginCount = 0;
        }
        UpdatedAt = now;
    }

    /// <summary>Used when a hash produced by an older algorithm is upgraded on a successful login.</summary>
    public void ReplacePasswordHash(string passwordHash)
    {
        PasswordHash = passwordHash;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
