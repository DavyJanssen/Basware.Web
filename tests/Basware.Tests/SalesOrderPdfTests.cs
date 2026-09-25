using System;
using System.Linq;
using WpfAppBaswareLogin.Services;
using WpfAppBaswareLogin.ViewModels;
using Xunit;

namespace Basware.Tests;

public class SalesOrderPdfTests
{
    [Theory]
    [InlineData("ORD", 12)]
    [InlineData(" ord ", 0)]
    [InlineData("21", 24)]
    public void PreviewAndPdfShowOnlyTheOrderedNumber(string type, int quantity)
    {
        var details = new EdiOrderDetails();
        var line = new EdiOrderLineDetails();
        line.QuantityValues.Add(new("52", 200));
        line.QuantityValues.Add(new(type, quantity));
        line.Quantities.Add("52: 200");
        line.Quantities.Add($"{type}: {quantity}");
        details.Lines.Add(line);
        var printed = SalesOrderPrintData.FromDetails(1, details);
        Assert.Equal(quantity.ToString(), Assert.Single(printed.Lines).Quantity);
        var html = SalesOrderPdfDocument.BuildHtml([printed]);
        Assert.DoesNotContain("ORD:", html);
        Assert.Contains($"<td>{quantity}</td>", html);
    }

    [Fact]
    public void GroupsLaboratoriesThenSortsLocationsAndOrderNumbers()
    {
        var orders = new[] { Order(1, "GSK", "Zolder", "B"), Order(2, "AbbVie", "Zolder", "A"),
            Order(3, "gsk", "Antwerpen", "B"), Order(4, "GSK", "Antwerpen", "A") };
        Assert.Equal(new long[] { 2, 4, 3, 1 }, SalesOrderPrintData.Sort(orders).Select(o => o.Id));
    }

    [Fact]
    public void MixedOrdersUseOneCanonicalGroupAndUnknownOrdersHaveAGroup()
    {
        Assert.Equal("Gemengd: AbbVie / GSK", SalesOrderPrintData.FromDetails(1,
            new EdiOrderDetails { LaboratoryNames = ["GSK", "AbbVie"] }).LaboratoryGroup);
        Assert.Equal("Onbekend", SalesOrderPrintData.FromDetails(2, new()).LaboratoryGroup);
    }

    [Fact]
    public void SnapshotKeepsEditedCommentsWithoutSharingMutableRows()
    {
        var original = Order(1, "GSK", "Zolder", "A");
        original.Lines.Add(new LineDisplay { Comment = "Bewaren" });
        var snapshot = original.Snapshot();
        original.Lines[0].Comment = "Gewijzigd";
        Assert.Equal("Bewaren", snapshot.Lines[0].Comment);
    }

    [Fact]
    public void HtmlIncludesEveryRowEscapesTextAndCreatesOneSectionPerOrder()
    {
        var order = Order(1, "GSK", "Zolder", "A");
        for (var i = 0; i < 100; i++) order.Lines.Add(new LineDisplay { Description = $"Regel-{i}", Comment = "<test> & tekst" });
        var html = SalesOrderPdfDocument.BuildHtml([order, Order(2, "AbbVie", "Antwerpen", "B")]);
        Assert.Equal(2, html.Split("<section class=\"page\">").Length - 1);
        Assert.Contains("Regel-99", html);
        Assert.Contains("&lt;test&gt; &amp; tekst", html);
        Assert.DoesNotContain("<test>", html);
        Assert.True(html.IndexOf("AbbVie", StringComparison.Ordinal) < html.IndexOf("GSK", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => SalesOrderPdfDocument.BuildHtml([]));
    }

    private static SalesOrderPrintData Order(long id, string lab, string location, string number) => new()
    { Id = id, LaboratoryGroup = lab, PrintLaboratory = lab, PrintDeliveryPoint = location, PrintOrderNumber = number };
}
