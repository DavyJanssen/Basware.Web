using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace WpfAppBaswareLogin.Services;

public sealed record EdiOrderSummary(long Id, string? OrderNumber, string DocumentNumber,
    DateTime? OrderDate, DateTime? RequestedDeliveryDate,
    string[] LaboratoryNames, int UnmatchedLineCount, string? DeliveryPointId)
{
    public DateTime? DocumentDate { get; init; }
    public string? DeliveryPointShortCode { get; init; }
    public string[] DeliveryPointSearchNames { get; init; } = [];
    public bool HasMixedLaboratories => LaboratoryNames.Length > 1;
    public string LaboratoryDisplay => LaboratoryFormatting.OrderName(LaboratoryNames, UnmatchedLineCount);
    public string LaboratoryNotice => LaboratoryFormatting.Notice(LaboratoryNames, UnmatchedLineCount);
}

public static class LaboratoryFormatting
{
    public static string OrderName(string[] names, int unmatched) => names.Length switch
    {
        0 => "Onbekend",
        1 => names[0] + (unmatched > 0 ? " (deels afgeleid)" : ""),
        _ => "Gemengd: " + string.Join(" / ", names)
    };
    public static string Notice(string[] names, int unmatched) => names.Length switch
    {
        0 => "Geen CNK-labokoppeling gevonden. Voeg een koppeling toe om het labo te bepalen.",
        1 when unmatched > 0 => $"Bij {unmatched} regel(s) is het labo afgeleid uit de andere orderregels. Bevestig de CNK-koppeling voordat je deze als basis voor korting gebruikt.",
        1 => "Het labo is op alle regels rechtstreeks via een CNK gekoppeld.",
        _ => "Deze order bevat meerdere labo’s. Controleer de order en stem de afwijking met de klant af. Onbekende regels krijgen bij deze order geen afgeleid labo."
    };
}

public sealed record EdiOrderPartner(
    string? PartnerType, string? PartnerId, string? PartnerIdType, string? Name, string? FullName = null,
    string? ShortCode = null, string? LocationName = null)
{
    // Name blijft de originele XML-naam; alleen de weergave wordt verrijkt.
    public string? DisplayName => PartnerType == "DP"
        ? FullName ?? $"Onbekend ({PartnerId ?? "—"})"
        : Name;
}

public sealed record EdiOrderQuantity(string? Type, decimal? Amount)
{
    public bool IsOrdered => string.Equals(Type?.Trim(), "ORD", StringComparison.OrdinalIgnoreCase)
        || Type?.Trim() == "21";

    // Basware uses ORD; also accept the numeric EDIFACT qualifier 21.
    // Never use other quantity types or add duplicate declarations together.
    public static decimal? OrderedAmount(IEnumerable<EdiOrderQuantity> quantities)
    {
        var ordered = quantities.Where(q => q.IsOrdered).ToArray();
        return ordered.Length == 1 ? ordered[0].Amount : null;
    }
}

public sealed class EdiOrderLineDetails
{
    public long Id { get; init; }
    public int LineNumber { get; init; }
    public string? Description { get; init; }
    public string[] CnkNumbers { get; init; } = [];
    public string[] DirectLaboratoryNames { get; init; } = [];
    public string[] OrderLaboratoryNames { get; init; } = [];
    public bool HasLaboratoryConflict => DirectLaboratoryNames.Length > 1;
    public string LaboratoryDisplay => DirectLaboratoryNames.Length switch
    {
        1 => DirectLaboratoryNames[0],
        > 1 => "Conflict: " + string.Join(" / ", DirectLaboratoryNames),
        _ => OrderLaboratoryNames.Length == 1 ? OrderLaboratoryNames[0] + " (afgeleid)" : "Onbekend"
    };
    public string LaboratoryBasis => DirectLaboratoryNames.Length switch
    {
        1 => "Rechtstreeks gekoppeld via CNK",
        > 1 => "De productidentificaties op deze regel wijzen naar verschillende labo’s.",
        _ => OrderLaboratoryNames.Length == 1 ? "Afgeleid uit andere regels; geen bevestigde CNK-koppeling." : "Geen koppeling; labo niet afgeleid."
    };
    public List<string> Products { get; } = [];
    public List<string> Quantities { get; } = [];
    public List<EdiOrderQuantity> QuantityValues { get; } = [];
    public string ProductDisplay => string.Join(Environment.NewLine, Products);
    public string QuantityDisplay => string.Join(Environment.NewLine, Quantities);
}

public sealed class EdiOrderDetails
{
    public string[] LaboratoryNames { get; init; } = [];
    public int UnmatchedLineCount { get; init; }
    public bool HasMixedLaboratories => LaboratoryNames.Length > 1;
    public string LaboratoryDisplay => LaboratoryFormatting.OrderName(LaboratoryNames, UnmatchedLineCount);
    public string LaboratoryNotice => LaboratoryFormatting.Notice(LaboratoryNames, UnmatchedLineCount);
    public string DocumentNumber { get; init; } = "";
    public DateTime? DocumentDate { get; init; }
    public string? SenderId { get; init; }
    public string? ReceiverId { get; init; }
    public string? TestIndicator { get; init; }
    public string? Version { get; init; }
    public string? SourceFilename { get; init; }
    public DateTime ImportedAt { get; init; }
    public string? RawXml { get; init; }
    public string? MessageReferenceNumber { get; init; }
    public string? MessageType { get; init; }
    public string? OrderNumber { get; init; }
    public string? MessageFunction { get; init; }
    public string? Currency { get; init; }
    public DateTime? OrderDate { get; init; }
    public DateTime? RequestedDeliveryDate { get; init; }
    public List<EdiOrderPartner> Partners { get; } = [];
    // Bij meerdere DP-partners blijven alle nummers/namen in dezelfde volgorde zichtbaar.
    public string DeliveryPointNumbers => Partners.Any(p => p.PartnerType == "DP")
        ? string.Join(Environment.NewLine, Partners.Where(p => p.PartnerType == "DP").Select(p => p.PartnerId ?? "—")) : "—";
    public string DeliveryPointNames => Partners.Any(p => p.PartnerType == "DP")
        ? string.Join(Environment.NewLine, Partners.Where(p => p.PartnerType == "DP").Select(p => p.DisplayName)) : "—";
    public List<EdiOrderLineDetails> Lines { get; } = [];
}

/// <summary>Read-only weergave van de EDI-tabellen en vestigingsnamen; geen schemawijzigingen.</summary>
public sealed class EdiOrderRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<EdiOrderSummary>> GetOrdersAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT o.id, o.message_number, d.document_number,
                   o.order_date, o.requested_delivery_date, labs.laboratory_names, labs.unmatched_line_count,
                   dp.dp_id, d.document_date, dp.short_code,
                   ARRAY(SELECT DISTINCT COALESCE(location.full_name, p.name, p.partner_id, '')
                         FROM public.edi_partner p
                         LEFT JOIN public.edi_delivery_location location ON location.dp_number = p.partner_id
                         WHERE p.order_id = o.id AND p.partner_type = 'DP') AS delivery_names
            FROM public.edi_order o
            JOIN public.edi_document d ON d.id = o.document_id
            JOIN public.edi_order_laboratory labs ON labs.order_id = o.id
            LEFT JOIN LATERAL (
                SELECT p.partner_id AS dp_id, location.short_code
                FROM public.edi_partner p
                LEFT JOIN public.edi_delivery_location location ON location.dp_number = p.partner_id
                WHERE p.order_id = o.id AND p.partner_type = 'DP'
                ORDER BY p.id
                LIMIT 1
            ) dp ON true
            ORDER BY o.order_date DESC NULLS LAST, o.id DESC;
            """);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var orders = new List<EdiOrderSummary>();
        while (await rows.ReadAsync(ct))
            orders.Add(new(rows.GetInt64(0), Text(rows, 1), rows.GetString(2), Date(rows, 3), Date(rows, 4), rows.GetFieldValue<string[]>(5), rows.GetInt32(6), Text(rows, 7))
            {
                DocumentDate = Date(rows, 8),
                DeliveryPointShortCode = Text(rows, 9),
                DeliveryPointSearchNames = rows.GetFieldValue<string[]>(10)
            });
        return orders;
    }

    public async Task<EdiOrderDetails?> GetDetailsAsync(long orderId, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        // Eén consistente momentopname voor kop, partners, regels en hun kinderen.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        EdiOrderDetails details;
        await using (var command = Command("""
            SELECT d.document_number, d.document_date, d.sender_id, d.receiver_id,
                   d.test_indicator, d.version, d.source_filename, d.imported_at, d.raw_xml,
                   o.message_reference_number, o.message_type, o.message_number,
                   o.message_function, o.currency, o.order_date, o.requested_delivery_date,
                   labs.laboratory_names, labs.unmatched_line_count
            FROM public.edi_order o JOIN public.edi_document d ON d.id = o.document_id
            JOIN public.edi_order_laboratory labs ON labs.order_id = o.id
            WHERE o.id = $1;
            """))
        await using (var rows = await command.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct)) return null;
            details = new()
            {
                DocumentNumber = rows.GetString(0), DocumentDate = Date(rows, 1),
                SenderId = Text(rows, 2), ReceiverId = Text(rows, 3), TestIndicator = Text(rows, 4),
                Version = Text(rows, 5), SourceFilename = Text(rows, 6), ImportedAt = rows.GetDateTime(7),
                RawXml = Text(rows, 8), MessageReferenceNumber = Text(rows, 9), MessageType = Text(rows, 10),
                OrderNumber = Text(rows, 11), MessageFunction = Text(rows, 12), Currency = Text(rows, 13),
                OrderDate = Date(rows, 14), RequestedDeliveryDate = Date(rows, 15),
                LaboratoryNames = rows.GetFieldValue<string[]>(16), UnmatchedLineCount = rows.GetInt32(17)
            };
        }
        await using (var command = Command("""
            SELECT p.partner_type, p.partner_id, p.partner_id_type, p.name, location.full_name, location.short_code, location.location_name
            FROM public.edi_partner p
            LEFT JOIN public.edi_delivery_location location
                ON p.partner_type = 'DP' AND location.dp_number = p.partner_id
            WHERE p.order_id = $1 ORDER BY p.id;
            """))
        await using (var rows = await command.ExecuteReaderAsync(ct))
        {
            while (await rows.ReadAsync(ct))
                details.Partners.Add(new(Text(rows, 0), Text(rows, 1), Text(rows, 2), Text(rows, 3), Text(rows, 4), Text(rows, 5), Text(rows, 6)));
        }
        var lines = new Dictionary<long, EdiOrderLineDetails>();
        await using (var command = Command("""
            SELECT line.id, line.line_number, line.long_description, labs.cnk_numbers, labs.laboratory_names
            FROM public.edi_order_line line
            JOIN public.edi_line_laboratory_match labs ON labs.order_line_id = line.id
            WHERE line.order_id = $1 ORDER BY line.line_number, line.id;
            """))
        await using (var rows = await command.ExecuteReaderAsync(ct))
        {
            while (await rows.ReadAsync(ct))
            {
                var line = new EdiOrderLineDetails
                {
                    Id = rows.GetInt64(0), LineNumber = rows.GetInt32(1), Description = Text(rows, 2),
                    CnkNumbers = rows.GetFieldValue<string[]>(3), DirectLaboratoryNames = rows.GetFieldValue<string[]>(4),
                    OrderLaboratoryNames = details.LaboratoryNames
                };
                lines.Add(line.Id, line);
                details.Lines.Add(line);
            }
        }
        // Productcodes en hoeveelheden apart lezen voorkomt vermenigvuldigde regels
        // wanneer een OrderItem meerdere ProductIdentification én Quantity-elementen heeft.
        await using (var command = Command("""
            SELECT p.order_line_id, p.function_code, p.product_type, p.product_number
            FROM public.edi_product_identification p
            JOIN public.edi_order_line l ON l.id = p.order_line_id
            WHERE l.order_id = $1 ORDER BY l.line_number, p.id;
            """))
        await using (var rows = await command.ExecuteReaderAsync(ct))
        {
            while (await rows.ReadAsync(ct))
                lines[rows.GetInt64(0)].Products.Add(
                    $"{Text(rows, 3) ?? "—"} · {Text(rows, 2) ?? "—"} · {Text(rows, 1) ?? "—"}");
        }
        await using (var command = Command("""
            SELECT q.order_line_id, q.quantity_type, q.amount
            FROM public.edi_quantity q JOIN public.edi_order_line l ON l.id = q.order_line_id
            WHERE l.order_id = $1 ORDER BY l.line_number, q.id;
            """))
        await using (var rows = await command.ExecuteReaderAsync(ct))
        {
            while (await rows.ReadAsync(ct))
            {
                decimal? numericAmount = rows.IsDBNull(2) ? null : rows.GetDecimal(2);
                lines[rows.GetInt64(0)].QuantityValues.Add(new(Text(rows, 1), numericAmount));
                var amount = numericAmount?.ToString("0.####", CultureInfo.CurrentCulture) ?? "—";
                lines[rows.GetInt64(0)].Quantities.Add($"{Text(rows, 1) ?? "—"}: {amount}");
            }
        }
        await transaction.CommitAsync(ct);
        return details;

        NpgsqlCommand Command(string sql)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(orderId);
            return command;
        }
    }

    private static string? Text(NpgsqlDataReader rows, int index) => rows.IsDBNull(index) ? null : rows.GetString(index);
    private static DateTime? Date(NpgsqlDataReader rows, int index) => rows.IsDBNull(index) ? null : rows.GetDateTime(index);
}
