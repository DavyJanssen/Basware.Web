using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Npgsql;
using NpgsqlTypes;

namespace WpfAppBaswareLogin.Services;

public sealed record EdiImportResult(long DocumentId, string DocumentNumber, bool Imported)
{
    /// <summary>Aantal orders in het document, ook bij een reeds aanwezige import.</summary>
    public int OrderCount { get; init; }
}

/// <summary>Importeert Basware XML in public.edi_*. Vereist de identity-migratie.
/// De aanroeper beheert de levensduur van de gedeelde NpgsqlDataSource.</summary>
public sealed class EdiXmlImporter(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Zoekt in één query welke auditnummers al in edi_document staan.
    /// Ondersteunt order_123.xml (downloader) en 123.xml (eerdere voorbeelden).
    /// Veronderstelt dat bestaande documenten volledig/atomair zijn geïmporteerd.
    /// Een ontbrekende of afwijkende source_filename leidt bewust niet tot overslaan.
    /// </summary>
    public async Task<IReadOnlySet<string>> GetImportedAuditNumbersAsync(
        IReadOnlyCollection<string> auditNumbers, CancellationToken ct = default)
    {
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var auditNumber in auditNumbers)
        {
            if (string.IsNullOrEmpty(auditNumber) || !auditNumber.All(char.IsAsciiDigit))
                throw new ArgumentException("Ongeldig Basware-auditnummer.", nameof(auditNumbers));
            candidates[$"order_{auditNumber}.xml"] = auditNumber;
            candidates[$"{auditNumber}.xml"] = auditNumber;
        }
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (candidates.Count == 0) return found;
        await using var cmd = dataSource.CreateCommand("""
            SELECT DISTINCT source_filename
            FROM public.edi_document
            WHERE source_filename = ANY($1);
            """);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            Value = candidates.Keys.ToArray()
        });
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
            if (candidates.TryGetValue(rows.GetString(0), out var auditNumber))
                found.Add(auditNumber);
        return found;
    }

    /// <summary>Voor UTF-8-bestanden zoals de aangeleverde Basware XML's.</summary>
    public async Task<EdiImportResult> ImportFileAsync(string path, CancellationToken ct = default)
    {
        var xml = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), ct);
        return await ImportXmlAsync(xml, Path.GetFileName(path), ct);
    }

    public async Task<EdiImportResult> ImportXmlAsync(string xml, string? filename = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 20_000_000
        });
        var root = XDocument.Load(reader).Root;
        if (root is null || root.Name.LocalName != "Document")
            throw new InvalidDataException("Verwacht een Basware Document, geen HTML of ander bestand.");
        var number = Required(root, "DocumentNumber");
        var orders = Many(root, "Order").ToArray();
        if (orders.Length == 0) throw new InvalidDataException("Order ontbreekt.");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // De unieke constraint beschermt ook tegen gelijktijdige dubbele imports.
        var documentId = await Insert("""
            INSERT INTO public.edi_document
              (document_number,document_date,sender_id,receiver_id,test_indicator,version,source_filename,raw_xml)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            ON CONFLICT (document_number) DO NOTHING RETURNING id;
            """, Text(number), Date(root,"DocumentDate"), Text(Value(root,"SenderID")),
            Text(Value(root,"ReceiverID")), Text(Value(root,"TestIndicator")),
            Text(Value(root,"Version")), Text(filename), Text(xml));

        if (documentId is null)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT id,raw_xml FROM public.edi_document WHERE document_number=$1 FOR UPDATE", connection, transaction);
            cmd.Parameters.Add(Text(number));
            long existingId;
            await using (var rows = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await rows.ReadAsync(ct)) throw new InvalidOperationException("Document gelijktijdig verwijderd; probeer opnieuw.");
                existingId = rows.GetInt64(0);
                if (rows.IsDBNull(1) || !string.Equals(rows.GetString(1), xml, StringComparison.Ordinal))
                    throw new InvalidDataException($"Document {number} bestaat met andere XML; niets overschreven.");
            }
            await transaction.CommitAsync(ct);
            return new(existingId, number, false) { OrderCount = orders.Length };
        }

        // Eén document met de volledige raw_xml; elke Order krijgt zijn eigen rij.
        // Alle orders worden samen gecommit, zodat het downloadfilter alleen volledige imports ziet.
        foreach (var order in orders)
        {
            ct.ThrowIfCancellationRequested();
            var header = One(order, "OrderHeader") ?? throw new InvalidDataException("OrderHeader ontbreekt.");
            var detail = One(order, "OrderDetail") ?? throw new InvalidDataException("OrderDetail ontbreekt.");
            var lines = Many(detail, "OrderItem").ToArray();
            if (lines.Length == 0) throw new InvalidDataException("Order bevat geen regels.");
            var dates = One(header, "Dates");

            var orderId = await Insert("""
                INSERT INTO public.edi_order
                  (document_id,message_reference_number,message_type,message_number,message_function,currency,order_date,requested_delivery_date)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8) RETURNING id;
                """, Id(documentId.Value), Text(Value(order,"MessageReferenceNumber")),
                Text(Value(header,"MessageType")), Text(Value(header,"MessageNumber")),
                Text(Value(header,"MessageFunction")), Text(Value(header,"OrderCurrency")),
                Date(dates,"OrderDate"), Date(dates,"RequestedDeliveryDate"));

            foreach (var partner in Many(One(header,"Partners"),"Partner"))
                await Insert("""
                    INSERT INTO public.edi_partner (order_id,partner_type,partner_id,partner_id_type,name)
                    VALUES ($1,$2,$3,$4,$5) RETURNING id;
                    """, Id(orderId!.Value), Text(Value(partner,"PartnerType")), Text(Value(partner,"PartnerID")),
                    Text(Value(partner,"PartnerIDType")), Text(Value(partner,"Name")));

            var seen = new HashSet<int>();
            foreach (var line in lines)
            {
                var value = Required(line,"LineItemNumber");
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber) || !seen.Add(lineNumber))
                    throw new InvalidDataException($"Ongeldig of dubbel LineItemNumber: {value}.");
                var lineId = await Insert("""
                    INSERT INTO public.edi_order_line (order_id,line_number,long_description)
                    VALUES ($1,$2,$3) RETURNING id;
                    """, Id(orderId!.Value), Param(NpgsqlDbType.Integer,lineNumber), Text(Value(line,"LongDescription")));
                foreach (var product in Many(One(line,"ProductIdentifications"),"ProductIdentification"))
                    await Insert("""
                        INSERT INTO public.edi_product_identification (order_line_id,function_code,product_type,product_number)
                        VALUES ($1,$2,$3,$4) RETURNING id;
                        """, Id(lineId!.Value), Text(Value(product,"Function")), Text(Value(product,"ProductType")), Text(Value(product,"ProductNumber")));
                foreach (var quantity in Many(One(line,"Quantities"),"Quantity"))
                    await Insert("""
                        INSERT INTO public.edi_quantity (order_line_id,quantity_type,amount)
                        VALUES ($1,$2,$3) RETURNING id;
                        """, Id(lineId!.Value), Text(Value(quantity,"QuantityType")), Amount(quantity));
            }
        }
        await transaction.CommitAsync(ct);
        return new(documentId.Value,number,true) { OrderCount = orders.Length };

        // Dispose van een niet-gecommitte transactie draait alle inserts terug.
        async Task<long?> Insert(string sql, params NpgsqlParameter[] parameters)
        {
            await using var cmd = new NpgsqlCommand(sql,connection,transaction);
            cmd.Parameters.AddRange(parameters);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is null or DBNull ? null : (long)result;
        }
    }

    // Ondersteunt XML zonder namespace of met één consistente namespace.
    private static IEnumerable<XElement> Many(XElement? parent,string name) =>
        parent?.Elements(parent.Name.Namespace + name) ?? Enumerable.Empty<XElement>();
    private static XElement? One(XElement? parent,string name)
    {
        var elements = Many(parent,name).Take(2).ToArray();
        if (elements.Length > 1) throw new InvalidDataException($"Meerdere {name}-elementen waar één verwacht wordt.");
        return elements.SingleOrDefault();
    }
    private static string? Value(XElement? parent,string name)
    {
        var element = One(parent,name);
        if (element?.HasElements == true) throw new InvalidDataException($"Verwacht tekst in {name}.");
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
    private static string Required(XElement parent,string name) =>
        Value(parent,name) ?? throw new InvalidDataException($"Verplicht veld {name} ontbreekt.");
    private static NpgsqlParameter Date(XElement? parent,string name)
    {
        var value = Value(parent,name);
        if (value is null) return Param(NpgsqlDbType.Timestamp,null);
        if (!DateTime.TryParseExact(value,"yyyyMMddHHmmss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))
            throw new InvalidDataException($"Ongeldige datum in {name}: {value}.");
        return Param(NpgsqlDbType.Timestamp,DateTime.SpecifyKind(date,DateTimeKind.Unspecified));
    }
    private static NpgsqlParameter Amount(XElement quantity)
    {
        var value = Value(quantity,"Amount");
        if (value is null) return Param(NpgsqlDbType.Numeric,null);
        if (!decimal.TryParse(value,NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)
            || amount <= -100_000_000_000_000m || amount >= 100_000_000_000_000m || decimal.Round(amount,4) != amount)
            throw new InvalidDataException($"Amount past niet in DECIMAL(18,4): {value}.");
        return Param(NpgsqlDbType.Numeric,amount);
    }
    private static NpgsqlParameter Param(NpgsqlDbType type,object? value) => new() { NpgsqlDbType=type,Value=value ?? DBNull.Value };
    private static NpgsqlParameter Text(string? value) => Param(NpgsqlDbType.Text,value);
    private static NpgsqlParameter Id(long value) => Param(NpgsqlDbType.Bigint,value);
}
