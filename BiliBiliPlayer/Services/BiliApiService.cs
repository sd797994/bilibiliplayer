using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BiliBiliPlayer.Models;

namespace BiliBiliPlayer.Services;

public sealed class BiliApiService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BiliApiService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bilibili.com/"),
            Timeout = TimeSpan.FromSeconds(18)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<PopularPage> GetPopularAsync(
        int page,
        int pageSize = 18,
        CancellationToken cancellationToken = default)
    {
        var relativeUri = $"x/web-interface/popular?ps={pageSize}&pn={Math.Max(1, page)}";
        var response = await SendAsync<PopularResponse>(relativeUri, null, cancellationToken);

        if (response.Code != 0 || response.Data is null)
        {
            throw new BiliApiException(response.Message, response.Code);
        }

        return response.Data;
    }

    public async Task<RecommendedPage> GetRecommendedAsync(
        string? cookieHeader,
        int freshIndex,
        int freshIndexInHour,
        int pageSize = 18,
        CancellationToken cancellationToken = default)
    {
        var index = Math.Max(1, freshIndex);
        var indexInHour = Math.Max(1, freshIndexInHour);
        var relativeUri =
            "x/web-interface/wbi/index/top/feed/rcmd" +
            $"?web_location=1430650&y_num=5&fresh_type=4&feed_version=V8" +
            $"&fresh_idx={index}&fresh_idx_1h={indexInHour}&homepage_ver=1" +
            $"&fetch_row=1&brush={index - 1}" +
            $"&ps={pageSize}&last_y_num=5&screen=1920-1080";
        var response = await SendAsync<RecommendedResponse>(
            relativeUri,
            cookieHeader,
            cancellationToken);

        if (response.Code != 0 || response.Data is null)
        {
            throw new BiliApiException(response.Message, response.Code);
        }

        return response.Data;
    }

    public async Task<PopularPage> SearchVideosAsync(
        string keyword,
        int page,
        string cookieHeader,
        CancellationToken cancellationToken = default)
    {
        var normalizedKeyword = keyword.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKeyword))
        {
            return new PopularPage { NoMore = true };
        }

        var pageNumber = Math.Max(1, page);
        var encodedKeyword = Uri.EscapeDataString(normalizedKeyword);
        var relativeUri =
            "x/web-interface/wbi/search/all/v2" +
            $"?keyword={encodedKeyword}&page={pageNumber}" +
            "&order=&duration=&tids=0&platform=pc&web_location=333.1007";
        var response = await SendAsync<SearchResponse>(
            relativeUri,
            cookieHeader,
            cancellationToken,
            new Uri($"https://search.bilibili.com/all?keyword={encodedKeyword}"));

        if (response.Code != 0 || response.Data is null)
        {
            throw new BiliApiException(response.Message, response.Code);
        }

        var videoSection = response.Data.ResultSections.FirstOrDefault(
            section => string.Equals(section.ResultType, "video", StringComparison.Ordinal));
        var results = videoSection?.Data ?? [];

        return new PopularPage
        {
            Items = results.Select(MapSearchVideo).ToList(),
            NoMore = results.Count == 0 || pageNumber >= response.Data.PageCount
        };
    }

    public async Task<UserProfile?> GetCurrentUserAsync(
        string cookieHeader,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        var response = await SendAsync<NavigationResponse>(
            "x/web-interface/nav",
            cookieHeader,
            cancellationToken);

        if (response.Code != 0 || response.Data?.IsLogin != true)
        {
            return null;
        }

        return new UserProfile
        {
            Mid = response.Data.Mid,
            UserName = response.Data.UserName,
            AvatarUrl = NormalizeHttps(response.Data.Face),
            Level = response.Data.LevelInfo?.CurrentLevel ?? 0
        };
    }

    private async Task<T> SendAsync<T>(
        string relativeUri,
        string? cookieHeader,
        CancellationToken cancellationToken,
        Uri? referrer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        if (referrer is not null)
        {
            request.Headers.Referrer = referrer;
        }

        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken)
            ?? throw new BiliApiException("接口返回了空数据。", -1);
    }

    private static string NormalizeHttps(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? $"https://{value[7..]}"
            : value;

    private static VideoItem MapSearchVideo(SearchVideoResult result) => new()
    {
        Bvid = result.Bvid,
        Picture = result.Picture,
        Title = WebUtility.HtmlDecode(Regex.Replace(result.Title, "<[^>]+>", string.Empty)),
        Duration = ParseDuration(result.Duration),
        PublishTimestamp = result.PublishTimestamp,
        Owner = new VideoOwner
        {
            Mid = result.Mid,
            Name = result.Author,
            Face = result.Upic
        },
        Statistics = new VideoStatistics
        {
            View = result.Play,
            Danmaku = result.Danmaku
        },
        RecommendationReason = new RecommendationReason { Content = "搜索结果" }
    };

    private static int ParseDuration(string value)
    {
        var totalSeconds = 0;
        foreach (var part in value.Split(':'))
        {
            if (!int.TryParse(part, out var number))
            {
                return 0;
            }

            totalSeconds = totalSeconds * 60 + number;
        }

        return totalSeconds;
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed class BiliApiException(string message, int code)
    : Exception(string.IsNullOrWhiteSpace(message) ? $"B 站接口错误（{code}）。" : message)
{
    public int Code { get; } = code;
}

public sealed class PopularResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public PopularPage? Data { get; set; }
}

public sealed class PopularPage
{
    [JsonPropertyName("list")]
    public List<VideoItem> Items { get; set; } = [];

    [JsonPropertyName("no_more")]
    public bool NoMore { get; set; }
}

public sealed class RecommendedResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public RecommendedPage? Data { get; set; }
}

public sealed class RecommendedPage
{
    [JsonPropertyName("item")]
    public List<VideoItem> Items { get; set; } = [];

    [JsonPropertyName("mid")]
    public long Mid { get; set; }
}

public sealed class SearchResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public SearchData? Data { get; set; }
}

public sealed class SearchData
{
    [JsonPropertyName("numPages")]
    public int PageCount { get; set; }

    [JsonPropertyName("result")]
    public List<SearchResultSection> ResultSections { get; set; } = [];
}

public sealed class SearchResultSection
{
    [JsonPropertyName("result_type")]
    public string ResultType { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public List<SearchVideoResult> Data { get; set; } = [];
}

public sealed class SearchVideoResult
{
    [JsonPropertyName("bvid")]
    public string Bvid { get; set; } = string.Empty;

    [JsonPropertyName("pic")]
    public string Picture { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("duration")]
    public string Duration { get; set; } = string.Empty;

    [JsonPropertyName("pubdate")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long PublishTimestamp { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("upic")]
    public string Upic { get; set; } = string.Empty;

    [JsonPropertyName("mid")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Mid { get; set; }

    [JsonPropertyName("play")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Play { get; set; }

    [JsonPropertyName("video_review")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Danmaku { get; set; }
}

public sealed class NavigationResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public NavigationData? Data { get; set; }
}

public sealed class NavigationData
{
    [JsonPropertyName("isLogin")]
    public bool IsLogin { get; set; }

    [JsonPropertyName("mid")]
    public long Mid { get; set; }

    [JsonPropertyName("uname")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("face")]
    public string Face { get; set; } = string.Empty;

    [JsonPropertyName("level_info")]
    public NavigationLevelInfo? LevelInfo { get; set; }
}

public sealed class NavigationLevelInfo
{
    [JsonPropertyName("current_level")]
    public int CurrentLevel { get; set; }
}
