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

    private static readonly ConcurrentDictionary<(string Bvid, long Cid), double> Positions = new();
    private static readonly ConcurrentDictionary<string, int> LastPages = new(StringComparer.Ordinal);

    public static int GetPageNumber(string bvid) =>
        !string.IsNullOrWhiteSpace(bvid) && LastPages.TryGetValue(bvid, out var page) ? page : 1;

    public static double GetPosition(string bvid, long cid)
    {
        if (string.IsNullOrWhiteSpace(bvid) || cid <= 0)
        {
            return 0d;
        }

        return Positions.TryGetValue((bvid, cid), out var position)
            ? Math.Max(0d, position)
            : 0d;
    }

    public static void SavePosition(
        string bvid,
        long cid,
        int pageNumber,
        double position,
        double duration,
        bool ended)
    {
        if (string.IsNullOrWhiteSpace(bvid) || cid <= 0 || pageNumber <= 0 ||
            !double.IsFinite(position) ||
            !double.IsFinite(duration))
        {
            return;
        }

        position = Math.Max(0d, position);
        duration = Math.Max(0d, duration);
        var key = (bvid, cid);
        LastPages[bvid] = pageNumber;

        // A zero duration means the media never became ready. Preserve an earlier useful
        // checkpoint instead of replacing it because the user closed during resolution.
        if (duration <= 0d)
        {
            if (position >= RestartThresholdSeconds)
            {
                Positions[key] = position;
            }

            return;
        }

        var reachedEnd = ended || duration - position <= CompletionThresholdSeconds;
        if (position < RestartThresholdSeconds || reachedEnd)
        {
            Positions.TryRemove(key, out _);
            return;
        }

        Positions[key] = Math.Min(position, duration);
    }
}
