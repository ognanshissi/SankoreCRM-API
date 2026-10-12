namespace Sankore.Modules.Integration.Features.Insurance.Retention;

/// <summary>
/// How long the insurance evidence of ASS-12 is kept, bound from
/// <c>Integration:Insurance:Retention</c>.
///
/// <para>
/// <b>A host setting and NOT a tenant parameter</b>, the same line M02 draws for
/// <c>Kyc:Storage</c> and the batch socle for its own encryption. The period is a regulatory
/// FLOOR — the CIMA LBC/FT text requires the pieces of a relationship to be kept at least ten
/// years — so a tenant able to edit it is a tenant able to destroy evidence a controller may
/// still demand. The platform operator sets it; nobody inside the institution lowers it.
/// </para>
///
/// <para>
/// <b>Ten years by default, and the default is the point.</b> <c>ConsentProof.RetainUntil</c> and
/// <c>MedicalQuestionnaire.RetainUntil</c> were designed nullable with <c>null</c> meaning
/// <i>no period has been decided</i> — explicitly not "sweepable now". Until this option existed,
/// nothing wrote them, so the purge had nothing it was allowed to touch and the tables grew for
/// ever. Deciding the period is what makes ASS-12 possible at all.
/// </para>
/// </summary>
internal sealed class InsuranceRetentionOptions
{
    public const string SectionName = "Integration:Insurance:Retention";

    /// <summary>
    /// Years an evidence row is kept, counted from the moment it was captured. Ten is the LBC/FT
    /// floor; a deployment subject to a longer national period raises it.
    /// </summary>
    public int EvidenceYears { get; set; } = 10;

    /// <summary>
    /// Rows stamped or purged per run. A ceiling so one night cannot hold a connection open over
    /// a decade of backlog; the next run continues where this one stopped, because the selection
    /// is "whatever is still expired".
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// Off by default. The purge DESTROYS evidence and the destruction cannot be undone, so it is
    /// an operator's explicit decision per environment rather than something a deployment acquires
    /// by upgrading. The stamping half runs either way: recording when a row becomes purgeable is
    /// not a destruction, and a deployment that never turns the purge on still carries honest
    /// retention dates for a controller to read.
    /// </summary>
    public bool PurgeEnabled { get; set; }

    /// <summary>
    /// The period as a sane value. A non-positive or absurd setting falls back to the floor rather
    /// than being honoured: zero years would mean purging evidence the day it is collected, and a
    /// typo in a configuration file is exactly how that would arrive.
    /// </summary>
    internal int ResolvedYears => EvidenceYears is > 0 and <= 100 ? EvidenceYears : 10;

    internal int ResolvedBatchSize => BatchSize is > 0 and <= 10_000 ? BatchSize : 500;
}
