namespace HinoGateway.Web.Services;

/// <summary>
/// Reports the outcome of every API call to <see cref="GatewayConnection"/>, then gets out of the way.
///
/// <para>
/// It deliberately does not retry and does not swallow: a page still sees exactly the exception or
/// status it would have seen, so nothing about existing error handling changes. All this adds is the
/// one fact no page was in a position to know — whether the gateway as a whole is still answering.
/// </para>
/// </summary>
public sealed class ConnectionHttpHandler(GatewayConnection connection) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (GatewayConnection.IsUnavailable(response.StatusCode))
                connection.RecordFailure($"{(int)response.StatusCode} {response.ReasonPhrase}");
            else
                connection.RecordSuccess();
            return response;
        }
        catch (Exception ex) when (GatewayConnection.IsTransportFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            connection.RecordFailure(ex.Message);
            throw;
        }
    }
}
