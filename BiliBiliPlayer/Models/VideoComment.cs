using System.Text.Json.Serialization;

namespace BiliBiliPlayer.Models;

public sealed class VideoCommentSnapshot
{
    [JsonPropertyName("total")]
    public int TotalCount { get; init; }

    [JsonPropertyName("hot")]
    public IReadOnlyList<VideoComment> Hot { get; init; } = Array.Empty<VideoComment>();

    [JsonPropertyName("latest")]
    public IReadOnlyList<VideoComment> Latest { get; init; } = Array.Empty<VideoComment>();

    [JsonPropertyName("hotError")]
    public string HotError { get; init; } = string.Empty;

    [JsonPropertyName("latestError")]
    public string LatestError { get; init; } = string.Empty;

    [JsonPropertyName("hotPaging")]
    public VideoCommentPaging HotPaging { get; init; } = new();

    [JsonPropertyName("latestPaging")]
    public VideoCommentPaging LatestPaging { get; init; } = new();
}

public sealed class VideoCommentPaging
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = "main";

    [JsonPropertyName("next")]
    public string Next { get; init; } = "0";

    [JsonPropertyName("page")]
    public int PageNumber { get; init; } = 1;

    [JsonPropertyName("end")]
    public bool IsEnd { get; init; } = true;
}

public sealed class VideoCommentPage
{
    [JsonPropertyName("items")]
    public IReadOnlyList<VideoComment> Items { get; init; } = Array.Empty<VideoComment>();

    [JsonPropertyName("total")]
    public int TotalCount { get; init; }

    [JsonPropertyName("paging")]
    public VideoCommentPaging Paging { get; init; } = new();

    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;
}

public sealed class VideoComment
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("n")]
    public string UserName { get; init; } = string.Empty;

    [JsonPropertyName("a")]
    public string AvatarUrl { get; init; } = string.Empty;

    [JsonPropertyName("m")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("t")]
    public long CreatedAt { get; init; }

    [JsonPropertyName("l")]
    public long LikeCount { get; init; }

    [JsonPropertyName("r")]
    public int ReplyCount { get; init; }

    [JsonPropertyName("p")]
    public IReadOnlyList<string> PictureUrls { get; init; } = Array.Empty<string>();

    [JsonPropertyName("e")]
    public IReadOnlyDictionary<string, string> Emotes { get; init; } =
        new Dictionary<string, string>();

    [JsonPropertyName("u")]
    public bool IsUploader { get; init; }

    [JsonPropertyName("c")]
    public IReadOnlyList<VideoComment> Replies { get; init; } = Array.Empty<VideoComment>();
}
