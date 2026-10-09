namespace Sankore.Integration.RelayAgent.Observability;

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

/// <summary>
/// Sends the agent's log to the Windows Event Log when it runs as a Windows service.
///
/// <para>
/// Not a nicety: a Windows service's standard output goes nowhere. Whoever installed the service
/// will look in Event Viewer, and an agent whose only diagnostics were on a console nobody sees
/// is an agent that cannot be supported by telephone — which is how an IMF's IT team will be
/// supported.
/// </para>
///
/// <para>
/// A class of its own, attributed Windows-only, rather than three lines in <c>Program.cs</c>. The
/// platform analyser carries an enclosing <see cref="SupportedOSPlatformAttribute"/> into a
/// method but not into a lambda or a top-level local function, so the guard in <c>Program.cs</c>
/// alone leaves a warning on the setter. Stating the platform once, here, is the honest form of
/// that — a <c>NoWarn</c> would have said the same thing less visibly.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsEventLogSetup
{
    /// <summary>The source Event Viewer groups the agent's entries under.</summary>
    private const string SourceName = "Sankore Relay Agent";

    public static void Add(ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.AddEventLog(Configure);
    }

    private static void Configure(EventLogSettings settings) => settings.SourceName = SourceName;
}
