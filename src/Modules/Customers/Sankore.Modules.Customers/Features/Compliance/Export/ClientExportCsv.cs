namespace Sankore.Modules.Customers.Features.Compliance.Export;

using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

/// <summary>
/// One exported line. Every field is already a <see cref="string"/>: the export is a contract
/// with whatever spreadsheet or BI tool consumes it, so the rendering of a date, an enum or a
/// masked value is decided here once and cannot drift with a host culture.
/// <para>
/// <see cref="PrimaryPhoneMasked"/> and <see cref="IdentityDocumentMasked"/> are the ONLY
/// protected fields present, and they are masked — a CSV has no reveal audit, no rate limit and
/// no expiry once it has left the building, so a clear identity document number must never reach
/// it whatever the caller's permissions.
/// </para>
/// </summary>
internal sealed record ClientExportRow(
    string ClientNumber,
    string DisplayName,
    string ClientType,
    string Status,
    string AgencyId,
    string AdvisorUserId,
    string KycStatus,
    string RiskLevel,
    string SegmentCode,
    string PrimaryPhoneMasked,
    string IdentityDocumentMasked,
    string CreatedAt);

/// <summary>
/// Column names and order of the export, fixed by the US: any change here breaks every
/// downstream consumer, so the map is explicit rather than derived from the property names.
/// </summary>
internal sealed class ClientExportRowMap : ClassMap<ClientExportRow>
{
    public ClientExportRowMap()
    {
        Map(r => r.ClientNumber).Index(0).Name("client_number");
        Map(r => r.DisplayName).Index(1).Name("display_name");
        Map(r => r.ClientType).Index(2).Name("client_type");
        Map(r => r.Status).Index(3).Name("status");
        Map(r => r.AgencyId).Index(4).Name("agency_id");
        Map(r => r.AdvisorUserId).Index(5).Name("advisor_user_id");
        Map(r => r.KycStatus).Index(6).Name("kyc_status");
        Map(r => r.RiskLevel).Index(7).Name("risk_level");
        Map(r => r.SegmentCode).Index(8).Name("segment_code");
        Map(r => r.PrimaryPhoneMasked).Index(9).Name("primary_phone_masked");
        Map(r => r.IdentityDocumentMasked).Index(10).Name("identity_document_masked");
        Map(r => r.CreatedAt).Index(11).Name("created_at");
    }
}

internal static class ClientExportCsv
{
    /// <summary>Header line, in order. Exposed so a test can assert the contract directly.</summary>
    internal static readonly string[] Headers =
    [
        "client_number", "display_name", "client_type", "status", "agency_id", "advisor_user_id",
        "kyc_status", "risk_level", "segment_code", "primary_phone_masked",
        "identity_document_masked", "created_at"
    ];

    internal const string ContentType = "text/csv; charset=utf-8";

    internal static string FileName(Guid exportId) => $"clients-export-{exportId:N}.csv";

    /// <summary>
    /// Renders the rows as UTF-8 CSV, BOM included: without it Excel misreads the accented
    /// display names of a West-African client base.
    /// </summary>
    internal static async Task<byte[]> WriteAsync(
        IReadOnlyCollection<ClientExportRow> rows, CancellationToken ct)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };

        using var buffer = new MemoryStream();

        // leaveOpen: the writer must flush and dispose while the MemoryStream stays readable.
        await using (var textWriter = new StreamWriter(
                         buffer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true))
        await using (var csv = new CsvWriter(textWriter, config))
        {
            csv.Context.RegisterClassMap<ClientExportRowMap>();
            await csv.WriteRecordsAsync(rows, ct);
        }

        return buffer.ToArray();
    }
}
