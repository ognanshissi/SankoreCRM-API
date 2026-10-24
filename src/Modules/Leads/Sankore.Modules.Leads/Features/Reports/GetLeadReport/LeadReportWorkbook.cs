using ClosedXML.Excel;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Features.Reports.GetLeadReport;

/// <summary>
/// The administrators' report as an Excel workbook — one sheet per block of the screen, built from
/// the SAME <see cref="LeadReportDto"/> the screen shows, so an export can never disagree with it.
///
/// <para>
/// Numbers are written as numbers and dates as dates, never as preformatted text: the point of an
/// Excel export is that the reader can sort, sum and chart it. Rates are stored as fractions with a
/// percent format (0.333 shown as 33.3 %), which is what Excel's own formulas expect.
/// </para>
/// </summary>
internal static class LeadReportWorkbook
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#E3EBEB");

    public static byte[] Build(LeadReportDto r, string? agencyName, DateTimeOffset generatedAt)
    {
        using var wb = new XLWorkbook();

        Summary(wb, r, agencyName, generatedAt);

        Table(wb, "Évolution", ["Période", "Leads captés", "Conversions"],
            r.Series.Select(p => new object?[] { p.PeriodStart.UtcDateTime, p.Captured, p.Converted }),
            dateColumns: [1], dateFormat: r.Granularity == ReportGranularity.Month ? "mmmm yyyy" : "dd/mm/yyyy");

        var captured = Math.Max(1, r.Kpis.Captured);
        Table(wb, "Progression", ["Étape du pipeline", "Leads", "Part des leads captés"],
            r.Progression.Select(s => new object?[] { Stage(s.Stage), s.Count, (double)s.Count / captured }),
            percentColumns: [3]);

        // One amount column per kind of amount: a loan and a deposit are never added together.
        var kinds = (r.Projects ?? []).SelectMany(p => p.Amounts).Select(a => a.Kind).Distinct().Order().ToList();
        Table(wb, "Projets",
            ["Phase actuelle", "Projets ouverts dans la période", .. kinds.Select(AmountKind)],
            (r.Projects ?? []).Select(p => new object?[] { Phase(p.Phase), p.Count }
                .Concat(kinds.Select(k => (object?)p.Amounts.FirstOrDefault(a => a.Kind == k)?.Amount)).ToArray()));

        if (r.Forecast is { } f)
        {
            // One line per kind of amount, never a grand total: a disbursement and a deposit are
            // flows in opposite directions.
            Table(wb, "Prévision",
                ["Nature du montant", "Clôtures prévues", "Montant estimé", "Montant pondéré", "Clôtures dépassées", "Pondéré dépassé"],
                f.Kinds.Select(k => new object?[]
                    { AmountKind(k.Kind), k.Count, k.Amount, k.WeightedAmount, k.Overdue, k.OverdueWeightedAmount }));

            Table(wb, "Prévision par mois", ["Mois", "Nature du montant", "Clôtures prévues", "Montant estimé", "Montant pondéré"],
                f.Schedule.Select(m => new object?[]
                    { m.Month.ToDateTime(TimeOnly.MinValue), AmountKind(m.Kind), m.Count, m.Amount, m.WeightedAmount }),
                dateColumns: [1], dateFormat: "mmmm yyyy");

            Table(wb, "Prévision par agence",
                ["Agence", "Nature du montant", "Clôtures prévues", "Montant pondéré", "Clôtures dépassées", "Pondéré dépassé"],
                f.Agencies.Select(a => new object?[]
                    { a.Name, AmountKind(a.Kind), a.Count, a.WeightedAmount, a.Overdue, a.OverdueWeightedAmount }));
        }

        Table(wb, "Statuts", ["Statut actuel", "Leads"],
            r.ByStatus.Select(s => new object?[] { Status(s.Status), s.Count }));

        Table(wb, "Canaux", ["Canal d'acquisition", "Leads captés", "Convertis à ce jour", "Taux de conversion"],
            r.BySource.Select(s => new object?[] { Source(s.Source), s.Captured, s.Converted, s.ConversionRate / 100 }),
            percentColumns: [4]);

        Table(wb, "Agents",
            ["Agent", "Affectations", "Contactés dans le délai", "Contactés en retard", "Sans contact",
             "Délai respecté", "Premier contact moyen (min)", "Conversions", "Taux de conversion", "Charge actuelle"],
            r.Agents.Select(a => new object?[]
            {
                a.Name, a.Assigned, a.ContactedWithinSla, a.ContactedLate, a.NotContacted,
                a.SlaComplianceRate / 100, a.AvgFirstContactMinutes, a.Converted, a.ConversionRate / 100, a.OpenLeads,
            }),
            percentColumns: [6, 9]);

        Table(wb, "Agences", ["Agence", "Leads captés", "Convertis à ce jour", "Taux de conversion"],
            r.Agencies.Select(a => new object?[] { a.Name, a.Captured, a.Converted, a.ConversionRate / 100 }),
            percentColumns: [4]);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Summary(XLWorkbook wb, LeadReportDto r, string? agencyName, DateTimeOffset generatedAt)
    {
        var ws = wb.Worksheets.Add("Synthèse");
        ws.Cell(1, 1).Value = "Rapport des leads";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);

        // `To` is exclusive: the last covered day is the day before.
        var rows = new (string Label, object? Value, string? Format)[]
        {
            ("Du", r.From.UtcDateTime, "dd/mm/yyyy"),
            ("Au", r.To.AddTicks(-1).UtcDateTime, "dd/mm/yyyy"),
            ("Agence", agencyName ?? "Toutes les agences", null),
            ("Généré le", generatedAt.UtcDateTime, "dd/mm/yyyy hh:mm"),
        };
        var row = 3;
        foreach (var (lbl, value, format) in rows)
        {
            ws.Cell(row, 1).Value = lbl;
            ws.Cell(row, 1).Style.Font.SetBold();
            SetValue(ws.Cell(row, 2), value);
            if (format is not null) ws.Cell(row, 2).Style.NumberFormat.Format = format;
            row++;
        }

        row++;
        Header(ws, row, ["Indicateur", "Valeur", "Définition"]);
        var k = r.Kpis;
        var kpis = new (string, object?, string?, string)[]
        {
            ("Leads captés", k.Captured, null, "Leads dont la date de capture tombe dans la période."),
            ("Conversions", k.ConvertedInPeriod, null, "Conversions réalisées pendant la période, quelle que soit la date de capture."),
            ("Taux de conversion", k.CohortConversionRate / 100, "0.0%", "Part des leads captés pendant la période qui sont convertis aujourd'hui."),
            ("Leads perdus", k.Lost, null, "Leads captés pendant la période dont le statut actuel est Perdu."),
            ("Leads ouverts", k.OpenLeads, null, "Leads non clos aujourd'hui, toutes dates de capture."),
            ("Premier contact moyen (min)", k.AvgFirstContactMinutes, "0.0", "Délai moyen entre l'affectation et le premier contact, sur les affectations de la période."),
            ("Délai respecté", k.SlaComplianceRate / 100, "0.0%", "Premiers contacts réalisés avant l'échéance, sur les affectations de la période."),
            ("Délais dépassés en cours", k.OpenSlaBreaches, null, "Affectations dont l'échéance de premier contact est passée, à l'instant de l'export."),
        };
        foreach (var (lbl, value, format, definition) in kpis)
        {
            row++;
            ws.Cell(row, 1).Value = lbl;
            SetValue(ws.Cell(row, 2), value);
            if (format is not null) ws.Cell(row, 2).Style.NumberFormat.Format = format;
            ws.Cell(row, 3).Value = definition;
        }

        row += 2;
        ws.Cell(row, 1).Value = "Les périodes sont découpées en heure universelle (UTC).";
        ws.Cell(row, 1).Style.Font.SetItalic();

        ws.Column(1).Width = 30;
        ws.Column(2).Width = 18;
        ws.Column(3).Width = 90;
    }

    private static void Table(
        XLWorkbook wb, string name, string[] headers, IEnumerable<object?[]> rows,
        int[]? percentColumns = null, int[]? dateColumns = null, string dateFormat = "dd/mm/yyyy")
    {
        var ws = wb.Worksheets.Add(name);
        Header(ws, 1, headers);

        var r = 1;
        foreach (var values in rows)
        {
            r++;
            for (var c = 0; c < values.Length; c++)
                SetValue(ws.Cell(r, c + 1), values[c]);
        }

        foreach (var c in percentColumns ?? [])
            ws.Column(c).Style.NumberFormat.Format = "0.0%";
        foreach (var c in dateColumns ?? [])
            ws.Column(c).Style.NumberFormat.Format = dateFormat;

        ws.SheetView.FreezeRows(1);
        if (r > 1) ws.Range(1, 1, r, headers.Length).SetAutoFilter();
        ws.Columns(1, headers.Length).AdjustToContents(1, Math.Min(r, 500));
        ws.Column(1).Width = Math.Max(ws.Column(1).Width, 22);
        // Header cells keep the percent/date format harmless: they hold text.
        ws.Row(1).Style.NumberFormat.Format = "@";
    }

    private static void Header(IXLWorksheet ws, int row, string[] headers)
    {
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(row, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.SetBold();
            cell.Style.Fill.SetBackgroundColor(HeaderFill);
        }
    }

    /// <summary>A null stays an EMPTY cell, never a 0: "no first contact yet" is not "0 minutes".</summary>
    private static void SetValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null: cell.Clear(XLClearOptions.Contents); break;
            case int i: cell.Value = i; break;
            case double d: cell.Value = d; break;
            // Amounts: a number Excel can sum, not their text — the default branch wrote them as text.
            case decimal m:
                cell.Value = (double)m;
                cell.Style.NumberFormat.Format = "#,##0";
                break;
            case DateTime dt: cell.Value = dt; break;
            default: cell.Value = value.ToString(); break;
        }
    }

    private static string Stage(PipelineStage s) => s switch
    {
        PipelineStage.New => "Nouveau",
        PipelineStage.ContactAttempted => "Contact tenté",
        PipelineStage.ContactEstablished => "Contact établi",
        PipelineStage.NeedIdentified => "Besoin identifié",
        PipelineStage.Qualified => "Qualifié",
        PipelineStage.ProductProposed => "Produit proposé",
        PipelineStage.ApplicationStarted => "Dossier démarré",
        PipelineStage.DocumentCollection => "Collecte des pièces",
        PipelineStage.ApplicationCompleted => "Dossier complet",
        PipelineStage.ApprovalPending => "En approbation",
        PipelineStage.Converted => "Converti",
        PipelineStage.Lost => "Perdu",
        _ => s.ToString(),
    };

    private static string AmountKind(ProjectAmountKind k) => k switch
    {
        ProjectAmountKind.Loan => "Décaissements (crédit)",
        ProjectAmountKind.Premium => "Primes (assurance)",
        ProjectAmountKind.Deposit => "Collecte (épargne, dépôts)",
        _ => "Montant estimé",
    };

    private static string Phase(OpportunityStage s) => s switch
    {
        OpportunityStage.Prospecting => "Prospection",
        OpportunityStage.Qualification => "Qualification",
        OpportunityStage.NeedsAnalysis => "Étude",
        OpportunityStage.Proposal => "Proposition",
        OpportunityStage.Negotiation => "Négociation",
        OpportunityStage.ClosedWon => "Gagné",
        OpportunityStage.ClosedLost => "Perdu",
        _ => s.ToString(),
    };

    private static string Status(LeadStatus s) => s switch
    {
        LeadStatus.New => "Nouveau",
        LeadStatus.Open => "Ouvert",
        LeadStatus.Qualifying => "En qualification",
        LeadStatus.Qualified => "Qualifié",
        LeadStatus.Nurturing => "En maturation",
        LeadStatus.Recycled => "Recyclé",
        LeadStatus.Converted => "Converti",
        LeadStatus.Lost => "Perdu",
        LeadStatus.Disqualified => "Disqualifié",
        LeadStatus.Archived => "Archivé",
        _ => s.ToString(),
    };

    private static string Source(LeadSource s) => s switch
    {
        LeadSource.Web => "Web",
        LeadSource.MobileAgent => "Agent mobile",
        LeadSource.Agency => "Agence",
        LeadSource.CallCenter => "Centre d'appels",
        LeadSource.Sms => "SMS",
        LeadSource.Ussd => "USSD",
        LeadSource.WhatsApp => "WhatsApp",
        LeadSource.Referral => "Recommandation",
        LeadSource.Partner => "Partenaire",
        LeadSource.FileImport => "Import",
        LeadSource.Campaign => "Campagne",
        _ => s.ToString(),
    };
}
