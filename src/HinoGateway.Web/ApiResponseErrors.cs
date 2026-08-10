using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HinoGateway.Web;

/// <summary>
/// Turns what the API returned into something worth showing an operator.
///
/// <para>
/// ⚠ <b>Reconstructed on 2026-08-10</b> after the source tree was lost; the two members are the ones
/// every page calls.
/// </para>
///
/// <para>
/// The validation endpoints answer with a JSON array of messages, the guards with a plain string,
/// and a failure inside ASP.NET Core with a ProblemDetails object. A page that simply printed the
/// raw body would show an operator <c>["Tag 'x' has an empty address."]</c>, brackets and quotes
/// included, so the unwrapping lives here once instead of in each page.
/// </para>
/// </summary>
public static class ApiResponseErrors
{
    /// <summary>
    /// Was this failure "you have no key / the wrong key"? Those are reported centrally by the
    /// layout, which asks for a key — a page that also showed its own error would say the same thing
    /// twice and bury the button that fixes it.
    /// </summary>
    public static bool IsUnauthorized(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

    /// <summary>The message to show for a failed response, unwrapped from whichever shape it used.</summary>
    public static async Task<string> ReadAsync(HttpResponseMessage response)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return $"{(int)response.StatusCode} {response.ReasonPhrase} ({ex.Message})";
        }

        if (string.IsNullOrWhiteSpace(body))
            return $"{(int)response.StatusCode} {response.ReasonPhrase}";

        try
        {
            using var document = JsonDocument.Parse(body);
            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.Array:
                    var messages = document.RootElement.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                        .Where(m => !string.IsNullOrWhiteSpace(m));
                    return string.Join(" · ", messages!);

                case JsonValueKind.String:
                    return document.RootElement.GetString() ?? body;

                case JsonValueKind.Object:
                    // ProblemDetails, or a validation dictionary.
                    if (document.RootElement.TryGetProperty("detail", out var detail) &&
                        detail.ValueKind == JsonValueKind.String)
                        return detail.GetString() ?? body;
                    if (document.RootElement.TryGetProperty("title", out var title) &&
                        title.ValueKind == JsonValueKind.String)
                        return title.GetString() ?? body;
                    return body;
            }
        }
        catch (JsonException)
        {
            // A plain-text body: that is already the message.
        }

        return body.Trim();
    }
}
