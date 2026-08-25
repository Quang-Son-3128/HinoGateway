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
                if (WorthAuditing(context))
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

    /// <summary>
    /// A POST that changes nothing is not an audit event.
    ///
    /// <para>
    /// The audit trail is durable and synchronous by design: <see cref="AuditTrail.WriteAsync"/>
    /// holds the caller until its batch has reached the disk, over a bounded channel that waits when
    /// it is full. That is exactly right for a PLC write. It is exactly wrong for the formula
    /// editor's checking endpoints, which are POSTs only because a formula does not fit in a query
    /// string — they compile some text and answer, storing nothing and writing to no device. Every
    /// keystroke in the editor was costing a flush to disk, and since both sit at Viewer level, a
    /// keyless gateway on a plant LAN gave anyone who could reach the port a way to make it flush as
    /// fast as they could send: request threads parked behind the disk, and the audit files growing
    /// towards the two gigabytes Audit:MaxTotalBytes allows, full of nothing that happened.
    /// </para>
    ///
    /// <para>
    /// Rejected requests need no mention here. Authorisation runs earlier in the pipeline and
    /// short-circuits, so a 401 or a 403 never reaches this middleware at all; the record of someone
    /// probing the gateway is kept by <c>GatewayApiKeyHandler</c> and <c>AuthFailureTracker</c>,
    /// which is where it belongs.
    /// </para>
    /// </summary>
    private static bool WorthAuditing(HttpContext context) =>
        !context.Request.Path.StartsWithSegments("/api/expressions", StringComparison.OrdinalIgnoreCase);
}
