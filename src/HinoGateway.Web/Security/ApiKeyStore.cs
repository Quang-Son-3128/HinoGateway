using Microsoft.JSInterop;

namespace HinoGateway.Web.Security;

/// <summary>Browser-session credential; closing the tab removes it.</summary>
public sealed class ApiKeyStore(IJSRuntime js)
{
    private const string Key = "hino-api-key";
    public async Task<string?> GetAsync()
    {
        try { return await js.InvokeAsync<string?>("sessionStorage.getItem", Key); }
        catch { return null; }
    }
    public async Task SetAsync(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) await js.InvokeVoidAsync("sessionStorage.removeItem", Key);
        else await js.InvokeVoidAsync("sessionStorage.setItem", Key, value.Trim());
    }
}

public sealed class ApiKeyHttpHandler(ApiKeyStore keys) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = await keys.GetAsync();
        if (!string.IsNullOrEmpty(key)) request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        return await base.SendAsync(request, ct);
    }
}
