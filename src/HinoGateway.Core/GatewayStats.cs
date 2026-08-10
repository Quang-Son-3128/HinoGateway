namespace HinoGateway.Core;

/// <summary>
/// The whole gateway in eight numbers, for the status bar that every page shows.
///
/// <para>
/// It exists because the alternative — the UI fetching every tag value every few seconds just to
/// count them — costs 1.8 MB per poll per open tab on a 10,000-tag project. The size of this
/// response does not depend on how many tags are configured, which is the entire point.
/// </para>
///
/// <para>⚠ Reconstructed 2026-08-10; field names verified against a captured <c>/api/stats</c>
/// response from the running gateway.</para>
/// </summary>
public sealed record GatewayStats(
    int DevicesTotal,
    int DevicesConnected,
    int TagsTotal,
    int TagsGood,
    int TagsUncertain,
    int TagsBad,
    int AlarmsActive,
    int AlarmsUnacknowledged);
