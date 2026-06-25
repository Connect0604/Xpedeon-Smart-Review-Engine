using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartReviewSystem;
using SmartReviewSystem.Services.DevOps;
using SmartReviewSystem.Services.Ollama;
using SmartReviewSystem.Services.Orchestration;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IAzureDevOpsService, AzureDevOpsService>();
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

await builder.Build().RunAsync();
