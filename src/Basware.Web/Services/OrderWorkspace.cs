using WpfAppBaswareLogin.Services;
namespace Basware.Web.Services;
public sealed class DatabaseSettings(IConfiguration config)
{
    public string? ConnectionString => config.GetConnectionString("Basware") is { Length: > 0 } value ? value : config["EDI_CONNECTION_STRING"];
    public bool Configured => !string.IsNullOrWhiteSpace(ConnectionString);
}
// Scoped to a Blazor circuit: selections and print edits are never shared between users.
public sealed class OrderWorkspace
{
    public HashSet<long> Selected { get; } = [];
    public Dictionary<long, SalesOrderPrintData> PrintEdits { get; } = [];
}
