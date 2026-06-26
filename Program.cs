using MigrationDashboard.Web.Endpoints;
using MigrationDashboard.Web.Models;
using MigrationDashboard.Web.Services;
using DevExpress.Blazor;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Identity;
using SmartReviewSystem;
using SmartReviewSystem.Services.DevOps;
using SmartReviewSystem.Services.Ollama;
using SmartReviewSystem.Services.Orchestration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "MigrationDashboard.Editor";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/migration-dashboard";
        options.LogoutPath = "/editor/logout";
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

builder.Services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(3);
});

builder.Services.AddScoped(_ => new HttpClient());
builder.Services.AddScoped<DevOpsDashboardState>();
builder.Services.AddHttpClient<IOllamaService, OllamaService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddScoped<ConfigRoutingStrategy>();
builder.Services.AddHttpClient<LlmRoutingStrategy>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient<IPocoDbComparisonService, PocoDbComparisonService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddHttpClient<IDevExpressPropertyValidationService, DevExpressPropertyValidationService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddScoped<ReviewOrchestrator>();
builder.Services.AddSingleton<IEditSessionRegistry>(_ => new EditSessionRegistry(TimeProvider.System));
builder.Services.AddScoped<CircuitContextAccessor>();
builder.Services.AddScoped<ICircuitContextAccessor>(serviceProvider => serviceProvider.GetRequiredService<CircuitContextAccessor>());
builder.Services.AddScoped<CircuitHandler, EditSessionCircuitHandler>();
builder.Services.AddScoped<IEditorAuthenticationService, EditorAuthenticationService>();
builder.Services.AddScoped<IEditorIdentityAccessor>(serviceProvider =>
    (EditorAuthenticationService)serviceProvider.GetRequiredService<IEditorAuthenticationService>());
builder.Services.AddScoped<PasswordHasher<EditorAppUser>>();
builder.Services.AddDevExpressBlazor();
builder.Services.AddSingleton(new DashboardGridOptions());
builder.Services.AddScoped<IMigrationDashboardRepository, SqlMigrationDashboardRepository>();
builder.Services.AddScoped<IMigrationDashboardService, MigrationDashboardService>();
builder.Services.AddScoped<ISessionEditorContext, SessionEditorContext>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapEditorAuthenticationEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
