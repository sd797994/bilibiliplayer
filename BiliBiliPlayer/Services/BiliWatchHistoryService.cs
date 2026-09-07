using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BiliBiliPlayer.Models;

namespace BiliBiliPlayer.Services;

/// <summary>
/// Creates one Bilibili watch-history entry. This intentionally does not run a periodic
/// heartbeat: the app only needs the video to appear in the signed-in account's history.
/// </summary>
public sealed class BiliWatchHistoryService
{
    private static readonly Uri HeartbeatEndpoint =
        new("https://api.bilibili.com/x/click-interface/web/heartbeat");
    private static readonly Uri HistoryEndpoint =
        new("https://api.bilibili.com/x/web-interface/history/cursor");

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient _httpClient;

    public BiliWatchHistoryService()
        : this(SharedHttpClient)
    {
    }

    public BiliWatchHistoryService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<bool> ReportStartedAsync(
        string bvid,
        long aid,
        long cid,
        double position,
        string cookieHeader,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bvid) || aid <= 0 || cid <= 0 ||
            string.IsNullOrWhiteSpace(cookieHeader))
        {
            return false;
        }

        var playedTime = double.IsFinite(position)
            ? Math.Max(1, (int)Math.Floor(position))
            : 1;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var csrf = ReadCookie(cookieHeader, "bili_jct");
        var form = new List<KeyValuePair<string, string>>
        {
            new("aid", aid.ToString(CultureInfo.InvariantCulture)),
            new("bvid", bvid),
            new("cid", cid.ToString(CultureInfo.InvariantCulture)),
            new("played_time", playedTime.ToString(CultureInfo.InvariantCulture)),
            new("realtime", "1"),
            new("start_ts", Math.Max(0, now - 1).ToString(CultureInfo.InvariantCulture)),
            new("type", "3"),
            new("dt", "2"),
            new("play_type", "0")
        };
        if (!string.IsNullOrWhiteSpace(csrf))
        {
            form.Add(new("csrf", csrf));
            form.Add(new("csrf_token", csrf));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, HeartbeatEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Referrer = new Uri(
            $"https://www.bilibili.com/video/{Uri.EscapeDataString(bvid)}/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("code", out var code) &&
               code.TryGetInt32(out var value) && value == 0;
    }

    public async Task<WatchHistoryPage> GetPageAsync(
        string cookieHeader,
        WatchHistoryCursor? cursor = null,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            throw new BiliWatchHistoryException("账号未登录", -101);
        }

        var pageSizeValue = Math.Clamp(pageSize, 1, 30);
        var query = $"ps={pageSizeValue}";
        var business = string.Empty;
        if (cursor is not null)
        {
            var max = Math.Max(0, cursor.Max);
            var viewAt = Math.Max(0, cursor.ViewAt);
            business = string.IsNullOrWhiteSpace(cursor.Business)
                ? "archive"
                : cursor.Business;
            query +=
                $"&max={max}" +
                $"&view_at={viewAt}" +
                $"&business={Uri.EscapeDataString(business)}";
        }

        var requestUri = new UriBuilder(HistoryEndpoint)
        {
            Query = query
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Referrer = new Uri("https://www.bilibili.com/account/history");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var code = ReadInt32(root, "code");
        if (code != 0)
        {
            throw new BiliWatchHistoryException(ReadString(root, "message"), code);
        }

        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            return new WatchHistoryPage();
        }

        var items = new List<WatchHistoryItem>();
        var rawItemCount = 0;
        if (data.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in list.EnumerateArray())
            {
                rawItemCount++;
                if (TryMapVideoHistory(element, out var item))
                {
                    items.Add(item);
                }
            }
        }

        WatchHistoryCursor? nextCursor = null;
        if (data.TryGetProperty("cursor", out var cursorElement) &&
            cursorElement.ValueKind == JsonValueKind.Object)
        {
            var nextBusiness = ReadString(cursorElement, "business");
            nextCursor = new WatchHistoryCursor(
                Math.Max(0, ReadInt64(cursorElement, "max")),
                Math.Max(0, ReadInt64(cursorElement, "view_at")),
                string.IsNullOrWhiteSpace(nextBusiness)
                    ? string.IsNullOrWhiteSpace(business) ? "archive" : business
                    : nextBusiness);
        }

        var cursorAdvanced = nextCursor is not null &&
                             (cursor is null ||
                              nextCursor.Max != cursor.Max ||
                              nextCursor.ViewAt != cursor.ViewAt ||
                              !string.Equals(
                                  nextCursor.Business,
                                  cursor.Business,
                                  StringComparison.Ordinal));
        return new WatchHistoryPage
        {
            Items = items,
            NextCursor = nextCursor,
            HasMore = rawItemCount > 0 && cursorAdvanced
        };
    }

    private static bool TryMapVideoHistory(JsonElement element, out WatchHistoryItem item)
    {
        item = new WatchHistoryItem();
        if (!element.TryGetProperty("history", out var history) ||
            history.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var business = ReadString(history, "business");
        var bvid = ReadString(history, "bvid");
        if (!string.Equals(business, "archive", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(bvid))
        {
            return false;
        }

        item = new WatchHistoryItem
        {
            Bvid = bvid,
            Aid = ReadInt64(history, "oid"),
            Cid = ReadInt64(history, "cid"),
            Title = ReadString(element, "title"),
            Cover = ReadString(element, "cover"),
            AuthorName = ReadString(element, "author_name"),
            AuthorFace = ReadString(element, "author_face"),
            AuthorMid = ReadInt64(element, "author_mid"),
            Duration = Math.Max(0, ReadInt32(element, "duration")),
            Progress = ReadInt32(element, "progress"),
            ViewedAt = Math.Max(0, ReadInt64(element, "view_at"))
        };
        return true;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.TryGetInt32(out var value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            ? value
            : 0;
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.TryGetInt64(out var value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String &&
               long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            ? value
            : 0;
    }

    private static string ReadCookie(string cookieHeader, string name)
    {
        foreach (var segment in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 || !string.Equals(
                    segment[..separator].Trim(), name, StringComparison.Ordinal))
            {
                continue;
            }

            return segment[(separator + 1)..].Trim();
        }

        return string.Empty;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        return client;
    }
}

public sealed class BiliWatchHistoryException(string message, int code)
    : Exception(string.IsNullOrWhiteSpace(message) ? $"B 站历史记录接口错误（{code}）。" : message)
{
    public int Code { get; } = code;
}
