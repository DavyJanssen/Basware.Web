using Microsoft.AspNetCore.Components;
using Basware.Web.Services;
namespace Basware.Web.Components.Shared;
public abstract class OperationPage : ComponentBase, IDisposable
{
    [Inject] protected IServiceProvider Services { get; set; } = default!;
    [Inject] protected DatabaseSettings Database { get; set; } = default!;
    [Inject] protected ILogger<OperationPage> Logger { get; set; } = default!;
    protected CancellationTokenSource Lifetime { get; } = new();
    protected bool Busy;
    protected string? Error;
    protected async Task Run(Func<Task> action)
    {
        if (Busy) return;
        Busy = true; Error = null;
        try { await action(); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.Xml.XmlException or System.Text.Json.JsonException)
        { Error = "Controleer het bestand of de ingevoerde waarden. " + (ex is ArgumentException ? ex.Message : "Het formaat is niet geldig."); }
        catch (Exception ex) { Logger.LogError(ex, "Basware operation failed"); Error = "De bewerking is niet gelukt. Controleer de databaseverbinding en probeer opnieuw. De technische details staan in het serverlog."; }
        finally { Busy = false; }
    }
    public void Dispose() { Lifetime.Cancel(); Lifetime.Dispose(); GC.SuppressFinalize(this); }
}
