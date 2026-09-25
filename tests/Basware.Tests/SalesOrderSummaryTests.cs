using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using WpfAppBaswareLogin.Services;
using Xunit;

namespace Basware.Tests;

public class SalesOrderSummaryTests
{
    [Theory]
    [InlineData("ORD")]
    [InlineData(" ord ")]
    [InlineData("21")]
    public void RecognizesBaswareAndEdifactOrderedQuantities(string type)
    {
        var line = Line("1234567", "Product", 12.5m);
        line.QuantityValues.Clear();
        line.QuantityValues.Add(new("52", 200));
        line.QuantityValues.Add(new(type, 12.5m));
        Assert.Equal(12.5m, Assert.Single(SalesOrderSummary.Create([Order(1, "ZO", "Zolder", line)]).Products).Total);
    }

    [Fact]
    public void RejectsConflictingOrderedAliases()
    {
        var line = Line("1234567", "Product", 12);
        line.QuantityValues.Add(new("ORD", 24));
        Assert.Throws<InvalidOperationException>(() => SalesOrderSummary.Create([Order(1, "ZO", "Zolder", line)]));
    }

    [Fact]
    public void AddsRepeatedCnkAcrossOrdersAndBranchesWithoutDoubleCountingSelectedOrder()
    {
        var first = Order(1, "ZO", "Zolder", Line("0123456", "Zink", 12));
        var second = Order(2, "BR", "Brugge", Line("0123-456", "Zink", 3.5m), Line("0123456", "Zink", 2));
        var result = SalesOrderSummary.Create([first, second, first]);
        var product = Assert.Single(result.Products);
        Assert.Equal("0123-456", product.Cnk);
        Assert.Equal(17.5m, product.Total);
        Assert.Equal(12m, product.QuantitiesByLocation["ZO"]);
        Assert.Equal(5.5m, product.QuantitiesByLocation["BR"]);
        Assert.Equal(new[] { "BR", "ZO" }, result.Locations.Select(l => l.Header));
        Assert.Equal(2, result.OrderCount);
    }

    [Fact]
    public void SortsByLaboratoryThenDescriptionAndUsesLineLaboratory()
    {
        var result = SalesOrderSummary.Create([Order(1, "ZO", "Zolder",
            Line("1234567", "Zink", 5, "GSK"), Line("1234568", "Ascorbine", 2, "GSK"), Line("1234569", "Zink", 1, "AbbVie"))]);
        Assert.Equal(new[] { "AbbVie", "GSK", "GSK" }, result.Products.Select(p => p.Laboratory));
        Assert.Equal(new[] { "Zink", "Ascorbine", "Zink" }, result.Products.Select(p => p.Description));
    }

    [Fact]
    public void CountsOrderedQuantityOnlyAndPreservesZero()
    {
        var line = Line("1234567", "Product", 0);
        line.QuantityValues.Add(new("52", 200));
        line.QuantityValues.Add(new("12", 20));
        Assert.Equal(0, Assert.Single(SalesOrderSummary.Create([Order(1, "ZO", "Zolder", line)]).Products).Total);
    }

    [Fact]
    public void RejectsMissingOrAmbiguousOrderedQuantity()
    {
        var line = Line("1234567", "Product", 1);
        line.QuantityValues.Clear();
        line.QuantityValues.Add(new("52", 200));
        Assert.Throws<InvalidOperationException>(() => SalesOrderSummary.Create([Order(1, "ZO", "Zolder", line)]));
        line.QuantityValues.Add(new("21", null));
        Assert.Throws<InvalidOperationException>(() => SalesOrderSummary.Create([Order(1, "ZO", "Zolder", line)]));
        line.QuantityValues.Add(new("21", 12));
        Assert.Throws<InvalidOperationException>(() => SalesOrderSummary.Create([Order(1, "ZO", "Zolder", line)]));
    }

    [Fact]
    public void DoesNotMergeMissingCnkAndDoesNotDuplicateAmbiguousBranches()
    {
        var a = new EdiOrderLineDetails { Id = 1, Description = "Zelfde naam" };
        var b = new EdiOrderLineDetails { Id = 2, Description = "Zelfde naam" };
        a.QuantityValues.Add(new("21", 5)); b.QuantityValues.Add(new("21", 8));
        var order = Order(1, "ZO", "Zolder", a, b);
        Assert.Equal(2, SalesOrderSummary.Create([order]).Products.Count);
        order.Details.Partners.Add(new("DP", "BR", "", "Brugge"));
        Assert.Throws<InvalidOperationException>(() => SalesOrderSummary.Create([order]));
    }

    [Fact]
    public void InferredUnknownAndConflictingLaboratoriesRemainVisible()
    {
        var inferred = new EdiOrderLineDetails { Id = 1, CnkNumbers = ["1234567"], OrderLaboratoryNames = ["GSK"] };
        var unknown = new EdiOrderLineDetails { Id = 2, CnkNumbers = ["1234568"], OrderLaboratoryNames = ["GSK", "AbbVie"] };
        var conflict = new EdiOrderLineDetails { Id = 3, CnkNumbers = ["1234569"], DirectLaboratoryNames = ["GSK", "AbbVie"] };
        foreach (var line in new[] { inferred, unknown, conflict }) line.QuantityValues.Add(new("21", 1));
        var result = SalesOrderSummary.Create([Order(1, "ZO", "Zolder", inferred, unknown, conflict)]);
        Assert.Equal(new[] { "Conflict: AbbVie / GSK", "GSK", "Onbekend" }, result.Products.Select(p => p.Laboratory));
        Assert.Equal(3m, result.Products.Sum(p => p.Total));
    }

    [Fact]
    public void WorkbookHasNumericBranchesFormulaTotalsAndPreservesIdentifiers()
    {
        var result = SalesOrderSummary.Create([Order(1, "ZO", "Zolder", Line("0123456", "=Product", 12)),
            Order(2, "BR", "Brugge", Line("0123456", "=Product", 3.5m))]);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            SalesOrderExcelExporter.Export(path, result);
            using (var package = SpreadsheetDocument.Open(path, false))
                Assert.Empty(new OpenXmlValidator().Validate(package));
            using var book = new XLWorkbook(path);
            var sheet = Assert.Single(book.Worksheets);
            Assert.Equal("BR", sheet.Cell("C1").GetString());
            Assert.Equal("ZO", sheet.Cell("D1").GetString());
            Assert.Equal("0123-456", sheet.Cell("A3").GetString());
            Assert.Equal("=Product", sheet.Cell("B3").GetString());
            Assert.False(sheet.Cell("B3").HasFormula);
            Assert.Equal(XLDataType.Number, sheet.Cell("C3").DataType);
            Assert.Equal("SUM(C3:D3)", sheet.Cell("E3").FormulaA1);
            Assert.Equal(15.5, sheet.Cell("E3").GetDouble());
            sheet.Cell("C3").Value = 10;
            book.RecalculateAllFormulas();
            Assert.Equal(22, sheet.Cell("E3").GetDouble());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CancelledExportDoesNotOverwriteExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            File.WriteAllText(path, "existing");
            var result = SalesOrderSummary.Create([Order(1, "ZO", "Zolder", Line("1234567", "Product", 1))]);
            Assert.Throws<OperationCanceledException>(() => SalesOrderExcelExporter.Export(path, result, new CancellationToken(true)));
            Assert.Equal("existing", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    private static EdiOrderLineDetails Line(string cnk, string description, decimal quantity, string lab = "GSK")
    {
        var line = new EdiOrderLineDetails { CnkNumbers = [cnk], Description = description, DirectLaboratoryNames = [lab] };
        line.QuantityValues.Add(new("21", quantity));
        return line;
    }

    private static SummaryOrder Order(long id, string code, string place, params EdiOrderLineDetails[] lines)
    {
        var details = new EdiOrderDetails { OrderNumber = id.ToString() };
        details.Partners.Add(new("DP", code, "", place, "Febelco " + place, code, place));
        details.Lines.AddRange(lines);
        return new(id, details);
    }
}
