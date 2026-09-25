using System.Globalization;

namespace WpfAppBaswareLogin.Services;

public sealed record SummaryLocation(string Key, string Name, string Header);
public sealed record SummaryOrder(long Id, EdiOrderDetails Details);

public sealed class SummaryProduct(string laboratory, string cnk, string description)
{
    public string Laboratory { get; } = laboratory;
    public string Cnk { get; } = cnk;
    public string Description { get; internal set; } = description;
    public Dictionary<string, decimal> QuantitiesByLocation { get; } = new(StringComparer.OrdinalIgnoreCase);
    public decimal Total => QuantitiesByLocation.Values.Sum();
}

public sealed record SalesOrderSummary(IReadOnlyList<SummaryLocation> Locations, IReadOnlyList<SummaryProduct> Products, int OrderCount)
{
    private static readonly StringComparer Alphabetical = StringComparer.Create(CultureInfo.GetCultureInfo("nl-BE"), true);

    public static SalesOrderSummary Create(IEnumerable<SummaryOrder> source)
    {
        var orders = source.DistinctBy(o => o.Id).ToArray();
        if (orders.Length == 0) throw new InvalidOperationException("Selecteer minstens één order.");
        var locations = new Dictionary<string, SummaryLocation>(StringComparer.OrdinalIgnoreCase);
        var products = new Dictionary<(string Lab, string Product), SummaryProduct>();
        foreach (var order in orders)
        {
            var location = GetLocation(order);
            locations.TryAdd(location.Key, location);
            foreach (var line in order.Details.Lines)
            {
                var quantity = EdiOrderQuantity.OrderedAmount(line.QuantityValues);
                if (quantity is null)
                    throw new InvalidOperationException($"Order {OrderName(order)}, regel {line.LineNumber}: geen eenduidig besteld aantal (ORD of 21). Controleer de bronorder.");
                var cnks = line.CnkNumbers.Select(NormalizeCnk).Distinct(StringComparer.Ordinal).ToArray();
                if (cnks.Length > 1)
                    throw new InvalidOperationException($"Order {OrderName(order)}, regel {line.LineNumber}: meerdere CNK-codes. Controleer welke productcode bij deze regel hoort.");
                var cnk = cnks.FirstOrDefault() ?? "";
                var laboratory = Laboratory(line);
                // Missing identifiers must not collapse unrelated products into a single total.
                var productKey = cnk.Length > 0 ? cnk : $"missing:{order.Id}:{line.Id}:{line.LineNumber}";
                var key = (laboratory.ToUpperInvariant(), productKey);
                var description = string.IsNullOrWhiteSpace(line.Description) ? "Omschrijving ontbreekt" : line.Description.Trim();
                if (!products.TryGetValue(key, out var product))
                {
                    product = new SummaryProduct(laboratory, cnk.Length > 0 ? cnk.Insert(4, "-") : "Ontbreekt", description);
                    products.Add(key, product);
                }
                else if (Alphabetical.Compare(description, product.Description) < 0)
                {
                    // Stable naming when the same CNK has slightly different source descriptions.
                    product.Description = description;
                }
                product.QuantitiesByLocation[location.Key] = product.QuantitiesByLocation.GetValueOrDefault(location.Key) + quantity.Value;
            }
        }
        if (products.Count == 0) throw new InvalidOperationException("De geselecteerde orders bevatten geen orderregels.");
        var sortedLocations = locations.Values.OrderBy(l => l.Name, Alphabetical).ThenBy(l => l.Key, Alphabetical).ToArray();
        // A reused short code must not produce indistinguishable column headings.
        var duplicateHeaders = sortedLocations.GroupBy(l => l.Header, Alphabetical).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(Alphabetical);
        sortedLocations = sortedLocations.Select(l => duplicateHeaders.Contains(l.Header) ? l with { Header = $"{l.Header} ({l.Key})" } : l).ToArray();
        return new(sortedLocations, products.Values.OrderBy(p => p.Laboratory, Alphabetical)
            .ThenBy(p => p.Description, Alphabetical).ThenBy(p => p.Cnk, Alphabetical).ToArray(), orders.Length);
    }

    private static string NormalizeCnk(string value)
    {
        var normalized = value.Trim().Replace("-", "");
        if (normalized.Length != 7 || normalized.Any(c => c < '0' || c > '9'))
            throw new InvalidOperationException($"Ongeldige CNK-code: {value}.");
        return normalized;
    }

    private static string Laboratory(EdiOrderLineDetails line)
    {
        var direct = line.DirectLaboratoryNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(Alphabetical).OrderBy(n => n, Alphabetical).ToArray();
        if (direct.Length == 1) return direct[0];
        if (direct.Length > 1) return "Conflict: " + string.Join(" / ", direct);
        var inferred = line.OrderLaboratoryNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(Alphabetical).ToArray();
        return inferred.Length == 1 ? inferred[0] : "Onbekend";
    }

    private static SummaryLocation GetLocation(SummaryOrder order)
    {
        var points = order.Details.Partners.Where(p => p.PartnerType == "DP")
            .DistinctBy(p => string.IsNullOrWhiteSpace(p.PartnerId) ? p.DisplayName : p.PartnerId).ToArray();
        if (points.Length > 1)
            throw new InvalidOperationException($"Order {OrderName(order)} heeft meerdere leveringsvestigingen. De aantallen kunnen niet eenduidig per vestiging worden verdeeld.");
        if (points.Length == 0) return new("unknown", "Onbekende vestiging", "Onbekend");
        var point = points[0];
        var name = point.LocationName ?? point.FullName ?? point.Name ?? point.PartnerId ?? "Onbekende vestiging";
        var key = string.IsNullOrWhiteSpace(point.PartnerId) ? "name:" + name : point.PartnerId.Trim();
        return new(key, name, string.IsNullOrWhiteSpace(point.ShortCode) ? name : point.ShortCode.Trim());
    }

    private static string OrderName(SummaryOrder order) => order.Details.OrderNumber ?? order.Details.DocumentNumber;
}
