using MigrationDashboard.Web.Components;
using MigrationDashboard.Web.Endpoints;
using MigrationDashboard.Web.Models;
using MigrationDashboard.Web.Services;
using DevExpress.Blazor;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
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
        options.LoginPath = "/dashboard";
        options.LogoutPath = "/editor/logout";
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
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

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapEditorAuthenticationEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
