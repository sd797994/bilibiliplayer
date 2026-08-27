using System.Collections.Concurrent;

namespace BiliBiliPlayer.Services;

/// <summary>
/// Keeps playback progress for the lifetime of the current application process only.
/// Nothing in this store is written to Preferences, a database, or the file system.
/// </summary>
public static class PlaybackProgressStore
{
    private const double RestartThresholdSeconds = 3d;
    private const double CompletionThresholdSeconds = 10d;

    private static readonly ConcurrentDictionary<string, double> Positions =
        new(StringComparer.OrdinalIgnoreCase);

    public static double GetPosition(string bvid)
    {
        if (string.IsNullOrWhiteSpace(bvid))
        {
            return 0d;
        }

        return Positions.TryGetValue(bvid, out var position)
            ? Math.Max(0d, position)
            : 0d;
    }

    public static void SavePosition(
        string bvid,
        double position,
        double duration,
        bool ended)
    {
        if (string.IsNullOrWhiteSpace(bvid) ||
            !double.IsFinite(position) ||
            !double.IsFinite(duration))
        {
            return;
        }

        position = Math.Max(0d, position);
        duration = Math.Max(0d, duration);

        // A zero duration means the media never became ready. Preserve an earlier useful
        // checkpoint instead of replacing it because the user closed during resolution.
        if (duration <= 0d)
        {
            if (position >= RestartThresholdSeconds)
            {
                Positions[bvid] = position;
            }

            return;
        }

        var reachedEnd = ended || duration - position <= CompletionThresholdSeconds;
        if (position < RestartThresholdSeconds || reachedEnd)
        {
            Positions.TryRemove(bvid, out _);
            return;
        }

        Positions[bvid] = Math.Min(position, duration);
    }
}
