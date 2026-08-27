using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BiliBiliPlayer.Models;

namespace BiliBiliPlayer.Services;

/// <summary>
/// Loads comment pages independently from the media pipeline. A comment request can fail or be
/// cancelled without replacing the current playback manifest or touching the media elements.
/// </summary>
public sealed class BiliCommentService
{
    private const int PageSize = 20;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bilibili.com/"),
            Timeout = TimeSpan.FromSeconds(12)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return httpClient;
    }

    public async Task<VideoCommentPage> GetPageAsync(
        long aid,
        long ownerMid,
        string bvid,
        string mode,
        VideoCommentPaging paging,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        if (aid <= 0 || paging.IsEnd)
        {
            return new VideoCommentPage
            {
                Paging = paging,
                TotalCount = 0
            };
        }

        var normalizedMode = string.Equals(mode, "latest", StringComparison.Ordinal)
            ? "latest"
            : "hot";

        if (string.Equals(paging.Source, "legacy", StringComparison.Ordinal))
        {
            return await GetLegacyPageAsync(
                aid,
                ownerMid,
                bvid,
                normalizedMode,
                paging.PageNumber,
                cookieHeader,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await GetMainPageAsync(
                aid,
                ownerMid,
                bvid,
                normalizedMode,
                paging,
                cookieHeader,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The legacy endpoint is still useful when the cursor endpoint is being rate-limited.
            // Duplicates caused by switching sort implementations are removed in PlayerPage.
            return await GetLegacyPageAsync(
                aid,
                ownerMid,
                bvid,
                normalizedMode,
                Math.Max(1, paging.PageNumber),
                cookieHeader,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<VideoCommentPage> GetMainPageAsync(
        long aid,
        long ownerMid,
        string bvid,
        string mode,
        VideoCommentPaging paging,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var modeValue = mode == "latest" ? 2 : 3;
        var next = string.IsNullOrWhiteSpace(paging.Next) ? "0" : paging.Next;
        var relativeUri =
            "x/v2/reply/main" +
            $"?oid={aid}&type=1&mode={modeValue}" +
            $"&next={Uri.EscapeDataString(next)}&plat=1&ps={PageSize}" +
            "&web_location=1315875";

        using var document = await GetJsonAsync(
            relativeUri,
            bvid,
            cookieHeader,
            cancellationToken).ConfigureAwait(false);
        var data = RequireObject(document.RootElement, "data");
        var items = ReadComments(data, ownerMid);
        var cursor = TryGetObject(data, "cursor");
        var total = GetInt32(cursor, "all_count", items.Count);
        var returnedNext = GetString(cursor, "next");
        var isEnd = GetBoolean(cursor, "is_end") || items.Count == 0;

        if (!isEnd && (string.IsNullOrWhiteSpace(returnedNext) || returnedNext == next))
        {
            throw new InvalidOperationException("评论接口没有返回有效的下一页游标。");
        }

        return new VideoCommentPage
        {
            Items = items,
            TotalCount = total,
            Paging = new VideoCommentPaging
            {
                Source = "main",
                Next = string.IsNullOrWhiteSpace(returnedNext) ? next : returnedNext,
                PageNumber = Math.Max(1, paging.PageNumber + 1),
                IsEnd = isEnd
            }
        };
    }

    private async Task<VideoCommentPage> GetLegacyPageAsync(
        long aid,
        long ownerMid,
        string bvid,
        string mode,
        int pageNumber,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, pageNumber);
        var sort = mode == "latest" ? 0 : 2;
        var noHot = mode == "latest" ? 1 : 0;
        var relativeUri =
            "x/v2/reply" +
            $"?oid={aid}&type=1&sort={sort}&pn={page}&ps={PageSize}&nohot={noHot}";

        using var document = await GetJsonAsync(
            relativeUri,
            bvid,
            cookieHeader,
            cancellationToken).ConfigureAwait(false);
        var data = RequireObject(document.RootElement, "data");
        var items = ReadComments(data, ownerMid);
        var pageInfo = TryGetObject(data, "page");
        var total = GetInt32(pageInfo, "count", items.Count);

        return new VideoCommentPage
        {
            Items = items,
            TotalCount = total,
            Paging = new VideoCommentPaging
            {
                Source = "legacy",
                Next = string.Empty,
                PageNumber = page + 1,
                IsEnd = items.Count < PageSize
            }
        };
    }

    private async Task<JsonDocument> GetJsonAsync(
        string relativeUri,
        string bvid,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        request.Headers.Referrer = new Uri($"https://www.bilibili.com/video/{bvid}/");
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var code = GetInt32(document.RootElement, "code", -1);
        if (code == 0)
        {
            return document;
        }

        var message = GetString(document.RootElement, "message");
        document.Dispose();
        throw new BiliApiException(
            string.IsNullOrWhiteSpace(message) ? $"评论接口错误（{code}）。" : message,
            code);
    }

    private static List<VideoComment> ReadComments(JsonElement data, long ownerMid)
    {
        if (!data.TryGetProperty("replies", out var replies) ||
            replies.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return replies
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => MapComment(item, ownerMid, includeChildren: true))
            .ToList();
    }

    private static VideoComment MapComment(
        JsonElement reply,
        long ownerMid,
        bool includeChildren)
    {
        var content = TryGetObject(reply, "content");
        var member = TryGetObject(reply, "member");
        var pictures = new List<string>();
        if (content.TryGetProperty("pictures", out var pictureArray) &&
            pictureArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var picture in pictureArray.EnumerateArray().Take(9))
            {
                var url = FirstNonEmpty(
                    GetString(picture, "img_src"),
                    GetString(picture, "url"),
                    GetString(picture, "img_url"));
                if (!string.IsNullOrWhiteSpace(url))
                {
                    pictures.Add(NormalizeHttps(url));
                }
            }
        }

        var emotes = new Dictionary<string, string>();
        if (content.TryGetProperty("emote", out var emoteObject) &&
            emoteObject.ValueKind == JsonValueKind.Object)
        {
            foreach (var emote in emoteObject.EnumerateObject())
            {
                var url = FirstNonEmpty(
                    GetString(emote.Value, "url"),
                    GetString(emote.Value, "gif_url"));
                if (!string.IsNullOrWhiteSpace(emote.Name) && !string.IsNullOrWhiteSpace(url))
                {
                    emotes[emote.Name] = NormalizeHttps(url);
                }
            }
        }

        var children = new List<VideoComment>();
        if (includeChildren &&
            reply.TryGetProperty("replies", out var childArray) &&
            childArray.ValueKind == JsonValueKind.Array)
        {
            children.AddRange(childArray
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Take(3)
                .Select(item => MapComment(item, ownerMid, includeChildren: false)));
        }

        var userName = Limit(GetString(member, "uname"), 80);
        var message = Limit(GetString(content, "message"), 1600);
        var memberMid = GetInt64(member, "mid");
        return new VideoComment
        {
            Id = FirstNonEmpty(GetString(reply, "rpid_str"), GetString(reply, "rpid")),
            UserName = string.IsNullOrWhiteSpace(userName) ? "哔哩哔哩用户" : userName,
            AvatarUrl = NormalizeHttps(FirstNonEmpty(
                GetString(member, "avatar"),
                GetString(member, "face"))),
            Message = message,
            CreatedAt = GetInt64(reply, "ctime"),
            LikeCount = GetInt64(reply, "like"),
            ReplyCount = GetInt32(reply, "rcount"),
            PictureUrls = pictures,
            Emotes = emotes,
            IsUploader = ownerMid > 0 && memberMid == ownerMid,
            Replies = children
        };
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        var value = TryGetObject(parent, name);
        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidOperationException("评论接口返回缺少数据。");
    }

    private static JsonElement TryGetObject(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string GetString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static int GetInt32(JsonElement parent, string name, int fallback = 0)
    {
        var value = GetInt64(parent, name, fallback);
        return value is > int.MaxValue or < int.MinValue ? fallback : (int)value;
    }

    private static long GetInt64(JsonElement parent, string name, long fallback = 0)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), out number)
            ? number
            : fallback;
    }

    private static bool GetBoolean(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0));

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string NormalizeHttps(string value) =>
        value.StartsWith("//", StringComparison.Ordinal)
            ? $"https:{value}"
            : value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? $"https://{value[7..]}"
                : value;

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

}
