namespace Sankore.Api.Infrastructure;

using Sankore.Modules.Integration.Adapters.Amplitude;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Adapters.Orass;
using Sankore.Modules.Integration.Adapters.PerfectVision;
using Sankore.Modules.Integration.Adapters.Ringover;
using Sankore.Modules.Integration.Adapters.Dialog360;
using Sankore.Modules.Integration.Adapters.Sab;
using Sankore.Modules.Integration.Adapters.Temenos;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Every core-banking adapter this host ships, registered in one place.
///
/// <para>
/// <b>Why it is a method and not six lines in <c>Program.cs</c>.</b> An adapter reaches a tenant
/// only if two independent things happen: the project is referenced by
/// <c>Sankore.Api.csproj</c>, and its <c>Add…Adapter</c> is called. Forget the second and the
/// assembly ships, its tests pass, and a configured connection answers
/// <see cref="IntegrationErrors.AdapterNotRegistered"/> — "this deployment was built without the
/// assembly", which sends whoever reads it to inspect a build that is in fact correct. That is
/// not hypothetical: Perfect Vision (INT-28) shipped that way, with 45 passing tests and no
/// route from a connection to its own adapter.
/// </para>
///
/// <para>
/// Collecting the calls here gives <c>IntegrationAdapterCompositionTests</c> one thing to point
/// at, so the gap is a failing test instead of a support ticket. The list is still written by
/// hand — a reflection scan over the loaded assemblies would register whatever happened to be
/// copied into the output folder, which is the opposite of a deployment decision.
/// </para>
/// </summary>
internal static class IntegrationAdapterComposition
{
    /// <summary>
    /// Registers every adapter the deployment offers.
    /// </summary>
    /// <param name="environment">
    /// Read only by the in-memory double's gate. Passed in rather than consulted through
    /// <c>IHostEnvironment</c> resolved from the container, because this runs during composition,
    /// before any provider exists.
    /// </param>
    internal static IServiceCollection AddIntegrationAdapters(
        this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(environment);

        // Temenos Transact (INT-12, INT-13). No environment gate: with no configured connection
        // the resolver simply answers NoActiveConnection, so shipping the adapter costs nothing
        // to a tenant that does not run Transact. Every option has a usable default;
        // Integration:Temenos only overrides them.
        services.AddTemenosAdapter(config);

        // Perfect Vision (INT-28), blocked on the vendor's file layout. Registered precisely
        // BECAUSE it refuses every call: the refusal names the missing document and the capability
        // matrix becomes readable by a screen, where the absence of a registration would instead
        // claim the deployment was built wrong. The adapter's own registration doc makes the
        // argument at length; this is the call it was waiting for.
        services.AddPerfectVisionAdapter(config);

        // Amplitude (INT-31), blocked on SBS's interface contract and API access. Same argument as
        // Perfect Vision, with one addition of its own: the refusal names WHICH of two artefacts
        // this installation's release needs — an API catalogue for Up, a file layout and an
        // acknowledgement format for pre-Up — and the capability matrix is computed from
        // AmplitudeSettings.AmplitudeVersion together with the connection's mode, so a screen can
        // read it before anything is unblocked.
        services.AddAmplitudeAdapter(config);

        // SAB AT through Open SAB (INT-32), blocked on the Open SAB API catalogue. Its refusals
        // separate three owners: a missing Entity and a missing API key are the tenant
        // administrator's own, fixable in a minute; the catalogue is a procurement conversation.
        // Needs AddSecretsVault to have run — it probes the vault for the key and registers none
        // of it itself.
        services.AddSabAdapter(config);

        // ORASS®Suite (ASS-06), the insurance family's first adapter, blocked on the ORASS
        // interface specification AND the insurer's agreement — two counterparties, where the three
        // core-banking adapters each wait on one. Its refusals separate five owners: settings of
        // the wrong shape, a missing apporteur code, a mode that contradicts the insurer's
        // coordinates and a missing credential are the tenant administrator's own; only the
        // specification is a procurement conversation. Needs AddSecretsVault to have run — it
        // probes the vault for whichever credential its carrier needs and registers none itself.
        services.AddOrassAdapter(config);

        // Ringover (TEL-B-04), the telephony family's first adapter, blocked on a TEST ACCOUNT
        // rather than on a document — which is the difference from the four above, and the reason
        // it cannot be unblocked by reading anything. Its refusals separate four owners: settings
        // of the wrong shape, a missing API host, a missing API key or webhook token are the
        // tenant administrator's own; only the account is a procurement conversation. Unlike the
        // others it also has one member that genuinely works through this registration —
        // ParseWebhook is pure and produces a real deduplication key — so a webhook arriving at a
        // configured connection is recorded rather than lost. Needs AddSecretsVault to have run:
        // it probes the vault for its two per-connection secrets and registers none itself.
        services.AddRingoverAdapter(config);

        // 360dialog (WA-BE-01), the messaging family's first adapter, and the first adapter of
        // this list that operates fully: it is built against Meta's documented Cloud API, which
        // 360dialog proxies. What remains to observe on a sandbox account is listed in
        // Dialog360Specification.OpenQuestions. Needs AddSecretsVault: it reads the per-number API
        // key and registers none of it.
        services.AddDialog360Adapter(config);

        // The in-memory double (INT-10), Development only. Guarded HERE as well as inside
        // AddFakeAdapter, which throws outside Development: the host decides whether to offer the
        // double at all, and the method refuses if the host got that decision wrong. Belt and
        // braces on purpose — an adapter that answers successful writes with no back-office behind
        // it would leave a tenant whose CBS and whose CRM disagree about every write, with nothing
        // reporting it.
        if (environment.IsDevelopment())
        {
            services.AddFakeAdapter(environment);
        }

        return services;
    }
}
