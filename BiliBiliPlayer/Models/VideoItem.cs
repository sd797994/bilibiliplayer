using System.Text.Json.Serialization;

namespace BiliBiliPlayer.Models;

public sealed class VideoItem
{
    [JsonPropertyName("bvid")]
    public string Bvid { get; set; } = string.Empty;

    [JsonPropertyName("pic")]
    public string Picture { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("owner")]
    public VideoOwner Owner { get; set; } = new();

    [JsonPropertyName("stat")]
    public VideoStatistics Statistics { get; set; } = new();

    [JsonPropertyName("rcmd_reason")]
    public RecommendationReason? RecommendationReason { get; set; }

    public string CoverUrl => NormalizeHttps(Picture);

    public string OwnerName => Owner.Name;

    public string OwnerFaceUrl => NormalizeHttps(Owner.Face);

    public string ViewCountText => FormatCount(Statistics.View);

    public string DanmakuCountText => FormatCount(Statistics.Danmaku);

    public string DurationText
    {
        get
        {
            var duration = TimeSpan.FromSeconds(Math.Max(0, Duration));
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes}:{duration.Seconds:00}";
        }
    }

    public string ReasonText => string.IsNullOrWhiteSpace(RecommendationReason?.Content)
        ? "热门推荐"
        : RecommendationReason.Content;

    private static string NormalizeHttps(string value) =>
        value.StartsWith("//", StringComparison.Ordinal)
            ? $"https:{value}"
            : value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? $"https://{value[7..]}"
            : value;

    private static string FormatCount(long count)
    {
        if (count >= 100_000_000)
        {
            return $"{count / 100_000_000d:0.#}亿";
        }

        if (count >= 10_000)
        {
            return $"{count / 10_000d:0.#}万";
        }

        return count.ToString("N0");
    }
}

public sealed class VideoOwner
{
    [JsonPropertyName("mid")]
    public long Mid { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("face")]
    public string Face { get; set; } = string.Empty;
}

public sealed class VideoStatistics
{
    [JsonPropertyName("view")]
    public long View { get; set; }

    [JsonPropertyName("danmaku")]
    public long Danmaku { get; set; }
}

public sealed class RecommendationReason
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
