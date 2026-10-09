namespace Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What the connected system can do, and in which mode. Exposed to callers so a screen can hide
/// what the IMF's own installation does not support, rather than offering a button that answers
/// a technical error (INT-02).
///
/// <para>
/// A capability that is absent from the map is NOT supported. That is why the lookup answers
/// <c>null</c> rather than defaulting to <see cref="CapabilityMode.Batch"/>: assuming batch
/// would make every unimplemented operation look like a slow one.
/// </para>
/// </summary>
public sealed class IntegrationCapabilities
{
    private readonly Dictionary<IntegrationCapability, CapabilityMode> _modes;

    public IntegrationCapabilities(IReadOnlyDictionary<IntegrationCapability, CapabilityMode> modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        _modes = new Dictionary<IntegrationCapability, CapabilityMode>(modes);
    }

    /// <summary>Nothing supported — what a blocked adapter declares until its spec arrives.</summary>
    public static IntegrationCapabilities None { get; } = new(new Dictionary<IntegrationCapability, CapabilityMode>());

    /// <summary>Every listed capability in one mode. The common shape for a batch-only adapter.</summary>
    public static IntegrationCapabilities All(CapabilityMode mode, params IntegrationCapability[] capabilities)
        => new(capabilities.ToDictionary(c => c, _ => mode));

    public IReadOnlyDictionary<IntegrationCapability, CapabilityMode> Modes => _modes;

    public bool Supports(IntegrationCapability capability) => _modes.ContainsKey(capability);

    /// <summary>The mode, or <c>null</c> when the capability is not supported at all.</summary>
    public CapabilityMode? ModeOf(IntegrationCapability capability)
        => _modes.TryGetValue(capability, out var mode) ? mode : null;

    public bool IsRealTime(IntegrationCapability capability)
        => ModeOf(capability) == CapabilityMode.RealTime;

    /// <summary>Wire shape: capability name → mode name, both as strings.</summary>
    public IReadOnlyDictionary<string, string> ToDictionary()
        => _modes.ToDictionary(e => e.Key.ToString(), e => e.Value.ToString());
}
