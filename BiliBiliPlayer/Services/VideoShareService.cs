using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using BiliBiliPlayer.Models;

namespace BiliBiliPlayer.Services;

/// <summary>
/// Sends Bilibili video link cards through the local WeChat Hook service.
/// </summary>
public static class VideoShareService
{
    private const string SendCardEndpoint = "http://127.0.0.1:30002/send_xml_with_local_thumb";
    private const string SendTextEndpoint = "http://127.0.0.1:30001/SendTextMsg";
    private const string RecentContactsEndpoint = "http://127.0.0.1:30002/share/recent_contacts";
    private const string UploadWxid = "filehelper";
    private const int BridgeTimeoutSeconds = 30;
    private const long MaximumCoverBytes = 20 * 1024 * 1024;
    private static readonly HttpClient CoverHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private static readonly HttpClient WeChatHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    public static string GetVideoUrl(VideoItem video)
    {
        var bvid = video.Bvid.Trim();
        return $"https://www.bilibili.com/video/{bvid}/";
    }

    public static async Task<IReadOnlyList<WeChatShareContact>> GetRecentContactsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await WeChatHttpClient.GetAsync(
                RecentContactsEndpoint,
                cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var ret = root.TryGetProperty("ret", out var retElement) &&
                      retElement.TryGetInt32(out var retValue)
                ? retValue
                : -1;
            if (!response.IsSuccessStatusCode || ret != 0)
            {
                var message = root.TryGetProperty("msg", out var messageElement) &&
                              messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : null;
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(message)
                        ? $"联系人服务返回 HTTP {(int)response.StatusCode}，ret={ret}。"
                        : message);
            }

            if (!root.TryGetProperty("data", out var dataElement) ||
                dataElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<WeChatShareContact>();
            }

            var contacts = new List<WeChatShareContact>();
            foreach (var item in dataElement.EnumerateArray())
            {
                var wxid = ReadJsonString(item, "wxid");
                var displayName = ReadJsonString(item, "display_name");
                if (string.IsNullOrWhiteSpace(wxid) || string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var avatarUrl = ReadJsonString(item, "avatar_url");
                var isGroup = item.TryGetProperty("is_group", out var groupElement) &&
                              groupElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                              groupElement.GetBoolean();
                var lastTimestamp = item.TryGetProperty("last_timestamp", out var timestampElement) &&
                                    timestampElement.TryGetInt64(out var timestamp)
                    ? timestamp
                    : 0;
                contacts.Add(new WeChatShareContact(
                    wxid.Trim(),
                    displayName.Trim(),
                    avatarUrl,
                    isGroup,
                    lastTimestamp));
            }
            return contacts;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("读取微信最近联系人超时。");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                "无法连接微信联系人服务，请确认微信及本地 Hook v5 已启动。",
                ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("微信联系人服务返回了无法识别的数据。", ex);
        }
    }

    public static async Task<WeChatShareResult> SendToWeChatAsync(
        VideoItem video,
        WeChatShareContact contact,
        string? recommendation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ValidateTargetWxid(contact.Wxid);

        var cardResult = await SendCardToWeChatAsync(video, contact.Wxid, cancellationToken);
        if (!cardResult.Success)
        {
            return cardResult;
        }

        var message = recommendation?.Trim();
        if (string.IsNullOrEmpty(message))
        {
            return WeChatShareResult.Ok($"卡片已发送给 {contact.DisplayName}。");
        }

        var textResult = await SendRecommendationAsync(contact.Wxid, message, cancellationToken);
        return textResult.Success
            ? WeChatShareResult.Ok($"卡片和推荐语已发送给 {contact.DisplayName}。")
            : WeChatShareResult.Fail($"卡片已发送，但推荐语发送失败：{textResult.Message}");
    }

    public static async Task<WeChatShareResult> SendCardToWeChatAsync(
        VideoItem video,
        string targetWxid,
        CancellationToken cancellationToken = default)
    {
        ValidateTargetWxid(targetWxid);
        var title = string.IsNullOrWhiteSpace(video.Title)
            ? video.Bvid.Trim()
            : video.Title.Trim();
        var ownerName = string.IsNullOrWhiteSpace(video.OwnerName)
            ? "未知UP主"
            : video.OwnerName.Trim();
        string? thumbnailPath = null;

        try
        {
            try
            {
                thumbnailPath = await DownloadCoverAsync(
                    video.CoverUrl,
                    GetVideoUrl(video),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return WeChatShareResult.Fail("下载 B 站封面超时，请稍后重试。");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
            {
                return WeChatShareResult.Fail($"下载 B 站封面失败：{ex.Message}");
            }

            var payload = new
            {
                wxid = targetWxid,
                title,
                description = $"UP主：{ownerName}\n播放：{video.ViewCountText}",
                url = GetVideoUrl(video),
                thumb_path = thumbnailPath,
                upload_wxid = UploadWxid,
                timeout_seconds = BridgeTimeoutSeconds
            };

            using var response = await WeChatHttpClient.PostAsJsonAsync(
                SendCardEndpoint,
                payload,
                cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseBridgeResponse(response, responseBody);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WeChatShareResult.Fail("微信封面上传或卡片发送超时。");
        }
        catch (HttpRequestException)
        {
            return WeChatShareResult.Fail(
                "无法连接微信卡片服务，请确认微信及本地 Hook v5 已启动。");
        }
        finally
        {
            TryDeleteThumbnail(thumbnailPath);
        }
    }

    private static async Task<WeChatShareResult> SendRecommendationAsync(
        string targetWxid,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = new
            {
                wxidorgid = targetWxid,
                msg = message
            };

            using var response = await WeChatHttpClient.PostAsJsonAsync(
                SendTextEndpoint,
                payload,
                cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseTextResponse(response, responseBody);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WeChatShareResult.Fail("发送超时。");
        }
        catch (HttpRequestException)
        {
            return WeChatShareResult.Fail("无法连接微信文本消息服务。");
        }
    }

    private static async Task<string> DownloadCoverAsync(
        string coverUrl,
        string videoUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var sourceUri) ||
            (sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException("当前视频没有有效的封面地址。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        request.Headers.Referrer = new Uri(videoUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BiliBiliPlayer", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));

        using var response = await CoverHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is <= 0 or > MaximumCoverBytes)
        {
            throw new InvalidDataException("封面文件大小无效。");
        }

        var cacheDirectory = Path.Combine(FileSystem.CacheDirectory, "wechat-share");
        Directory.CreateDirectory(cacheDirectory);
        var extension = GetImageExtension(response.Content.Headers.ContentType?.MediaType);
        var path = Path.Combine(cacheDirectory, $"bilibili-cover-{Guid.NewGuid():N}{extension}");

        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);

            if (output.Length == 0 || output.Length > MaximumCoverBytes)
            {
                throw new InvalidDataException("下载到的封面文件大小无效。");
            }

            return path;
        }
        catch
        {
            TryDeleteThumbnail(path);
            throw;
        }
    }

    private static WeChatShareResult ParseBridgeResponse(
        HttpResponseMessage response,
        string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var ret = root.TryGetProperty("ret", out var retElement) &&
                      retElement.TryGetInt32(out var retValue)
                ? retValue
                : -1;
            var message = root.TryGetProperty("msg", out var messageElement)
                ? messageElement.GetString()
                : root.TryGetProperty("retmsg", out var retMessageElement)
                    ? retMessageElement.GetString()
                    : null;

            if (response.IsSuccessStatusCode && ret == 0 && PrivateSendSucceeded(root))
            {
                return WeChatShareResult.Ok();
            }

            if (ret == -6)
            {
                return WeChatShareResult.Fail("微信封面上传超时，请确认 Hook v5 状态正常后重试。");
            }

            var detail = string.IsNullOrWhiteSpace(message)
                ? $"HTTP {(int)response.StatusCode}，ret={ret}"
                : $"{message}（ret={ret}）";
            return WeChatShareResult.Fail($"微信分享失败：{detail}");
        }
        catch (JsonException)
        {
            return WeChatShareResult.Fail(
                response.IsSuccessStatusCode
                    ? "微信分享服务返回了无法识别的数据。"
                    : $"微信分享服务返回 HTTP {(int)response.StatusCode}。");
        }
    }

    private static WeChatShareResult ParseTextResponse(
        HttpResponseMessage response,
        string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var ret = root.TryGetProperty("ret", out var retElement) &&
                      retElement.TryGetInt32(out var retValue)
                ? retValue
                : -1;
            if (response.IsSuccessStatusCode && ret == 0)
            {
                return WeChatShareResult.Ok("推荐语已发送。");
            }

            var message = root.TryGetProperty("msg", out var messageElement)
                ? messageElement.GetString()
                : root.TryGetProperty("retmsg", out var retMessageElement)
                    ? retMessageElement.GetString()
                    : null;
            var detail = string.IsNullOrWhiteSpace(message)
                ? $"HTTP {(int)response.StatusCode}，ret={ret}"
                : $"{message}（ret={ret}）";
            return WeChatShareResult.Fail(detail);
        }
        catch (JsonException)
        {
            return WeChatShareResult.Fail(
                response.IsSuccessStatusCode
                    ? "服务返回了无法识别的数据。"
                    : $"服务返回 HTTP {(int)response.StatusCode}。");
        }
    }

    private static bool PrivateSendSucceeded(JsonElement root)
    {
        if (!root.TryGetProperty("private_body", out var privateBodyElement) ||
            privateBodyElement.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        var privateBody = privateBodyElement.GetString();
        if (string.IsNullOrWhiteSpace(privateBody))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(privateBody);
            var privateRoot = document.RootElement;
            var ret = privateRoot.TryGetProperty("ret", out var retElement) &&
                      retElement.TryGetInt32(out var retValue)
                ? retValue
                : -1;
            var dataRet = privateRoot.TryGetProperty("data", out var dataElement) &&
                          dataElement.ValueKind == JsonValueKind.Object &&
                          dataElement.TryGetProperty("ret", out var dataRetElement) &&
                          dataRetElement.TryGetInt32(out var dataRetValue)
                ? dataRetValue
                : 0;
            return ret == 0 && dataRet == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ReadJsonString(JsonElement value, string propertyName)
    {
        return value.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static void ValidateTargetWxid(string targetWxid)
    {
        if (string.IsNullOrWhiteSpace(targetWxid) || targetWxid.Length > 256 ||
            targetWxid.Any(char.IsControl))
        {
            throw new ArgumentException("微信联系人标识无效。", nameof(targetWxid));
        }
    }

    private static string GetImageExtension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg"
    };

    private static void TryDeleteThumbnail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Cache cleanup must not overwrite the actual share result.
        }
    }
}

public sealed record WeChatShareResult(bool Success, string Message)
{
    public static WeChatShareResult Ok(string message = "已发送到微信。") => new(true, message);

    public static WeChatShareResult Fail(string message) => new(false, message);
}

public sealed record WeChatShareContact(
    string Wxid,
    string DisplayName,
    string AvatarUrl,
    bool IsGroup,
    long LastTimestamp)
{
    public string AvatarText => string.IsNullOrWhiteSpace(DisplayName)
        ? "微"
        : DisplayName[..1];

    public string KindText => IsGroup ? "群聊" : "联系人";
}
