using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using HinoGateway.Web;
using MudBlazor.Services;
using HinoGateway.Web.Security;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<ApiKeyStore>();
builder.Services.AddScoped<ApiKeyHttpHandler>();
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<ApiKeyHttpHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
});
builder.Services.AddMudServices();

await builder.Build().RunAsync();
