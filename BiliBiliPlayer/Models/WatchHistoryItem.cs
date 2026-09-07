namespace BiliBiliPlayer.Models;

public sealed class WatchHistoryItem
{
    public string Bvid { get; init; } = string.Empty;

    public long Aid { get; init; }

    public long Cid { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Cover { get; init; } = string.Empty;

    public string AuthorName { get; init; } = string.Empty;

    public string AuthorFace { get; init; } = string.Empty;

    public long AuthorMid { get; init; }

    public int Duration { get; init; }

    public int Progress { get; init; }

    public long ViewedAt { get; init; }

    public string CoverUrl => NormalizeHttps(Cover);

    public string DurationText => FormatDuration(Duration);

    public string ProgressText
    {
        get
        {
            if (Progress < 0 || (Duration > 0 && Progress >= Duration - 2))
            {
                return "已看完";
            }

            if (Progress <= 0)
            {
                return "刚刚开始";
            }

            return Duration > 0
                ? $"看到 {FormatDuration(Math.Min(Progress, Duration))}"
                : $"看到 {FormatDuration(Progress)}";
        }
    }

    public string ViewedAtText
    {
        get
        {
            if (ViewedAt <= 0)
            {
                return "最近观看";
            }

            try
            {
                var viewed = DateTimeOffset.FromUnixTimeSeconds(ViewedAt).ToLocalTime();
                var now = DateTimeOffset.Now;
                if (viewed.Date == now.Date)
                {
                    return $"今天 {viewed:HH:mm}";
                }

                if (viewed.Date == now.Date.AddDays(-1))
                {
                    return $"昨天 {viewed:HH:mm}";
                }

                return viewed.Year == now.Year
                    ? viewed.ToString("MM-dd HH:mm")
                    : viewed.ToString("yyyy-MM-dd");
            }
            catch (ArgumentOutOfRangeException)
            {
                return "最近观看";
            }
        }
    }

    public VideoItem ToVideoItem() => new()
    {
        Bvid = Bvid,
        Picture = Cover,
        Title = Title,
        Duration = Duration,
        Owner = new VideoOwner
        {
            Mid = AuthorMid,
            Name = AuthorName,
            Face = AuthorFace
        },
        RecommendationReason = new RecommendationReason { Content = "观看历史" }
    };

    private static string NormalizeHttps(string value) =>
        value.StartsWith("//", StringComparison.Ordinal)
            ? $"https:{value}"
            : value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? $"https://{value[7..]}"
                : value;

    private static string FormatDuration(int seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }
}

public sealed record WatchHistoryCursor(long Max, long ViewAt, string Business);

public sealed class WatchHistoryPage
{
    public IReadOnlyList<WatchHistoryItem> Items { get; init; } = [];

    public WatchHistoryCursor? NextCursor { get; init; }

    public bool HasMore { get; init; }
}
