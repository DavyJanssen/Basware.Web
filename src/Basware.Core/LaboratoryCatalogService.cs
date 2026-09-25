using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace WpfAppBaswareLogin.Services;

public sealed record LaboratoryImportResult(int Entries, int Added, int Unchanged, int Conflicts);

public static class CnkNumber
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var cnk = value.Trim();
        if (cnk.Length == 8 && cnk[4] == '-') cnk = cnk.Remove(4, 1);
        if (cnk.Length != 7 || !cnk.All(char.IsAsciiDigit))
            throw new ArgumentException("Een CNK bestaat uit 7 cijfers, bijvoorbeeld 1361211 of 1361-211.");
        return cnk;
    }
}

/// <summary>Beheert uitsluitend expliciete CNK-labokoppelingen; nooit afleidingen.</summary>
public sealed class LaboratoryCatalogService(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<string>> GetLaboratoryNamesAsync(CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT name FROM public.edi_laboratory ORDER BY name");
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await rows.ReadAsync(ct)) names.Add(rows.GetString(0));
        return names;
    }

    public async Task<string?> GetMappingAsync(string cnk, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT lab.name FROM public.edi_cnk_laboratory mapping
            JOIN public.edi_laboratory lab ON lab.id = mapping.laboratory_id
            WHERE mapping.cnk = $1;
            """);
        cmd.Parameters.AddWithValue(CnkNumber.Normalize(cnk));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SaveMappingAsync(string cnk, string laboratoryName, CancellationToken ct = default)
    {
        cnk = CnkNumber.Normalize(cnk);
        laboratoryName = ValidateName(laboratoryName);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var lab = new NpgsqlCommand("""
            INSERT INTO public.edi_laboratory(name) VALUES ($1)
            ON CONFLICT(name) DO UPDATE SET name = EXCLUDED.name RETURNING id;
            """, connection, transaction);
        lab.Parameters.AddWithValue(laboratoryName);
        var labId = (long)(await lab.ExecuteScalarAsync(ct))!;
        await using var mapping = new NpgsqlCommand("""
            INSERT INTO public.edi_cnk_laboratory(cnk, laboratory_id) VALUES ($1,$2)
            ON CONFLICT(cnk) DO UPDATE SET laboratory_id = EXCLUDED.laboratory_id,
                source_filename = NULL, updated_at = CURRENT_TIMESTAMP;
            """, connection, transaction);
        mapping.Parameters.AddWithValue(cnk);
        mapping.Parameters.AddWithValue(labId);
        await mapping.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Voegt JSON-koppelingen atomair toe. Bestaande afwijkende koppelingen
    /// blijven behouden en worden als conflict geteld; handmatige correcties gaan niet verloren.</summary>
    public async Task<LaboratoryImportResult> ImportJsonAsync(string path, CancellationToken ct = default)
    {
        using var input = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(input, cancellationToken: ct);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Verwacht een JSON-object met CNK als sleutel en labonaam als waarde.");
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in json.RootElement.EnumerateObject())
        {
            var cnk = CnkNumber.Normalize(property.Name);
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"De labonaam bij {property.Name} is geen tekst.");
            var name = ValidateName(property.Value.GetString()!);
            if (entries.TryGetValue(cnk, out var previous) && previous != name)
                throw new InvalidDataException($"CNK {cnk} heeft meerdere labonamen in dit bestand.");
            entries[cnk] = name;
        }
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var create = new NpgsqlCommand(
            "CREATE TEMP TABLE laboratory_import_stage(cnk text PRIMARY KEY, name text NOT NULL) ON COMMIT DROP",
            connection, transaction))
            await create.ExecuteNonQueryAsync(ct);
        await using (var copy = await connection.BeginBinaryImportAsync(
            "COPY laboratory_import_stage(cnk,name) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var (cnk, name) in entries)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(cnk, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(name, NpgsqlDbType.Text, ct);
            }
            await copy.CompleteAsync(ct);
        }
        await using (var labs = new NpgsqlCommand("""
            INSERT INTO public.edi_laboratory(name)
            SELECT DISTINCT name FROM laboratory_import_stage ORDER BY name
            ON CONFLICT(name) DO NOTHING;
            """, connection, transaction))
            await labs.ExecuteNonQueryAsync(ct);
        int added;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO public.edi_cnk_laboratory(cnk,laboratory_id,source_filename)
            SELECT stage.cnk, lab.id, $1
            FROM laboratory_import_stage stage JOIN public.edi_laboratory lab ON lab.name=stage.name
            ORDER BY stage.cnk ON CONFLICT(cnk) DO NOTHING;
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(Path.GetFileName(path));
            added = await insert.ExecuteNonQueryAsync(ct);
        }
        int conflicts;
        await using (var count = new NpgsqlCommand("""
            SELECT count(*)::integer FROM laboratory_import_stage stage
            JOIN public.edi_cnk_laboratory mapping ON mapping.cnk=stage.cnk
            JOIN public.edi_laboratory lab ON lab.id=mapping.laboratory_id
            WHERE lab.name <> stage.name;
            """, connection, transaction))
            conflicts = (int)(await count.ExecuteScalarAsync(ct))!;
        await transaction.CommitAsync(ct);
        return new(entries.Count, added, entries.Count - added - conflicts, conflicts);
    }

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (name.Length > 255) throw new ArgumentException("De labonaam mag maximaal 255 tekens bevatten.");
        return name;
    }
}
