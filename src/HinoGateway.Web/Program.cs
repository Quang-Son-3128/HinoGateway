using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using HinoGateway.Web;
using MudBlazor.Services;
using HinoGateway.Web.Security;
using HinoGateway.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<ApiKeyStore>();
builder.Services.AddScoped<ApiKeyHttpHandler>();
builder.Services.AddScoped<GatewayConnection>();
builder.Services.AddScoped(sp =>
{
    // Outermost, so it sees the outcome of every request the application makes without any page
    // having to opt in. It only observes; the exception or status a page would have received is
    // exactly what it still receives.
    var connection = new ConnectionHttpHandler(sp.GetRequiredService<GatewayConnection>());
    var apiKey = sp.GetRequiredService<ApiKeyHttpHandler>();
    apiKey.InnerHandler = new HttpClientHandler();
    connection.InnerHandler = apiKey;
    return new HttpClient(connection) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
});
builder.Services.AddScoped<EngineApplyCoordinator>();
builder.Services.AddMudServices();

await builder.Build().RunAsync();
