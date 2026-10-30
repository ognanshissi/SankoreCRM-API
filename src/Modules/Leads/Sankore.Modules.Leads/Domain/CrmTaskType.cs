namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// The SYSTEM task types. A task's type is the code of a <see cref="TaskTypeConfig"/> of its
/// tenant: these seven are seeded for every tenant, cannot be deactivated, and are the only ones
/// the platform itself relies on — the automatic tasks, the closing rules, the next-step
/// suggestions. A tenant adds its own types beside them from the settings screen.
///
/// <para>
/// It was an enum until 2026-10-30, while the settings screen wrote types nothing read. The codes
/// are the enum's former names, which the <c>type</c> columns already held as strings — so no
/// task or rule had to be rewritten.
/// </para>
/// </summary>
public static class CrmTaskType
{
    public const string FirstContact   = "FirstContact";
    public const string Qualification  = "Qualification";
    public const string SlaFollowUp    = "SlaFollowUp";
    public const string ScoreReview    = "ScoreReview";
    public const string OwnerHandover  = "OwnerHandover";
    public const string ManualDispatch = "ManualDispatch";
    public const string Generic        = "Generic";

    /// <summary>The width of the <c>type</c> columns and of <see cref="TaskTypeConfig.Code"/>.</summary>
    public const int CodeMaxLength = 30;

    /// <summary>What a tenant starts with — the labels the screens used to hard-code.</summary>
    public static readonly IReadOnlyList<SystemTaskType> System =
    [
        new(FirstContact,   "Premier contact", "Premier appel ou rendez-vous avec un lead qui vient d'être attribué.", 10),
        new(Qualification,  "Qualification",   "Qualifier le besoin et la situation du lead.", 20),
        new(SlaFollowUp,    "Suivi SLA",       "Relance générée lorsqu'un délai de traitement est dépassé.", 30),
        new(ScoreReview,    "Revue score",     "Revoir un lead dont le score a changé.", 40),
        new(OwnerHandover,  "Passation",       "Reprendre un lead d'un collègue.", 50),
        new(ManualDispatch, "Dispatch manuel", "Attribuer à la main un lead qu'aucun agent n'a pu recevoir.", 60),
        new(Generic,        "Générique",       "Tâche libre, décrite par son titre.", 70),
    ];

    public static bool IsSystem(string? code) => code is not null && System.Any(t => t.Code == code);

    /// <summary>The seeded label, for a system type whose row is missing; the code otherwise.</summary>
    public static string DefaultLabel(string code) => System.FirstOrDefault(t => t.Code == code)?.Label ?? code;
}

public sealed record SystemTaskType(string Code, string Label, string Description, int DisplayOrder);
