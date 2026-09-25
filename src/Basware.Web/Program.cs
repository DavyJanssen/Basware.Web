using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Basware.Web.Components;
using Basware.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using WpfAppBaswareLogin.Services;
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => {
    o.LoginPath = "/login"; o.Cookie.Name = "Basware.Session";
    o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var keys = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keys)) builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("Basware.Web");
builder.Services.Configure<ForwardedHeadersOptions>(o => {
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (System.Net.IPAddress.TryParse(builder.Configuration["ReverseProxy:Address"], out var address)) o.KnownProxies.Add(address);
});
builder.Services.AddSingleton<DatabaseSettings>();
builder.Services.AddSingleton<NpgsqlDataSource>(sp => NpgsqlDataSource.Create(sp.GetRequiredService<DatabaseSettings>().ConnectionString
    ?? throw new InvalidOperationException("Database niet ingesteld. Configureer ConnectionStrings__Basware.")));
builder.Services.AddScoped<EdiOrderRepository>();
builder.Services.AddScoped<EdiXmlImporter>();
builder.Services.AddScoped<LaboratoryCatalogService>();
builder.Services.AddScoped<OrderWorkspace>();
var app = builder.Build();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Error", createScopeForErrors: true); app.UseHsts(); app.UseHttpsRedirection(); }
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.UseAntiforgery();
app.MapPost("/auth/login", async (HttpContext context, IAntiforgery csrf, IConfiguration config) => {
    try { await csrf.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    var form = await context.Request.ReadFormAsync();
    var expected = config["Authentication:Password"];
    var username = config["Authentication:Username"] ?? "admin";
    var valid = !string.IsNullOrWhiteSpace(expected) && expected.Length >= 16
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(form["password"].ToString())), SHA256.HashData(Encoding.UTF8.GetBytes(expected)))
        && string.Equals(form["username"], username, StringComparison.Ordinal);
    if (!valid) return Results.LocalRedirect("/login?failed=true");
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.LocalRedirect("/");
}).RequireRateLimiting("login");
app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery csrf) => {
    try { await csrf.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    await context.SignOutAsync(); return Results.LocalRedirect("/login");
}).RequireAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
