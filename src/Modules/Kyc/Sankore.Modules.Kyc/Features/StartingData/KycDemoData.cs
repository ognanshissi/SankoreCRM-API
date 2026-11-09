using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval.Decide;
using Sankore.Modules.Kyc.Features.Verification.ManualValidation;
using Sankore.Modules.Kyc.Features.Verification.RunVerification;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Kyc.Features.StartingData;

/// <summary>
/// The demo's KYC files (<c>docs/donnees-demarrage-demo.md</c>, lot 4), taken through the path the
/// back office takes when no biometry service answers: an agent deposits an identity document and a
/// selfie (demonstration documents: a PDF that says so, a silhouette), runs the verification — the
/// file waits « en vérification » — and validates by hand; then OTHER people sign the rungs, the
/// four-eyes rule being the module's own. Three files end validated, two wait for their branch
/// manager, one is refused; the other clients' files stay « en collecte ».
///
/// <para>
/// Every act is done as the person who would do it (<see cref="BackgroundJobContext"/>), each command
/// in a scope of its own. A file no longer « en collecte » is left alone, so a second load changes
/// nothing. No core banking is connected in a demo: a validation queues no customer creation.
/// </para>
/// </summary>
internal sealed class KycDemoData : ITenantStartingData
{
    public int Order => 70;
    public string Module => "Dossiers KYC de démo";

    internal enum Target { Validated, AwaitingManager, Refused }

    /// <summary>Client key (from the clients' step), its branch, where its file ends.</summary>
    internal static readonly IReadOnlyList<(string Client, string Branch, Target Target)> Files =
    [
        ("awa-kone", "plateau", Target.Validated),
        ("kouame-nguessan", "plateau", Target.Validated),
        ("fanta-camara", "yopougon", Target.Validated),
        ("bakary-diarra", "plateau", Target.AwaitingManager),
        ("ibrahima-ouedraogo", "yopougon", Target.AwaitingManager),
        ("grace-akissi", "yopougon", Target.Refused),
    ];

    public async Task<IReadOnlyDictionary<string, int>> LoadAsync(StartingDataRequest request, IServiceProvider services, CancellationToken ct)
    {
        var created = new Dictionary<string, int>();
        if (request.Kind != StartingDataKind.Demo) return created;

        var db = services.GetRequiredService<KycDbContext>();
        var sender = services.GetRequiredService<ISender>();
        var store = services.GetRequiredService<IKycDocumentStore>();
        var clock = services.GetRequiredService<TimeProvider>();
        Guid Account(string profile) => request.Shared[DemoKeys.Account(profile)];

        int validated = 0, waiting = 0, refused = 0;
        foreach (var (clientKey, branch, target) in Files)
        {
            if (!request.Shared.TryGetValue(DemoKeys.Client(clientKey), out var clientId)) continue;
            // Opened by the client's creation, through the outbox: a file not there yet is skipped.
            var file = await db.KycFiles.AsNoTracking()
                .Where(f => f.CustomerId == clientId)
                .OrderByDescending(f => f.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (file is null || file.Status != KycFileStatus.Collecting) continue;

            var agent = Account($"agent-{branch}-1");
            var colleague = Account($"agent-{branch}-2");
            var manager = Account($"chef-agence-{branch}");

            // The documents, as the upload stores them.
            var identity = await StoreAsync(db, store, request.TenantId, file.Id, KycDocumentKind.IdentityDocumentFront,
                DemoPdf.Page("Carte nationale d'identité - démonstration",
                [
                    "DOCUMENT DE DÉMONSTRATION - aucune valeur d'identité",
                    "",
                    "Pièce fictive déposée par les données de démonstration du CRM.",
                ]), "application/pdf", agent, clock, ct);
            var selfie = await StoreAsync(db, store, request.TenantId, file.Id, KycDocumentKind.Selfie,
                DemoSilhouette.Png(), "image/png", agent, clock, ct);

            // Verification: with no biometry service the file waits « en vérification ».
            await As(request, agent, () => sender.Send(new RunKycVerificationCommand(
                request.TenantId, file.Id, identity, selfie, agent, KycDocumentType.Cni), ct));
            var status = await StatusAsync(db, file.Id, ct);
            if (status is not (KycFileStatus.Verifying or KycFileStatus.ComplementRequired)) continue;

            var manual = await As(request, agent, () => sender.Send(new ManuallyValidateKycFileCommand(
                file.Id, "Pièces vérifiées au guichet (données de démonstration)."), ct));
            if (manual.IsFailure) throw new InvalidOperationException($"{clientKey} : {manual.Error}");
            var levels = manual.Value!.ApprovalLevels
                .Select(l => Enum.Parse<KycApprovalLevel>(l, ignoreCase: true))
                .Order()
                .ToList();

            // The rungs, each signed by its own person — never the agent who validated.
            Guid Signer(KycApprovalLevel level) => level switch
            {
                KycApprovalLevel.Agent => colleague,
                KycApprovalLevel.BranchManager => manager,
                KycApprovalLevel.ComplianceOfficer => Account("conformite"),
                _ => Account("administrateur"),
            };
            foreach (var level in levels)
            {
                if (target == Target.AwaitingManager && level >= KycApprovalLevel.BranchManager) break;
                var refusing = target == Target.Refused && level == levels[^1];
                var decided = await As(request, Signer(level), () => sender.Send(new DecideKycApprovalCommand(
                    file.Id, level,
                    refusing ? KycApprovalDecision.Rejected : KycApprovalDecision.Approved,
                    refusing ? "Pièce d'identité illisible : nouvelle pièce demandée au client." : "Dossier conforme."), ct));
                if (decided.IsFailure) throw new InvalidOperationException($"{clientKey}, {level} : {decided.Error}");
                if (refusing) break;
            }

            switch (target)
            {
                case Target.Validated: validated++; break;
                case Target.AwaitingManager: waiting++; break;
                case Target.Refused: refused++; break;
            }
        }
        await HandToAdvisorsAsync(request, services, db, ct);

        if (validated > 0) created["KYC validés"] = validated;
        if (waiting > 0) created["KYC en validation"] = waiting;
        if (refused > 0) created["KYC refusés"] = refused;
        return created;
    }

    /// <summary>
    /// Every demo file to its client's advisor. A file is its opener's (the client's creator, here the
    /// system account), and an agent sees only the files they are responsible for: without this no
    /// agent of the demo saw a single file of their clients (recette of lot 5). At the counter the
    /// agent who creates the client opens the file and is its responsible — this gives the demo that
    /// state. A direct write, demo rows only, and only where the system account still holds the file.
    /// </summary>
    private static async Task HandToAdvisorsAsync(StartingDataRequest request, IServiceProvider services, KycDbContext db, CancellationToken ct)
    {
        var clientIds = request.Shared.Where(kv => kv.Key.StartsWith(DemoKeys.ClientPrefix, StringComparison.Ordinal))
            .Select(kv => kv.Value).Distinct().ToList();
        if (clientIds.Count == 0) return;

        var summaries = await services.GetRequiredService<ICustomersModule>().GetClientSummariesAsync(request.TenantId, clientIds, ct);
        foreach (var (clientId, summary) in summaries)
        {
            if (summary.AdvisorUserId is not { } advisor) continue;
            await db.KycFiles
                .Where(f => f.CustomerId == clientId && f.ResponsibleAgentId == request.SystemUserId)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.ResponsibleAgentId, (Guid?)advisor), ct);
        }
    }

    /// <summary>What the upload endpoint does for one image: the encrypted store, then the row.</summary>
    private static async Task<string> StoreAsync(
        KycDbContext db, IKycDocumentStore store, Guid tenantId, Guid fileId, KycDocumentKind kind,
        byte[] content, string contentType, Guid uploadedBy, TimeProvider clock, CancellationToken ct)
    {
        await using var stream = new MemoryStream(content);
        var stored = await store.StoreAsync(tenantId, fileId, kind, stream, contentType, ct);
        db.KycDocuments.Add(KycDocument.Register(
            tenantId, fileId, kind, stored.StorageRef, stored.ContentType, stored.SizeBytes, stored.Sha256,
            uploadedBy, clock, declaredDocumentType: kind == KycDocumentKind.IdentityDocumentFront ? KycDocumentType.Cni : null));
        await db.SaveChangesAsync(ct);
        return stored.StorageRef;
    }

    private static Task<KycFileStatus> StatusAsync(KycDbContext db, Guid fileId, CancellationToken ct)
        => db.KycFiles.AsNoTracking().Where(f => f.Id == fileId).Select(f => f.Status).FirstAsync(ct);

    private static async Task<T> As<T>(StartingDataRequest request, Guid userId, Func<Task<T>> work)
    {
        using (BackgroundJobContext.SetScope(request.TenantId, userId, "Démo"))
            return await work();
    }
}
