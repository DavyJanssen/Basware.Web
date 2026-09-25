using System.IO;
using ClosedXML.Excel;

namespace WpfAppBaswareLogin.Services;

public static class SalesOrderExcelExporter
{
    public static void Export(string filename, SalesOrderSummary summary, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (summary.Products.Count == 0 || summary.Locations.Count == 0)
            throw new InvalidOperationException("Er zijn geen orderregels om te exporteren.");
        if (summary.Locations.Count > 16381) throw new InvalidOperationException("Te veel vestigingen voor één Excel-werkblad.");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Bestelde producten");
        sheet.ShowGridLines = false;
        sheet.Style.Font.FontName = "Calibri";
        sheet.Style.Font.FontSize = 11;
        sheet.Column(1).Width = 14;
        sheet.Column(1).Style.NumberFormat.Format = "@";
        sheet.Column(2).Width = 62;
        var totalColumn = summary.Locations.Count + 3;
        for (var index = 0; index < summary.Locations.Count; index++)
            sheet.Column(index + 3).Width = Math.Clamp(summary.Locations[index].Header.Length + 3, 8, 30);
        sheet.Column(totalColumn).Width = 16;
        sheet.SheetView.Freeze(1, 2);
        sheet.Cell(1, 1).Value = "CNK";
        sheet.Cell(1, 2).Value = "Omschrijving";
        Headers(1);
        var row = 2;
        foreach (var group in summary.Products.GroupBy(p => p.Laboratory, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            sheet.Range(row, 1, row, 2).Merge().Value = group.Key;
            Headers(row);
            sheet.Row(row).Height = Math.Max(25, 16 * Math.Ceiling(group.Key.Length / 70.0));
            sheet.Range(row, 1, row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            row++;
            foreach (var product in group)
            {
                ct.ThrowIfCancellationRequested();
                if (row > 1048576) throw new InvalidOperationException("Te veel productregels voor één Excel-werkblad.");
                sheet.Cell(row, 1).Value = product.Cnk;
                sheet.Cell(row, 2).Value = product.Description;
                for (var index = 0; index < summary.Locations.Count; index++)
                    sheet.Cell(row, index + 3).Value = (double)product.QuantitiesByLocation.GetValueOrDefault(summary.Locations[index].Key);
                var total = sheet.Cell(row, totalColumn);
                total.FormulaA1 = $"SUM(C{row}:{XLHelper.GetColumnLetterFromNumber(totalColumn - 1)}{row})";
                total.Style.Font.Bold = true;
                total.Style.Fill.BackgroundColor = XLColor.FromHtml("#EDF4F9");
                var numbers = sheet.Range(row, 3, row, totalColumn);
                numbers.Style.NumberFormat.Format = "[=0]\"\";General";
                total.Style.NumberFormat.Format = "General";
                numbers.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                sheet.Range(row, 1, row, totalColumn).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                sheet.Cell(row, 2).Style.Alignment.WrapText = true;
                sheet.Row(row).Height = Math.Max(22, 16 * product.Description.Split('\n').Sum(part => Math.Max(1, (int)Math.Ceiling(part.Length / 58.0))));
                row++;
            }
        }
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(1, 1);
        sheet.PageSetup.PrintAreas.Add($"A1:{XLHelper.GetColumnLetterFromNumber(totalColumn)}{row - 1}");
        workbook.RecalculateAllFormulas();
        ct.ThrowIfCancellationRequested();
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filename))!, ".basware-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            workbook.SaveAs(temporary, new SaveOptions { EvaluateFormulasBeforeSaving = true });
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, filename, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void Headers(int headerRow)
        {
            for (var index = 0; index < summary.Locations.Count; index++)
                sheet.Cell(headerRow, index + 3).Value = summary.Locations[index].Header;
            sheet.Cell(headerRow, totalColumn).Value = "Totaal besteld";
            var header = sheet.Range(headerRow, 1, headerRow, totalColumn);
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#045187");
            header.Style.Font.FontColor = XLColor.White;
            header.Style.Font.Bold = true;
            header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            header.Style.Alignment.WrapText = true;
            sheet.Row(headerRow).Height = 30;
        }
    }
}
