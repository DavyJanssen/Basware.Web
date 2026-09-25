using System.Globalization;
using WpfAppBaswareLogin.ViewModels;

namespace WpfAppBaswareLogin.Services;

/// <summary>The same editable values feed the preview and the PDF export.</summary>
public sealed class SalesOrderPrintData
{
    public long Id { get; init; }
    public string ClientName { get; init; } = "-";
    public string PrintOrderNumber { get; init; } = "-";
    public string PrintOrderDate { get; init; } = "-";
    public string PrintRequestedDelivery { get; init; } = "-";
    public string PrintLaboratory { get; init; } = "-";
    public string PrintDeliveryPoint { get; init; } = "-";
    public string LaboratoryGroup { get; init; } = "";
    public List<LineDisplay> Lines { get; init; } = [];

    public static SalesOrderPrintData FromDetails(long id, EdiOrderDetails details)
    {
        var names = details.LaboratoryNames.OrderBy(n => n, Alphabetical).ToArray();
        return new()
        {
            Id = id,
            ClientName = details.DeliveryPointNames.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "-",
            PrintOrderNumber = details.OrderNumber ?? details.DocumentNumber,
            PrintOrderDate = details.OrderDate?.ToString("dd/MM/yyyy") ?? "-",
            PrintRequestedDelivery = details.RequestedDeliveryDate?.ToString("dd/MM/yyyy") ?? "-",
            PrintLaboratory = details.LaboratoryDisplay,
            PrintDeliveryPoint = details.DeliveryPointNames,
            // A mixed order appears once, in a group for its exact combination of laboratories.
            LaboratoryGroup = names.Length == 0 ? "Onbekend" : names.Length == 1 ? names[0] : "Gemengd: " + string.Join(" / ", names),
            Lines = details.Lines.Select(line => new LineDisplay
            {
                CnkDisplay = line.CnkNumbers.Length > 0 ? FormatCnk(line.CnkNumbers[0]) : "",
                Description = line.Description ?? "",
                Quantity = EdiOrderQuantity.OrderedAmount(line.QuantityValues)?.ToString("0.####", CultureInfo.CurrentCulture) ?? "—",
                Comment = ""
            }).ToList()
        };
    }

    public SalesOrderPrintData Snapshot() => new()
    {
        Id = Id, ClientName = ClientName, PrintOrderNumber = PrintOrderNumber,
        PrintOrderDate = PrintOrderDate, PrintRequestedDelivery = PrintRequestedDelivery,
        PrintLaboratory = PrintLaboratory, PrintDeliveryPoint = PrintDeliveryPoint, LaboratoryGroup = LaboratoryGroup,
        Lines = Lines.Select(l => new LineDisplay { CnkDisplay = l.CnkDisplay, Description = l.Description, Quantity = l.Quantity, Comment = l.Comment }).ToList()
    };

    public static readonly StringComparer Alphabetical = StringComparer.Create(CultureInfo.GetCultureInfo("nl-BE"), true);

    public static IReadOnlyList<SalesOrderPrintData> Sort(IEnumerable<SalesOrderPrintData> orders) => orders
        .OrderBy(o => o.LaboratoryGroup, Alphabetical)
        .ThenBy(o => o.PrintDeliveryPoint, Alphabetical)
        .ThenBy(o => o.PrintOrderNumber, Alphabetical)
        .ThenBy(o => o.Id).ToArray();

    private static string FormatCnk(string raw)
    {
        var digits = string.Concat(raw.Where(char.IsDigit));
        return digits.Length == 7 ? digits[..4] + "-" + digits[4..] : raw;
    }
}
