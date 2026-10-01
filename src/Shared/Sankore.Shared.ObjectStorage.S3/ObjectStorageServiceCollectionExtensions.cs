namespace Sankore.Shared.ObjectStorage;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Registers one object backend per concern, keyed by the concern name.
///
/// <para>
/// Keyed, because a single host holds several: KYC evidence and spreadsheet imports share no
/// retention rule, no size ceiling and no blast radius, so they share no bucket. The concern name
/// IS the bucket name — <c>kyc-documents</c>, <c>imports</c> — rather than a second setting that
/// could drift from it, and the module that owns the concern declares the constant
/// (<c>KycObjectStorage.Concern</c>) so only one spelling of the word ever exists.
/// </para>
/// </summary>
public static class ObjectStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IObjectBackend"/> keyed by <paramref name="concern"/>: an
    /// <see cref="R2ObjectBackend"/> on the bucket of that name when <c>ObjectStorage:R2</c> is
    /// configured, a <see cref="LocalObjectBackend"/> on
    /// <paramref name="localBasePathFallback"/> otherwise.
    ///
    /// <para>
    /// The choice is made here, at start-up, and the credential is validated here too. A bucket
    /// that is half configured fails the boot rather than falling back to the filesystem: the
    /// fallback is a deliberate deployment (a developer machine, a single-node install), and
    /// quietly becoming one because a secret failed to be injected would write evidence to a
    /// container volume that the next redeploy discards — invisibly, until someone asks for a
    /// document that is no longer there.
    /// </para>
    /// </summary>
    /// <param name="concern">
    /// The DI key and the bucket name. Must be a legal bucket name, since it is one.
    /// </param>
    /// <param name="localBasePathFallback">
    /// Where the filesystem backend stores objects when no bucket is configured. Resolved by the
    /// caller — only the host can see its content root.
    /// </param>
    public static IServiceCollection AddObjectBackend(
        this IServiceCollection services,
        IConfiguration config,
        string concern,
        string localBasePathFallback)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(concern);
        ArgumentException.ThrowIfNullOrWhiteSpace(localBasePathFallback);

        var options = ObjectStorageOptions.FromConfiguration(config);

        if (!options.IsConfigured)
        {
            services.AddKeyedSingleton<IObjectBackend>(concern, (sp, _) =>
                new LocalObjectBackend(localBasePathFallback, Logger<LocalObjectBackend>(sp)));

            return services;
        }

        // Partially configured: name the missing key and stop.
        options.Validate();
        ValidateBucketName(concern);

        services.AddKeyedSingleton<IObjectBackend>(concern, (sp, _) =>
            new R2ObjectBackend(options, concern, Logger<R2ObjectBackend>(sp)));

        return services;
    }

    /// <summary>
    /// <c>true</c> when a bucket is configured — for a host that wants to log which medium it
    /// ended up on, and for a migration that must know whether there is a destination at all.
    /// </summary>
    public static bool IsObjectStorageConfigured(this IConfiguration config) =>
        ObjectStorageOptions.FromConfiguration(config).IsConfigured;

    /// <summary>
    /// The concern is sent to the provider as a bucket name, so an illegal one fails at the first
    /// write with a signature error that names neither the bucket nor the concern. Checked once,
    /// here, where the name is still a C# constant someone can fix.
    /// </summary>
    private static void ValidateBucketName(string concern)
    {
        var legal =
            concern.Length is >= 3 and <= 63
            && char.IsAsciiLetterOrDigit(concern[0])
            && char.IsAsciiLetterOrDigit(concern[^1])
            && concern.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterLower(c) || c is '-' or '.');

        if (!legal)
            throw new ArgumentException(
                $"'{concern}' cannot be a bucket name: 3-63 characters, lower-case letters, "
                + "digits, '-' and '.', starting and ending alphanumeric. The concern name is the "
                + "bucket name.",
                nameof(concern));
    }

    /// <summary>
    /// Logging is registered in every host, but not in every test that resolves a backend, and a
    /// missing logger must not be the reason a storage registration cannot be exercised.
    /// </summary>
    private static ILogger<T> Logger<T>(IServiceProvider sp) =>
        sp.GetService<ILogger<T>>() ?? NullLogger<T>.Instance;
}
