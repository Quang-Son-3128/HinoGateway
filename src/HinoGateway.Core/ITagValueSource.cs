namespace HinoGateway.Core;

/// <summary>
/// Read-only access to the gateway's current tag values.
///
/// <para>
/// The tag store itself lives in the engine, and everything that produces values has so far had no
/// reason to look at what other tags hold — a Modbus driver reads registers, not tags. Expression
/// tags are the first producer whose input <em>is</em> the tag database, and this is the whole of
/// what they need: no subscription, no history, no writing. Keeping it to one method means the
/// expression evaluator can be tested against a dictionary, and means nothing that computes a value
/// is able to reach in and change one.
/// </para>
/// </summary>
public interface ITagValueSource
{
    /// <summary>
    /// The latest value of <paramref name="tagId"/> (a runtime tag id — the full folder path).
    /// False when no such tag exists <em>or has ever been read</em>; the two are indistinguishable
    /// here on purpose, because a caller can do nothing different about them. Configuration-time
    /// validation is where a reference to a tag that does not exist gets reported.
    /// </summary>
    bool TryGet(string tagId, out TagValue value);
}
