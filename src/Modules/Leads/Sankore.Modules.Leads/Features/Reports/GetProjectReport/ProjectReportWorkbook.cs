using ClosedXML.Excel;
using Sankore.Modules.Leads.Features.Reports.GetLeadReport;

namespace Sankore.Modules.Leads.Features.Reports.GetProjectReport;

/// <summary>
/// The « Projets » tab as an Excel workbook, from the SAME <see cref="ProjectReportDto"/> the
/// screen shows, with the lead workbook's conventions (numbers as numbers, rates as fractions).
/// </summary>
internal static class ProjectReportWorkbook
{
    public static byte[] Build(ProjectReportDto r, string? agencyName, DateTimeOffset generatedAt)
    {
        using var wb = new XLWorkbook();
        Summary(wb, r, agencyName, generatedAt);

        LeadReportWorkbook.Table(wb, "Pipeline", ["Nature du montant", "Projets ouverts", "Montant estimé", "Montant pondéré"],
            r.Pipeline.Select(p => new object?[] { LeadReportWorkbook.AmountKind(p.Kind), p.Count, p.Amount, p.Weighted }));

        LeadReportWorkbook.Table(wb, "Produits", ["Produit", "Ouverts", "Gagnés", "Perdus", "Taux de gain", "Montant estimé"],
            r.ByProduct.Select(p => new object?[] { p.Product, p.Opened, p.Won, p.Lost, p.WinRate / 100, p.Amount }),
            percentColumns: [5]);

        LeadReportWorkbook.Table(wb, "Motifs de perte", ["Motif", "Projets perdus"],
            r.LossReasons.Select(l => new object?[] { l.Reason, l.Count }));

        LeadReportWorkbook.Table(wb, "Étapes",
            ["Parcours", "Étape", "Sorties sur la période", "Durée moyenne (jours)", "En attente aujourd'hui", "Attente moyenne (jours)"],
            r.Steps.Select(s => new object?[] { s.Template, s.Step, s.Left, s.AvgDays, s.WaitingNow, s.AvgWaitingDays }));

        LeadReportWorkbook.Table(wb, "En retard",
            ["Projet", "Produit", "Phase", "Étape", "Responsable", "Clôture prévue", "Jours de retard", "Montant estimé"],
            r.Overdue.Select(o => new object?[]
            {
                o.Title, o.Product, LeadReportWorkbook.Phase(o.Phase), o.Step, o.OwnerName,
                o.ExpectedCloseDate.UtcDateTime, o.DaysLate, o.Amount,
            }),
            dateColumns: [6]);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Summary(XLWorkbook wb, ProjectReportDto r, string? agencyName, DateTimeOffset generatedAt)
    {
        var ws = wb.Worksheets.Add("Synthèse");
        ws.Cell(1, 1).Value = "Rapport des projets";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);

        var head = new (string, object?, string?)[]
        {
            ("Du", r.From.UtcDateTime, "dd/mm/yyyy"),
            ("Au", r.To.AddTicks(-1).UtcDateTime, "dd/mm/yyyy"),
            ("Agence", agencyName ?? "Toutes les agences", null),
            ("Généré le", generatedAt.UtcDateTime, "dd/mm/yyyy hh:mm"),
        };
        var row = 3;
        foreach (var (label, value, format) in head)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 1).Style.Font.SetBold();
            LeadReportWorkbook.SetValue(ws.Cell(row, 2), value);
            if (format is not null) ws.Cell(row, 2).Style.NumberFormat.Format = format;
            row++;
        }

        row++;
        LeadReportWorkbook.Header(ws, row, ["Indicateur", "Période", "Période précédente", "Définition"]);
        var k = r.Kpis.Period;
        var p = r.Previous;
        var lines = new (string, object?, object?, string?, string)[]
        {
            ("Projets ouverts", k.Opened, p.Opened, null, "Projets créés pendant la période."),
            ("Projets gagnés", k.Won, p.Won, null, "Projets gagnés pendant la période, quelle que soit leur date d'ouverture."),
            ("Projets perdus", k.Lost, p.Lost, null, "Projets perdus pendant la période."),
            ("Taux de gain", k.WinRate / 100, p.WinRate / 100, "0.0%", "Gagnés ÷ (gagnés + perdus) pendant la période."),
            ("Cycle moyen (jours)", k.AvgCycleDays, p.AvgCycleDays, "0.0", "De l'ouverture au gain, pour les projets gagnés pendant la période."),
            ("Projets en cours", r.Kpis.OpenProjects, null, null, "Projets non clos à l'instant de l'export."),
            ("Clôtures dépassées", r.Kpis.Overdue, null, null, "Projets en cours dont la date de clôture prévue est passée."),
        };
        foreach (var (label, value, previous, format, definition) in lines)
        {
            row++;
            ws.Cell(row, 1).Value = label;
            LeadReportWorkbook.SetValue(ws.Cell(row, 2), value);
            LeadReportWorkbook.SetValue(ws.Cell(row, 3), previous);
            if (format is not null)
            {
                ws.Cell(row, 2).Style.NumberFormat.Format = format;
                ws.Cell(row, 3).Style.NumberFormat.Format = format;
            }
            ws.Cell(row, 4).Value = definition;
        }

        ws.Column(1).Width = 26;
        ws.Column(2).Width = 14;
        ws.Column(3).Width = 20;
        ws.Column(4).Width = 80;
    }
}
