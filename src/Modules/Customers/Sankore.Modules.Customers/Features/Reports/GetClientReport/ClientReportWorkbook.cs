namespace Sankore.Modules.Customers.Features.Reports.GetClientReport;

using ClosedXML.Excel;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// The « Clients » tab as an Excel workbook, from the SAME <see cref="ClientReportDto"/> the screen
/// shows. Numbers stay numbers so the reader can sort and sum; an absent figure is an empty cell.
/// </summary>
internal static class ClientReportWorkbook
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#E3EBEB");

    public static byte[] Build(ClientReportDto r, string? agencyName, DateTimeOffset generatedAt)
    {
        using var wb = new XLWorkbook();

        var ws = wb.Worksheets.Add("Synthèse");
        ws.Cell(1, 1).Value = "Rapport des clients";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
        ws.Cell(3, 1).Value = "Du"; Set(ws.Cell(3, 2), r.From.UtcDateTime, "dd/mm/yyyy");
        ws.Cell(4, 1).Value = "Au"; Set(ws.Cell(4, 2), r.To.AddTicks(-1).UtcDateTime, "dd/mm/yyyy");
        ws.Cell(5, 1).Value = "Agence"; ws.Cell(5, 2).Value = agencyName ?? "Toutes les agences";
        ws.Cell(6, 1).Value = "Généré le"; Set(ws.Cell(6, 2), generatedAt.UtcDateTime, "dd/mm/yyyy hh:mm");
        ws.Range(3, 1, 6, 1).Style.Font.SetBold();

        var row = 8;
        Header(ws, row, ["Indicateur", "Période", "Période précédente", "Définition"]);
        var k = r.Kpis;
        var p = r.Previous;
        var lines = new (string, object?, object?, string)[]
        {
            ("Nouveaux clients", k.Period.NewClients, p.NewClients, "Clients créés pendant la période."),
            ("Réclamations reçues", k.Period.ComplaintsReceived, p.ComplaintsReceived, "Réclamations enregistrées pendant la période."),
            ("Réclamations traitées", k.Period.ComplaintsResolved, p.ComplaintsResolved, "Réclamations répondues pendant la période."),
            ("Délai de traitement (jours)", k.Period.AvgResolutionDays, p.AvgResolutionDays, "De la réception à la réponse, pour celles répondues pendant la période."),
            ("Suspendus ou archivés", k.Period.SuspendedOrArchived, p.SuspendedOrArchived, "Clients passés au statut Suspendu ou Archivé pendant la période."),
            ("Clients actifs", k.Active, null, "Statut Actif, à l'instant de l'export."),
            ("Sans conseiller", k.WithoutAdvisor, null, "Actifs ou en attente de KYC, sans conseiller."),
            ("Produits par client", k.AvgProductsPerClient, null, "Contrats actifs ÷ clients actifs."),
            ("Risque élevé", k.HighRisk, null, "Actifs ou en attente de KYC, risque élevé."),
            ("PPE", k.Pep, null, "Actifs ou en attente de KYC, exposés politiquement."),
            ("Réclamations ouvertes", k.OpenComplaints, null, "Réclamations sans réponse à l'instant de l'export."),
            ("Réclamations en retard", k.OverdueComplaints, null, "Ouvertes et passé leur échéance de réponse."),
        };
        foreach (var (label, value, previous, definition) in lines)
        {
            row++;
            ws.Cell(row, 1).Value = label;
            Set(ws.Cell(row, 2), value);
            Set(ws.Cell(row, 3), previous);
            ws.Cell(row, 4).Value = definition;
        }
        ws.Column(1).Width = 28;
        ws.Column(2).Width = 12;
        ws.Column(3).Width = 20;
        ws.Column(4).Width = 80;

        Table(wb, "Types", ["Type", "Clients (actifs ou en attente)", "Nouveaux sur la période"],
            r.ByType.Select(t => new object?[] { t.Type == ClientType.Legal ? "Personne morale" : "Personne physique", t.Base, t.NewClients }));
        Table(wb, "Segments", ["Segment", "Clients"],
            r.BySegment.Select(s => new object?[] { s.SegmentCode ?? "Non segmenté", s.Count }));
        Table(wb, "Agences", ["Agence", "Nouveaux sur la période", "Clients actifs"],
            r.ByAgency.Select(a => new object?[] { a.Name, a.NewClients, a.Active }));

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Table(XLWorkbook wb, string name, string[] headers, IEnumerable<object?[]> rows)
    {
        var ws = wb.Worksheets.Add(name);
        Header(ws, 1, headers);
        var r = 1;
        foreach (var values in rows)
        {
            r++;
            for (var c = 0; c < values.Length; c++) Set(ws.Cell(r, c + 1), values[c]);
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, headers.Length).AdjustToContents();
    }

    private static void Header(IXLWorksheet ws, int row, string[] headers)
    {
        for (var c = 0; c < headers.Length; c++)
        {
            ws.Cell(row, c + 1).Value = headers[c];
            ws.Cell(row, c + 1).Style.Font.SetBold();
            ws.Cell(row, c + 1).Style.Fill.SetBackgroundColor(HeaderFill);
        }
    }

    private static void Set(IXLCell cell, object? value, string? format = null)
    {
        switch (value)
        {
            case null: cell.Clear(XLClearOptions.Contents); break;
            case int i: cell.Value = i; break;
            case double d: cell.Value = d; break;
            case DateTime dt: cell.Value = dt; break;
            default: cell.Value = value.ToString(); break;
        }
        if (format is not null) cell.Style.NumberFormat.Format = format;
    }
}
