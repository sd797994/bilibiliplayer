using System.Text.Json;
using BiliBiliPlayer.Models;

namespace BiliBiliPlayer.Services;

/// <summary>
/// Resolves Bilibili playurl data inside the app's logged-in WebView2 profile.
/// The WebView passed to this service is expected to be temporary: PlayerPage creates it only
/// while resolving, then disconnects/destroys it as soon as the manifest has been captured.
/// </summary>
public sealed class BiliPlaybackResolver
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<PlaybackManifest> ResolveManifestAsync(
        WebView resolverWebView,
        string bvid,
        int maximumQuality = 80,
        CancellationToken cancellationToken = default)
    {
#if WINDOWS
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var nativeWebView = await GetNativeWebViewAsync(resolverWebView, cancellationToken)
                ?? throw new InvalidOperationException("后台 WebView2 尚未初始化。");

            var core = nativeWebView.CoreWebView2
                ?? throw new InvalidOperationException("后台 WebView2 Core 尚未初始化。");

            // This WebView exists only for a few seconds, but silence it before navigating so a
            // website player can never leak audio during the resolution window.
            core.IsMuted = true;

            var context = await ReadVideoContextAsync(core, bvid, cancellationToken);

            // Ask for the highest quality supported by our UI first. Some videos simply do not
            // have a 1080P representation; in that case Bilibili normally downgrades the response,
            // but a few videos/endpoints reject the unsupported qn instead. Retry lower qn values
            // inside the same short-lived resolver so opening a 720P-only video never fails just
            // because our preferred ceiling is 1080P.
            var requestQualities = new[] { maximumQuality, 64, 32, 16 }
                .Where(quality => quality > 0 && quality <= maximumQuality)
                .Distinct()
                .OrderByDescending(quality => quality)
                .ToArray();

            foreach (var requestQuality in requestQualities)
            {
                var playInfoJson = await FetchPlayInfoInPageAsync(
                    core,
                    context,
                    requestQuality,
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(playInfoJson))
                {
                    continue;
                }

                try
                {
                    var manifest = ParsePlaybackManifest(playInfoJson, requestQuality);
                    if (manifest.Sources.Count > 0)
                    {
                        return manifest;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Try the next lower quality before falling back to the page's own play info.
                }
            }

            // Fallback to the play information already produced by Bilibili's own page.
            var pagePlayInfoJson = await TryReadPagePlayInfoAsync(core);
            if (!string.IsNullOrWhiteSpace(pagePlayInfoJson))
            {
                return ParsePlaybackManifest(pagePlayInfoJson, maximumQuality);
            }

            throw new InvalidOperationException(
                "没有拿到播放地址。请确认视频在当前账号中可以正常观看，然后重试。");
        }
        finally
        {
            _gate.Release();
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("当前播放解析实现先支持 Windows/WebView2。");
#endif
    }

#if WINDOWS
    private static async Task<VideoContext> ReadVideoContextAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        string bvid,
        CancellationToken cancellationToken)
    {
        core.Navigate($"https://www.bilibili.com/video/{Uri.EscapeDataString(bvid)}/?autoplay=0");

        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var raw = await core.ExecuteScriptAsync(
                    """
                    (() => {
                        // CoreWebView2.IsMuted already silences this temporary resolver. Also
                        // pause media elements so the hidden page does not spend resources decoding.
                        document.querySelectorAll('video,audio').forEach(media => {
                            try { media.muted = true; media.pause(); media.preload = 'none'; } catch (_) {}
                        });

                        const state = window.__INITIAL_STATE__;
                        if (!state) return '';
                        const data = state.videoData ?? state.videoInfo;
                        if (!data) return '';
                        const firstPage = Array.isArray(data.pages) ? data.pages[0] : null;
                        const cid = data.cid ?? firstPage?.cid ?? state.cid;
                        const aid = data.aid ?? data.id ?? 0;
                        const bvid = data.bvid ?? '';
                        if (!cid || !bvid) return '';
                        return JSON.stringify({ aid, cid, bvid });
                    })()
                    """);

                var json = DecodeScriptString(raw);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var context = JsonSerializer.Deserialize<VideoContext>(
                        json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (context is not null && context.Cid > 0)
                    {
                        return context;
                    }
                }
            }
            catch
            {
                // The document may still be navigating. Retry until the timeout below.
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("B 站视频页初始化超时，暂时无法解析 cid。");
    }

    private static async Task<string?> FetchPlayInfoInPageAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        VideoContext context,
        int requestedQuality,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var prefix = $"BILI_PLAYURL:{requestId}:";
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnWebMessageReceived(
            object? sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                var message = args.TryGetWebMessageAsString();
                if (!message.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return;
                }

                completion.TrySetResult(message[prefix.Length..]);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        core.WebMessageReceived += OnWebMessageReceived;
        try
        {
            var bvidJson = JsonSerializer.Serialize(context.Bvid);
            var prefixJson = JsonSerializer.Serialize(prefix);
            var script = $$"""
                (() => {
                    const prefix = {{prefixJson}};
                    const params = new URLSearchParams({
                        bvid: {{bvidJson}},
                        avid: '{{context.Aid}}',
                        cid: '{{context.Cid}}',
                        qn: '{{requestedQuality}}',
                        fnval: '4048',
                        fnver: '0',
                        fourk: '1'
                    });

                    const post = payload => {
                        try { chrome.webview.postMessage(prefix + JSON.stringify(payload)); } catch (_) {}
                    };

                    fetch('https://api.bilibili.com/x/player/playurl?' + params.toString(), {
                        method: 'GET',
                        credentials: 'include',
                        cache: 'no-store'
                    })
                    .then(async response => {
                        const text = await response.text();
                        post({ ok: response.ok, status: response.status, text });
                    })
                    .catch(error => post({ ok: false, status: 0, error: String(error) }));
                })()
                """;

            await core.ExecuteScriptAsync(script);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completedTask = await Task.WhenAny(completion.Task, timeoutTask);
            if (completedTask != completion.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            var envelopeJson = await completion.Task;
            if (string.IsNullOrWhiteSpace(envelopeJson))
            {
                return null;
            }

            using var envelope = JsonDocument.Parse(envelopeJson);
            var root = envelope.RootElement;
            if (!root.TryGetProperty("text", out var textElement))
            {
                return null;
            }

            var text = textElement.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            using var responseJson = JsonDocument.Parse(text);
            if (responseJson.RootElement.TryGetProperty("code", out var codeElement) &&
                codeElement.GetInt32() == 0)
            {
                return text;
            }

            return null;
        }
        finally
        {
            core.WebMessageReceived -= OnWebMessageReceived;
        }
    }

    private static async Task<string?> TryReadPagePlayInfoAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        try
        {
            var raw = await core.ExecuteScriptAsync(
                "JSON.stringify(window.__playinfo__ ?? null)");
            var json = DecodeScriptString(raw);
            return string.Equals(json, "null", StringComparison.OrdinalIgnoreCase) ? null : json;
        }
        catch
        {
            return null;
        }
    }

    private static PlaybackManifest ParsePlaybackManifest(string json, int requestedQuality)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("code", out var codeElement) && codeElement.GetInt32() != 0)
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            throw new InvalidOperationException(message ?? "B 站返回了播放解析错误。");
        }

        JsonElement data;
        if (root.TryGetProperty("data", out var dataElement))
        {
            data = dataElement;
        }
        else if (root.TryGetProperty("result", out var resultElement))
        {
            data = resultElement;
        }
        else
        {
            data = root;
        }

        if (data.TryGetProperty("dash", out var dashElement) &&
            dashElement.ValueKind == JsonValueKind.Object &&
            dashElement.TryGetProperty("video", out var videoArray) &&
            videoArray.ValueKind == JsonValueKind.Array)
        {
            var videos = videoArray.EnumerateArray()
                .Select(ParseTrack)
                .Where(track => track is not null && !string.IsNullOrWhiteSpace(track.Url))
                .Select(track => track!)
                .ToList();

            if (videos.Count == 0)
            {
                throw new InvalidOperationException("播放信息中没有可用的视频轨。");
            }

            Track? selectedAudio = null;
            if (dashElement.TryGetProperty("audio", out var audioArray) &&
                audioArray.ValueKind == JsonValueKind.Array)
            {
                selectedAudio = audioArray.EnumerateArray()
                    .Select(ParseTrack)
                    .Where(track => track is not null && !string.IsNullOrWhiteSpace(track.Url))
                    .Select(track => track!)
                    .OrderByDescending(track => track.Bandwidth)
                    .FirstOrDefault();
            }

            // Treat accept_quality as the authoritative list when the API provides it. DASH can
            // occasionally contain representations that are not actually selectable for the
            // current video/account, so blindly taking the largest track may pick a non-playable
            // 1080P URL on a 720P-only video.
            var acceptedQualities = ReadQualityArray(data, "accept_quality");
            var responseQuality = data.TryGetProperty("quality", out var responseQualityElement) &&
                                  responseQualityElement.TryGetInt32(out var parsedResponseQuality)
                ? parsedResponseQuality
                : 0;

            // Pick one compatible video representation for every genuinely available quality. The
            // URLs are cached as a snapshot so switching quality does not invoke the resolver again.
            var selectedVideos = videos
                .GroupBy(track => track.Quality)
                .Select(group => group
                    .OrderBy(track => CodecRank(track.Codec))
                    .ThenByDescending(track => track.Bandwidth)
                    .First())
                .Where(track => track.Quality > 0)
                .Where(track => acceptedQualities.Count == 0 || acceptedQualities.Contains(track.Quality))
                .Where(track => responseQuality <= 0 || track.Quality <= responseQuality)
                .OrderBy(track => track.Quality)
                .ToList();

            // Be defensive if a response exposes an odd quality list that does not line up with the
            // DASH tracks. Falling back to the actual response quality is safer than failing the video.
            if (selectedVideos.Count == 0 && responseQuality > 0)
            {
                var fallback = videos
                    .Where(track => track.Quality <= responseQuality)
                    .GroupBy(track => track.Quality)
                    .Select(group => group
                        .OrderBy(track => CodecRank(track.Codec))
                        .ThenByDescending(track => track.Bandwidth)
                        .First())
                    .OrderBy(track => track.Quality)
                    .ToList();

                selectedVideos.AddRange(fallback);
            }

            if (selectedVideos.Count == 0)
            {
                throw new InvalidOperationException("当前视频没有可播放的清晰度轨道。");
            }

            var availableQualities = selectedVideos
                .Select(track => track.Quality)
                .Distinct()
                .OrderBy(quality => quality)
                .ToArray();

            var sources = new Dictionary<int, PlaybackSource>();
            foreach (var video in selectedVideos)
            {
                sources[video.Quality] = new PlaybackSource
                {
                    VideoUrl = video.Url,
                    AudioUrl = selectedAudio?.Url,
                    RequestedQuality = video.Quality,
                    ActualQuality = video.Quality,
                    VideoCodec = video.Codec,
                    AvailableQualities = availableQualities
                };
            }

            return new PlaybackManifest
            {
                Sources = sources,
                AvailableQualities = availableQualities
            };
        }

        if (data.TryGetProperty("durl", out var durlElement) &&
            durlElement.ValueKind == JsonValueKind.Array)
        {
            var first = durlElement.EnumerateArray().FirstOrDefault();
            var url = GetString(first, "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                var actualQuality = data.TryGetProperty("quality", out var qualityElement) &&
                                    qualityElement.TryGetInt32(out var quality)
                    ? quality
                    : requestedQuality;

                var availableQualities = new[] { actualQuality };
                var source = new PlaybackSource
                {
                    VideoUrl = url,
                    RequestedQuality = requestedQuality,
                    ActualQuality = actualQuality,
                    AvailableQualities = availableQualities
                };

                return new PlaybackManifest
                {
                    Sources = new Dictionary<int, PlaybackSource> { [actualQuality] = source },
                    AvailableQualities = availableQualities
                };
            }
        }

        throw new InvalidOperationException("B 站返回的播放信息里没有 DASH/MP4 地址。");
    }

    private static IReadOnlySet<int> ReadQualityArray(JsonElement data, string propertyName)
    {
        if (!data.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<int>();
        }

        var values = new HashSet<int>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.TryGetInt32(out var value) && value > 0)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static Track? ParseTrack(JsonElement element)
    {
        var url = GetString(element, "baseUrl") ??
                  GetString(element, "base_url") ??
                  GetString(element, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var quality = element.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id)
            ? id
            : 0;
        var bandwidth = element.TryGetProperty("bandwidth", out var bandwidthElement) &&
                        bandwidthElement.TryGetInt64(out var value)
            ? value
            : 0;
        var codec = GetString(element, "codecs") ?? string.Empty;

        return new Track(quality, url, codec, bandwidth);
    }

    private static int CodecRank(string codec)
    {
        // Prefer AVC for Windows compatibility; fall back to HEVC/AV1 when AVC is absent.
        if (codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase)) return 0;
        if (codec.StartsWith("hev", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("hvc", StringComparison.OrdinalIgnoreCase)) return 1;
        if (codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string DecodeScriptString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }
        catch
        {
            return raw.Trim('"');
        }
    }

    private static async Task<Microsoft.UI.Xaml.Controls.WebView2?> GetNativeWebViewAsync(
        WebView webView,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60 && webView.Handler?.PlatformView is null; attempt++)
        {
            await Task.Delay(100, cancellationToken);
        }

        if (webView.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 nativeWebView)
        {
            return null;
        }

        await nativeWebView.EnsureCoreWebView2Async();
        return nativeWebView;
    }

    private sealed class VideoContext
    {
        public long Aid { get; set; }
        public long Cid { get; set; }
        public string Bvid { get; set; } = string.Empty;
    }

    private sealed record Track(int Quality, string Url, string Codec, long Bandwidth);
#endif
}
