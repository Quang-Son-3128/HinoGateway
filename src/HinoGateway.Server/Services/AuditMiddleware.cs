using System.Diagnostics;

namespace HinoGateway.Server.Services;

public sealed class AuditMiddleware(RequestDelegate next, ILogger<AuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, AuditTrail audit)
    {
        if (context.Request.Method is not ("POST" or "PUT" or "PATCH" or "DELETE")
            || context.Request.Path.Equals("/api/tags/write", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.StartNew();
        Exception? failure = null;
        try { await next(context); }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            try
            {
                await audit.WriteAsync(new GatewayAuditEvent(DateTimeOffset.UtcNow,
                    $"http.{context.Request.Method.ToLowerInvariant()}",
                    context.User.Identity?.Name ?? "unknown", "REST",
                    context.Connection.RemoteIpAddress?.ToString(), context.Request.Path,
                    failure is null && context.Response.StatusCode < 400 ? "Successful" : "Failed",
                    context.TraceIdentifier, Reason: failure?.Message ?? $"HTTP {context.Response.StatusCode}",
                    ElapsedMs: started.ElapsedMilliseconds));
            }
            catch (Exception ex) { logger.LogError(ex, "Failed to audit {Method} {Path}", context.Request.Method, context.Request.Path); }
        }
    }
}
