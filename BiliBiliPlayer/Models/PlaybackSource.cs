using System.Text.Json.Serialization;

namespace BiliBiliPlayer.Models;

public sealed class DanmakuComment
{
    [JsonPropertyName("t")]
    public double Time { get; init; }

    [JsonPropertyName("m")]
    public int Mode { get; init; }

    [JsonPropertyName("c")]
    public int Color { get; init; }

    [JsonPropertyName("x")]
    public string Text { get; init; } = string.Empty;
}

public sealed class PlaybackSource
{
    public string VideoUrl { get; init; } = string.Empty;

    public string? AudioUrl { get; init; }

    public IReadOnlyList<string> AudioUrls { get; init; } = Array.Empty<string>();

    public int RequestedQuality { get; init; }

    public int ActualQuality { get; init; }

    public string VideoCodec { get; init; } = string.Empty;

    public IReadOnlyList<int> AvailableQualities { get; init; } = Array.Empty<int>();

    public bool IsDash => !string.IsNullOrWhiteSpace(AudioUrl);

    public string QualityLabel => BiliQualityNames.GetLabel(ActualQuality);
}

/// <summary>
/// A snapshot of all playable qualities returned by one Bilibili playurl response.
/// The signed CDN URLs are intentionally kept only in memory for the lifetime of PlayerPage.
/// </summary>
public sealed class PlaybackManifest
{
    public long Aid { get; init; }

    public long OwnerMid { get; init; }

    public IReadOnlyDictionary<int, PlaybackSource> Sources { get; init; } =
        new Dictionary<int, PlaybackSource>();

    public IReadOnlyList<int> AvailableQualities { get; init; } = Array.Empty<int>();

    public IReadOnlyList<DanmakuComment> Danmaku { get; init; } = Array.Empty<DanmakuComment>();

    public VideoCommentSnapshot Comments { get; init; } = new();

    public int GetHighestAvailableQuality(int ceiling = int.MaxValue)
    {
        var quality = Sources.Keys
            .Where(value => value > 0 && value <= ceiling)
            .DefaultIfEmpty(0)
            .Max();

        if (quality > 0)
        {
            return quality;
        }

        return Sources.Keys.DefaultIfEmpty(0).Max();
    }

    public PlaybackSource GetBestMatch(int requestedQuality)
    {
        if (Sources.Count == 0)
        {
            throw new InvalidOperationException("播放清单中没有可用的视频轨。");
        }

        PlaybackSource? selected = null;

        if (Sources.TryGetValue(requestedQuality, out var exact))
        {
            selected = exact;
        }
        else
        {
            selected = Sources
                .Where(pair => pair.Key <= requestedQuality)
                .OrderByDescending(pair => pair.Key)
                .Select(pair => pair.Value)
                .FirstOrDefault()
                ?? Sources
                    .OrderBy(pair => pair.Key)
                    .Select(pair => pair.Value)
                    .First();
        }

        return new PlaybackSource
        {
            VideoUrl = selected.VideoUrl,
            AudioUrl = selected.AudioUrl,
            AudioUrls = selected.AudioUrls,
            RequestedQuality = requestedQuality,
            ActualQuality = selected.ActualQuality,
            VideoCodec = selected.VideoCodec,
            AvailableQualities = AvailableQualities
        };
    }
}

public static class BiliQualityNames
{
    public static string GetLabel(int quality) => quality switch
    {
        6 => "240P",
        16 => "360P",
        32 => "480P",
        64 => "720P",
        74 => "720P60",
        80 => "1080P",
        112 => "1080P+",
        116 => "1080P60",
        120 => "4K",
        125 => "HDR",
        126 => "杜比视界",
        127 => "8K",
        _ => quality > 0 ? $"QN {quality}" : "自动"
    };
}
