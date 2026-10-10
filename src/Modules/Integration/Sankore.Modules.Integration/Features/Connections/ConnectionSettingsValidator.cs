namespace Sankore.Modules.Integration.Features.Connections;

using FluentValidation;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What a command carrying connection settings must expose for the shared rules to read.
/// Create and Update validate the same settings object against the same per-kind rules, and this
/// is what lets those rules be written once instead of drifting between the two.
/// </summary>
internal interface IConnectionSettingsCarrier
{
    /// <summary>Needed by the rules: the SFTP coordinates are only mandatory in Batch mode.</summary>
    IntegrationMode Mode { get; }

    ConnectionSettings? Settings { get; }
}

/// <summary>
/// Per-<see cref="IntegrationKind"/> validation of <see cref="ConnectionSettings"/> (INT-03,
/// criterion 2), in the <c>When(x =&gt; x.Settings is TConcrete, …)</c> + cast idiom M13's
/// <c>CreateLeadSourceValidator</c> established.
///
/// <para>
/// Every failure is named <c>settings.&lt;camelCaseProp&gt;</c> so the 422 keys match the path the
/// field actually has in the request JSON. Without the explicit name FluentValidation reports the
/// C# expression — <c>((TemenosSettings)x.Settings).BaseUrl</c> — which no front-end can map back
/// to a form field.
/// </para>
///
/// <para>
/// Each rule-level condition re-tests the type with a pattern (<c>is TemenosSettings s</c>) rather
/// than trusting the enclosing <c>When</c> to have run first: FluentValidation composes a rule's
/// own condition with the shared one, and a cast in a condition that is evaluated first throws
/// instead of skipping.
/// </para>
/// </summary>
internal abstract class ConnectionSettingsValidator<TCommand> : AbstractValidator<TCommand>
    where TCommand : IConnectionSettingsCarrier
{
    protected ConnectionSettingsValidator()
    {
        RuleFor(x => x.Mode).IsInEnum().OverridePropertyName("mode");

        // Null covers two cases the host's converter cannot tell apart, because it answers null
        // for both: no settings object at all, and one whose "$kind" is missing or names a kind
        // this build does not know. The message therefore names the discriminator — otherwise a
        // caller who sent a full, well-formed Temenos object without "$kind" reads "settings is
        // required" and goes looking for the field they did send.
        RuleFor(x => x.Settings)
            .NotNull()
            .OverridePropertyName("settings")
            .WithMessage(
                "settings is required and must carry a \"$kind\" naming the connection kind "
                + "(Temenos, Amplitude, Sab, PerfectVision, Orass, Fake).");

        // Mode and settings shape must agree: Batch means files over SFTP, and a kind whose
        // settings carry no SFTP coordinates cannot run that way at all.
        RuleFor(x => x.Mode)
            .Must((cmd, mode) => mode != IntegrationMode.Batch || cmd.Settings is BatchCapableSettings)
            .When(x => x.Settings is not null)
            .OverridePropertyName("mode")
            .WithMessage("mode Batch requires file-based settings; this kind exposes none.");

        // ── Knobs shared by every kind ────────────────────────────────────
        When(x => x.Settings is not null, () =>
        {
            RuleFor(x => x.Settings!.RateLimitPerMinute)
                .GreaterThanOrEqualTo(0)
                .OverridePropertyName("settings.rateLimitPerMinute")
                .WithMessage("settings.rateLimitPerMinute cannot be negative (0 means unlimited).");

            RuleFor(x => x.Settings!.CircuitBreakerFailureThreshold)
                .GreaterThanOrEqualTo(0)
                .OverridePropertyName("settings.circuitBreakerFailureThreshold");

            RuleFor(x => x.Settings!.CircuitBreakerBreakSeconds)
                .GreaterThanOrEqualTo(0)
                .OverridePropertyName("settings.circuitBreakerBreakSeconds");

            // Capped: a per-call budget above five minutes is not a timeout, it is a hung
            // request holding a connection of the pool for the dispatcher's whole cycle.
            RuleFor(x => x.Settings!.TimeoutSeconds)
                .InclusiveBetween(0, 300)
                .OverridePropertyName("settings.timeoutSeconds")
                .WithMessage("settings.timeoutSeconds must be between 0 (default) and 300.");
        });

        // ── Temenos (INT-12/INT-13) ───────────────────────────────────────
        When(x => x.Settings is TemenosSettings, () =>
        {
            RuleFor(x => ((TemenosSettings)x.Settings!).BaseUrl)
                .NotEmpty()
                .OverridePropertyName("settings.baseUrl")
                .WithMessage("settings.baseUrl is required for a Temenos connection.");

            RuleFor(x => ((TemenosSettings)x.Settings!).BaseUrl)
                .Must(BeAnHttpsUrl)
                .When(x => x.Settings is TemenosSettings { BaseUrl.Length: > 0 })
                .OverridePropertyName("settings.baseUrl")
                .WithMessage("settings.baseUrl must be an absolute https URL.");

            RuleFor(x => ((TemenosSettings)x.Settings!).AuthMode)
                .IsInEnum()
                .OverridePropertyName("settings.authMode");

            // OAuth client-credentials cannot be attempted without the endpoint to call and the
            // client to call it as. Only the SECRET is absent from here: it lives in the vault.
            RuleFor(x => ((TemenosSettings)x.Settings!).TokenEndpoint)
                .NotEmpty()
                .When(x => x.Settings is TemenosSettings { AuthMode: TemenosAuthMode.OAuthClientCredentials })
                .OverridePropertyName("settings.tokenEndpoint")
                .WithMessage("settings.tokenEndpoint is required when authMode is OAuthClientCredentials.");

            RuleFor(x => ((TemenosSettings)x.Settings!).TokenEndpoint)
                .Must(BeAnHttpsUrl)
                .When(x => x.Settings is TemenosSettings
                {
                    AuthMode: TemenosAuthMode.OAuthClientCredentials,
                    TokenEndpoint.Length: > 0,
                })
                .OverridePropertyName("settings.tokenEndpoint")
                .WithMessage("settings.tokenEndpoint must be an absolute https URL.");

            RuleFor(x => ((TemenosSettings)x.Settings!).OAuthClientId)
                .NotEmpty()
                .When(x => x.Settings is TemenosSettings { AuthMode: TemenosAuthMode.OAuthClientCredentials })
                .OverridePropertyName("settings.oAuthClientId")
                .WithMessage("settings.oAuthClientId is required when authMode is OAuthClientCredentials.");

            RuleFor(x => ((TemenosSettings)x.Settings!).CompanyId)
                .MaximumLength(50)
                .OverridePropertyName("settings.companyId");

            RuleFor(x => ((TemenosSettings)x.Settings!).ApiVersion)
                .MaximumLength(20)
                .OverridePropertyName("settings.apiVersion");
        });

        // ── Amplitude (INT-31) ────────────────────────────────────────────
        When(x => x.Settings is AmplitudeSettings, () =>
        {
            RuleFor(x => ((AmplitudeSettings)x.Settings!).AmplitudeVersion)
                .IsInEnum()
                .OverridePropertyName("settings.amplitudeVersion");

            // Amplitude Up exposes API services; earlier releases do not and fall back to the
            // batch socle, where a base URL would be meaningless rather than merely unused.
            RuleFor(x => ((AmplitudeSettings)x.Settings!).BaseUrl)
                .NotEmpty()
                .When(x => x.Settings is AmplitudeSettings { AmplitudeVersion: AmplitudeVersion.Up })
                .OverridePropertyName("settings.baseUrl")
                .WithMessage("settings.baseUrl is required when amplitudeVersion is Up.");

            RuleFor(x => ((AmplitudeSettings)x.Settings!).BaseUrl)
                .Must(BeAnHttpsUrl)
                .When(x => x.Settings is AmplitudeSettings { BaseUrl.Length: > 0 })
                .OverridePropertyName("settings.baseUrl")
                .WithMessage("settings.baseUrl must be an absolute https URL.");
        });

        // ── SAB AT, through Open SAB (INT-32) ─────────────────────────────
        When(x => x.Settings is SabSettings, () =>
        {
            RuleFor(x => ((SabSettings)x.Settings!).BaseUrl)
                .NotEmpty()
                .OverridePropertyName("settings.baseUrl")
                .WithMessage("settings.baseUrl is required for a SAB connection.");

            RuleFor(x => ((SabSettings)x.Settings!).BaseUrl)
                .Must(BeAnHttpsUrl)
                .When(x => x.Settings is SabSettings { BaseUrl.Length: > 0 })
                .OverridePropertyName("settings.baseUrl")
                .WithMessage("settings.baseUrl must be an absolute https URL.");

            // Required and not optional: on a multi-IMF network such as CIF one installation
            // serves several institutions, and a call that does not say which lands on whichever
            // entity Open SAB defaults to.
            RuleFor(x => ((SabSettings)x.Settings!).Entity)
                .NotEmpty()
                .OverridePropertyName("settings.entity")
                .WithMessage("settings.entity is required for a SAB connection.");
        });

        // ── ORASS (ASS-06) ────────────────────────────────────────────────
        When(x => x.Settings is OrassSettings, () =>
        {
            RuleFor(x => ((OrassSettings)x.Settings!).BaseUrl)
                .Must(BeAnHttpsUrl)
                .When(x => x.Settings is OrassSettings { BaseUrl.Length: > 0 })
                .OverridePropertyName("settings.baseUrl")
                .WithMessage("settings.baseUrl must be an absolute https URL.");

            RuleFor(x => ((OrassSettings)x.Settings!).Branch)
                .IsInEnum()
                .OverridePropertyName("settings.branch");

            RuleFor(x => ((OrassSettings)x.Settings!).IntermediaryCode)
                .MaximumLength(50)
                .OverridePropertyName("settings.intermediaryCode");
        });

        // ── Every file-based kind (INT-24/INT-25) ─────────────────────────
        When(x => x.Settings is BatchCapableSettings, () =>
        {
            RuleFor(x => ((BatchCapableSettings)x.Settings!).SftpPort)
                .InclusiveBetween(1, 65535)
                .OverridePropertyName("settings.sftpPort");

            RuleFor(x => ((BatchCapableSettings)x.Settings!).FieldSeparator)
                .NotEmpty()
                .MaximumLength(3)
                .OverridePropertyName("settings.fieldSeparator");

            RuleFor(x => ((BatchCapableSettings)x.Settings!).FileEncoding)
                .NotEmpty()
                .OverridePropertyName("settings.fileEncoding");

            RuleFor(x => ((BatchCapableSettings)x.Settings!).RetentionDays)
                .GreaterThan(0)
                .OverridePropertyName("settings.retentionDays");

            RuleFor(x => ((BatchCapableSettings)x.Settings!).AckTimeoutHours)
                .GreaterThan(0)
                .OverridePropertyName("settings.ackTimeoutHours");
        });

        // Only in Batch mode are the coordinates mandatory: the same settings record also serves
        // a Relay connection, where the agent on the IMF's side owns the file system and SANKORE
        // never opens an SFTP session at all.
        When(x => x.Mode == IntegrationMode.Batch && x.Settings is BatchCapableSettings, () =>
        {
            RuleFor(x => ((BatchCapableSettings)x.Settings!).SftpHost)
                .NotEmpty()
                .OverridePropertyName("settings.sftpHost")
                .WithMessage("settings.sftpHost is required in Batch mode.");

            RuleFor(x => ((BatchCapableSettings)x.Settings!).OutboundDirectory)
                .NotEmpty()
                .OverridePropertyName("settings.outboundDirectory")
                .WithMessage("settings.outboundDirectory is required in Batch mode.");

            // Both directories, always: a connection that deposits without polling produces
            // files nobody acknowledges, and every Batched command then waits out its
            // ackTimeoutHours before alerting.
            RuleFor(x => ((BatchCapableSettings)x.Settings!).InboundDirectory)
                .NotEmpty()
                .OverridePropertyName("settings.inboundDirectory")
                .WithMessage("settings.inboundDirectory is required in Batch mode.");
        });
    }

    /// <summary>
    /// https only, as INT-03 requires. An on-premise CBS is still reachable over TLS, and a
    /// plain-http base URL would carry a bearer token across the IMF's own LAN.
    /// </summary>
    private static bool BeAnHttpsUrl(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps;
}
