namespace Sankore.Modules.Integration.Tests.Features.StartingData;

using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Sankore.Modules.Integration.Features.StartingData;
using Sankore.Shared.Infrastructure.Tenants;
using Xunit;

/// <summary>
/// The demo's information sheets are hand-written PDFs (lot 3): a reader opens them only if every
/// offset of the cross-reference table points at its object, which is what is checked here.
/// </summary>
public sealed class DemoPdfTests
{
    [Fact]
    public void Every_cross_reference_points_at_its_object()
    {
        var bytes = IntegrationDemoData.Sheet(IntegrationDemoData.Products[0]);
        var text = Encoding.Latin1.GetString(bytes);

        text.Should().StartWith("%PDF-1.4").And.EndWith("%%EOF\n");
        var startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        text[startxref..].Should().StartWith("xref");

        var offsets = Regex.Matches(text[startxref..], @"(\d{10}) 00000 n").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        offsets.Should().HaveCount(5);
        for (var i = 0; i < offsets.Count; i++)
            text[offsets[i]..].Should().StartWith($"{i + 1} 0 obj");

        var length = int.Parse(Regex.Match(text, @"/Length (\d+)").Groups[1].Value);
        var streamStart = text.IndexOf("stream\n", StringComparison.Ordinal) + "stream\n".Length;
        text.Substring(streamStart + length, "\nendstream".Length).Should().Be("\nendstream");
    }

    [Fact]
    public void Accents_are_kept_and_the_delimiters_escaped()
    {
        var text = Encoding.Latin1.GetString(DemoPdf.Page("Santé (démo)", ["Âge — 18 ans", "Prime : 2 000 FCFA"]));

        text.Should().Contain(@"(Santé \(démo\))").And.Contain("(Âge - 18 ans)").And.Contain("(Prime : 2 000 FCFA)");
    }
}
