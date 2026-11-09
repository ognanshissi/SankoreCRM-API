using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;
using Sankore.Modules.Administration.Features.Agencies.CreateAgency;
using Sankore.Modules.Administration.Features.Territories.CreateTerritory;
using Sankore.Modules.Administration.Features.Users.AssignManager;
using Sankore.Modules.Administration.Features.Users.DemoUsers;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.StartingData;

/// <summary>
/// The demo accounts' settings (owner, 2026-11-08): their shared password — <c>DemoData:Password</c>,
/// never in the code — and their address, <c>DemoData:EmailTemplate</c>: a real mailbox with one
/// sub-address per profile AND per institution, so the demo's e-mails (manager appointments, alerts,
/// password resets) actually arrive, and several demos can live on one platform — a sign-in name is
/// unique across every tenant.
/// </summary>
public sealed class DemoDataOptions
{
    public const string Section = "DemoData";
    public const string ProfilePlaceholder = "{profil}";
    public const string InstitutionPlaceholder = "{institution}";
    public const string DefaultEmailTemplate = "contact+" + ProfilePlaceholder + "." + InstitutionPlaceholder + "@talisoft.net";

    public string Password { get; set; } = string.Empty;
    public string EmailTemplate { get; set; } = DefaultEmailTemplate;

    private string Template => string.IsNullOrWhiteSpace(EmailTemplate) ? DefaultEmailTemplate : EmailTemplate.Trim();

    public string EmailFor(string profile, string institution)
        => Template.Replace(ProfilePlaceholder, profile, StringComparison.Ordinal)
            .Replace(InstitutionPlaceholder, institution, StringComparison.Ordinal);

    /// <summary>« Démo Caisse d'Abidjan » → « demo-caisse-d-abidjan », 30 characters at most.</summary>
    public static string InstitutionSlug(string name)
    {
        var ascii = new string(name.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => c < 128 && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant();
        var slug = System.Text.RegularExpressions.Regex.Replace(ascii, "[^a-z0-9]+", "-").Trim('-');
        slug = slug.Length > 30 ? slug[..30].TrimEnd('-') : slug;
        return slug.Length == 0 ? "demo" : slug;
    }

    /// <summary>Whether <paramref name="email"/> is one the template produces — the only ones a demo account may have.</summary>
    public bool IsDemoAddress(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !Template.Contains(ProfilePlaceholder, StringComparison.Ordinal)) return false;
        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(Template)
            .Replace(System.Text.RegularExpressions.Regex.Escape(ProfilePlaceholder), "[a-z0-9-]+")
            .Replace(System.Text.RegularExpressions.Regex.Escape(InstitutionPlaceholder), "[a-z0-9-]+") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(email.Trim(), pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

/// <summary>
/// The demo's share of Administration (<c>docs/donnees-demarrage-demo.md</c>, lot 2): two branches
/// under the head office, one account per profile at <see cref="DemoDataOptions.EmailTemplate"/> with
/// the shared password, branch managers and reporting lines, two territories. Runs after the starter
/// (Order 30), for a <see cref="StartingDataKind.Demo"/> load only; idempotent on the agency name,
/// the e-mail and the territory code, and it only fills a reporting line or a manager left empty.
/// </summary>
internal sealed class AdministrationDemoData(IOptions<DemoDataOptions> options) : ITenantStartingData
{
    public const string Plateau = "Agence Plateau";
    public const string Yopougon = "Agence Yopougon";

    public int Order => 30;
    public string Module => "Comptes de démo";

    /// <param name="Profile">The address's sub-part: <c>contact+{Profile}.{institution}@talisoft.net</c>.</param>
    /// <param name="Agency">Null: the head office.</param>
    /// <param name="ReportsTo">Their manager's <paramref name="Profile"/>; null at the top.</param>
    internal sealed record DemoAccount(string Profile, string FirstName, string LastName, string Role, string? Agency, string? ReportsTo);

    internal static readonly IReadOnlyList<DemoAccount> Accounts =
    [
        new("administrateur", "Aminata", "Touré", global::Sankore.Shared.Kernel.Roles.Administrator.Code, null, null),
        new("responsable-commercial", "Serge", "Kouadio", global::Sankore.Shared.Kernel.Roles.SalesManager.Code, null, "administrateur"),
        new("analyste-credit", "Salimata", "Sangaré", global::Sankore.Shared.Kernel.Roles.CreditAnalyst.Code, null, "administrateur"),
        new("conformite", "Didier", "Assi", global::Sankore.Shared.Kernel.Roles.RegulationManager.Code, null, "administrateur"),
        new("chef-agence-plateau", "Mariam", "Bamba", global::Sankore.Shared.Kernel.Roles.BranchManager.Code, Plateau, "responsable-commercial"),
        new("chef-agence-yopougon", "Ibrahim", "Coulibaly", global::Sankore.Shared.Kernel.Roles.BranchManager.Code, Yopougon, "responsable-commercial"),
        new("agent-plateau-1", "Fatou", "Diallo", global::Sankore.Shared.Kernel.Roles.Agent.Code, Plateau, "chef-agence-plateau"),
        new("agent-plateau-2", "Jean-Marc", "Yao", global::Sankore.Shared.Kernel.Roles.Agent.Code, Plateau, "chef-agence-plateau"),
        new("agent-commercial-plateau", "Christelle", "Aka", global::Sankore.Shared.Kernel.Roles.CommercialAgent.Code, Plateau, "chef-agence-plateau"),
        new("gestionnaire-comptes", "Nadia", "Koné", global::Sankore.Shared.Kernel.Roles.AccountManager.Code, Plateau, "chef-agence-plateau"),
        new("gestionnaire-credit", "Patrick", "Ehui", global::Sankore.Shared.Kernel.Roles.CreditManager.Code, Plateau, "chef-agence-plateau"),
        new("caissier", "Rokia", "Traoré", global::Sankore.Shared.Kernel.Roles.Cashier.Code, Plateau, "chef-agence-plateau"),
        new("agent-yopougon-1", "Awa", "Ouattara", global::Sankore.Shared.Kernel.Roles.Agent.Code, Yopougon, "chef-agence-yopougon"),
        new("agent-yopougon-2", "Moussa", "Konaté", global::Sankore.Shared.Kernel.Roles.Agent.Code, Yopougon, "chef-agence-yopougon"),
        new("agent-commercial-yopougon", "Yannick", "Brou", global::Sankore.Shared.Kernel.Roles.CommercialAgent.Code, Yopougon, "chef-agence-yopougon"),
    ];

    /// <summary>Branch → the profile that runs it.</summary>
    internal static readonly IReadOnlyDictionary<string, string> BranchManagers = new Dictionary<string, string>
    {
        [Plateau] = "chef-agence-plateau",
        [Yopougon] = "chef-agence-yopougon",
    };

    public async Task<IReadOnlyDictionary<string, int>> LoadAsync(StartingDataRequest request, IServiceProvider services, CancellationToken ct)
    {
        var created = new Dictionary<string, int>();
        if (request.Kind != StartingDataKind.Demo) return created;

        var password = options.Value.Password;
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "Mot de passe des comptes de démo absent : définir DemoData:Password dans la configuration du serveur.");

        var db = services.GetRequiredService<AdministrationDbContext>();
        var sender = services.GetRequiredService<ISender>();

        // The branches, under the head office the starter created (or the institution's own).
        var agencies = await db.Agencies.ToListAsync(ct);
        var headOffice = agencies.FirstOrDefault(a => a.AgencyType == AgencyType.HeadQuarter && a.ParentAgencyId == null)
            ?? throw new InvalidOperationException("Aucun siège : le démarrage doit créer les agences avant la démo.");
        var agencyIds = agencies.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var newAgencies = 0;
        foreach (var (name, district) in new[] { (Plateau, "Plateau"), (Yopougon, "Yopougon") })
        {
            if (agencyIds.ContainsKey(name)) continue;
            var result = Ok(await sender.Send(new CreateAgencyCommand(name, $"Agence de démonstration — {district}.", AgencyType.Branch,
                headOffice.Id, district, "Abidjan", null, "Côte d'Ivoire", null, null, null), ct), name);
            agencyIds[name] = result.AgencyId;
            newAgencies++;
        }
        if (newAgencies > 0) created["Agences"] = newAgencies;

        // The accounts, found again by their address — those of a paid module only where it is on
        // (docs/isolation-socle.md, lot 2): a socle demo has no credit analyst nor cashier.
        var enabled = await services.GetRequiredService<ITenantModules>().EnabledSetAsync(request.TenantId, ct);
        var accounts = Accounts.Where(a => ModuleScopes.InReach(ModuleScopes.OfRole(a.Role), enabled)).ToList();
        var settings = options.Value;
        var institution = DemoDataOptions.InstitutionSlug(request.TenantName);
        var emails = accounts.ToDictionary(a => a.Profile, a => settings.EmailFor(a.Profile, institution));
        var normalized = emails.Values.Select(e => e.ToUpperInvariant()).ToList();
        var existing = await db.Users.Where(u => u.NormalizedEmail != null && normalized.Contains(u.NormalizedEmail))
            .ToDictionaryAsync(u => u.NormalizedEmail!, u => u.Id, ct);
        var ids = new Dictionary<string, Guid>();
        var newAccounts = 0;
        foreach (var account in accounts)
        {
            var email = emails[account.Profile];
            if (existing.TryGetValue(email.ToUpperInvariant(), out var id))
            {
                ids[account.Profile] = id;
                continue;
            }
            var agencyId = account.Agency is null ? headOffice.Id : agencyIds[account.Agency];
            ids[account.Profile] = Ok(await sender.Send(new ProvisionDemoUserCommand(
                agencyId, account.Role, account.FirstName, account.LastName, email, password, request.SystemUserId), ct), email);
            newAccounts++;
        }
        if (newAccounts > 0) created["Comptes de démo"] = newAccounts;

        // For the demo's later steps: where each branch is, who each profile is.
        foreach (var (name, id) in agencyIds) request.Shared[DemoKeys.Agency(name)] = id;
        foreach (var (profile, id) in ids) request.Shared[DemoKeys.Account(profile)] = id;

        // Who runs each branch, then who reports to whom — only where nobody was set. What exists
        // is read back; what this load created has nobody yet.
        var agencyIdList = BranchManagers.Keys.Select(b => agencyIds[b]).ToList();
        var runBy = await db.Agencies.Where(a => agencyIdList.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.ManagerUserId, ct);
        foreach (var (branch, manager) in BranchManagers)
        {
            var agencyId = agencyIds[branch];
            if (runBy.GetValueOrDefault(agencyId) is null)
                Ok(await sender.Send(new AssignAgencyManagerCommand(agencyId, ids[manager]), ct), branch);
        }
        var accountIds = ids.Values.ToList();
        var reportsTo = await db.Users.Where(u => accountIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.ReportsToUserId, ct);
        foreach (var account in accounts.Where(a => a.ReportsTo is not null && ids.ContainsKey(a.ReportsTo)))
        {
            var id = ids[account.Profile];
            if (reportsTo.GetValueOrDefault(id) is null)
                Ok(await sender.Send(new AssignManagerCommand(id, ids[account.ReportsTo!]), ct), emails[account.Profile]);
        }

        // Two territories around the branches.
        var codes = (await db.Territories.Select(t => t.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var territories = 0;
        foreach (var (code, name, lat, lng) in new[] { ("ABJ-SUD", "Abidjan Sud", 5.3236, -4.0197), ("ABJ-OUEST", "Abidjan Ouest", 5.3364, -4.0890) })
        {
            if (codes.Contains(code)) continue;
            Ok(await sender.Send(new CreateTerritoryCommand(name, code, "Territoire de démonstration.", lat, lng, 8, []), ct), code);
            territories++;
        }
        if (territories > 0) created["Territoires"] = territories;

        return created;
    }

    private static T Ok<T>(Result<T> result, string what)
        => result.IsSuccess ? result.Value! : throw new InvalidOperationException($"{what} : {result.Error}");
}
