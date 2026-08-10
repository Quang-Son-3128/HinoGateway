namespace HinoGateway.Core;

/// <summary>
/// The result of reading one tag during a poll: which tag, and its freshly sampled value.
/// A driver's <c>PollAsync</c> returns a batch of these.
/// </summary>
public sealed record TagRead(string TagId, TagValue Value);
