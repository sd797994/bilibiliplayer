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
        CancellationToken cancellationToken = default,
        int pageNumber = 1)
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

            var context = await ReadVideoContextAsync(core, bvid, pageNumber, cancellationToken);

            // Danmaku uses the same logged-in page session as playurl. Fetch it in parallel so
            // opening a video is still gated on the slower of the two, not their sum.
            var danmakuTask = FetchDanmakuInPageAsync(core, context.Cid, cancellationToken);
            var subtitlesTask = FetchSubtitlesInPageAsync(
                core,
                context.Bvid,
                context.Aid,
                context.Cid,
                cancellationToken);
            var commentsTask = FetchCommentsInPageAsync(
                core,
                context.Aid,
                context.OwnerMid,
                context.ReplyCount,
                cancellationToken);

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

            PlaybackManifest? manifest = null;
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
                    var parsed = ParsePlaybackManifest(playInfoJson, requestQuality);
                    if (parsed.Sources.Count > 0)
                    {
                        manifest = parsed;
                        break;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Try the next lower quality before falling back to the page's own play info.
                }
            }

            // Fallback to the play information already produced by Bilibili's own page.
            if (manifest is null)
            {
                var pagePlayInfoJson = await TryReadPagePlayInfoAsync(core, context);
                if (!string.IsNullOrWhiteSpace(pagePlayInfoJson))
                {
                    manifest = ParsePlaybackManifest(pagePlayInfoJson, maximumQuality);
                }
            }

            if (manifest is null || manifest.Sources.Count == 0)
            {
                throw new InvalidOperationException(
                    "没有拿到播放地址。请确认视频在当前账号中可以正常观看，然后重试。");
            }

            IReadOnlyList<DanmakuComment> danmaku = Array.Empty<DanmakuComment>();
            var subtitles = new SubtitleSnapshot
            {
                Bvid = context.Bvid,
                Aid = context.Aid,
                Cid = context.Cid,
                Error = "字幕加载失败，请重新打开视频重试"
            };
            var comments = new VideoCommentSnapshot { TotalCount = context.ReplyCount };
            try
            {
                danmaku = await danmakuTask;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Playback must not depend on danmaku. An empty list just hides the overlay.
            }

            try
            {
                subtitles = await subtitlesTask;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Subtitles are optional and must never prevent video playback.
            }

            try
            {
                comments = await commentsTask;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Comments are optional and must never prevent video playback.
            }

            return WithCommunityData(
                manifest,
                context.Bvid,
                context.Aid,
                context.Cid,
                context.PageNumber,
                context.Parts,
                context.OwnerMid,
                danmaku,
                subtitles,
                comments);
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
        int pageNumber,
        CancellationToken cancellationToken)
    {
        pageNumber = Math.Max(1, pageNumber);
        core.Navigate($"https://www.bilibili.com/video/{Uri.EscapeDataString(bvid)}/?p={pageNumber}&autoplay=0");

        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var raw = await core.ExecuteScriptAsync(
                    $$"""
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
                        const parts = (Array.isArray(data.pages) ? data.pages : [])
                            .map((item, index) => ({
                                cid: Number(item.cid),
                                page: Number(item.page) || index + 1,
                                part: String(item.part || `P${index + 1}`),
                                duration: Math.max(0, Number(item.duration) || 0)
                            }))
                            .filter(item => Number.isSafeInteger(item.cid) && item.cid > 0 &&
                                Number.isSafeInteger(item.page) && item.page > 0);
                        const selected = parts.find(item => item.page === {{pageNumber}}) ?? parts[0];
                        // videoData.cid can still refer to P1 even on a ?p=2 page.
                        const cid = selected?.cid ?? data.cid ?? state.cid;
                        const pageNumber = selected?.page ?? 1;
                        if (parts.length === 0 && cid) {
                            parts.push({ cid, page: 1, part: String(data.title || '正片'), duration: Number(data.duration) || 0 });
                        }
                        const aid = data.aid ?? data.id ?? 0;
                        const bvid = data.bvid ?? '';
                        const ownerMid = data.owner?.mid ?? 0;
                        const replyCount = data.stat?.reply ?? 0;
                        if (!cid || !bvid) return '';
                        return JSON.stringify({ aid, cid, bvid, pageNumber, parts, ownerMid, replyCount });
                    })()
                    """);

                var json = DecodeScriptString(raw);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var context = JsonSerializer.Deserialize<VideoContext>(
                        json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    // ExecuteScriptAsync can still see the previous document during navigation.
                    if (context is not null && context.Aid > 0 && context.Cid > 0 &&
                        string.Equals(context.Bvid, bvid, StringComparison.Ordinal))
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

    private static async Task<IReadOnlyList<DanmakuComment>> FetchDanmakuInPageAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        long cid,
        CancellationToken cancellationToken)
    {
        if (cid <= 0)
        {
            return Array.Empty<DanmakuComment>();
        }

        var requestId = Guid.NewGuid().ToString("N");
        var prefix = $"BILI_DANMAKU:{requestId}:";
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
            var prefixJson = JsonSerializer.Serialize(prefix);
            var script = $$"""
                (() => {
                    const prefix = {{prefixJson}};
                    const post = payload => {
                        try { chrome.webview.postMessage(prefix + JSON.stringify(payload)); } catch (_) {}
                    };

                    fetch('https://api.bilibili.com/x/v1/dm/list.so?oid={{cid}}', {
                        method: 'GET',
                        credentials: 'include',
                        cache: 'no-store'
                    })
                    .then(async response => {
                        const xml = await response.text();
                        const doc = new DOMParser().parseFromString(xml, 'text/xml');
                        const nodes = doc.getElementsByTagName('d');
                        const items = [];
                        for (let i = 0; i < nodes.length && items.length < 4000; i++) {
                            const el = nodes[i];
                            const parts = (el.getAttribute('p') || '').split(',');
                            const mode = Number(parts[1]) || 1;
                            const text = (el.textContent || '').trim();
                            if (!text || mode >= 7) continue;
                            items.push({
                                t: Number(parts[0]) || 0,
                                m: mode,
                                c: Number(parts[3]) || 16777215,
                                x: text.slice(0, 80)
                            });
                        }
                        items.sort((a, b) => a.t - b.t);
                        post({ ok: true, items });
                    })
                    .catch(error => post({ ok: false, error: String(error), items: [] }));
                })()
                """;

            await core.ExecuteScriptAsync(script);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
            var completedTask = await Task.WhenAny(completion.Task, timeoutTask);
            if (completedTask != completion.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Array.Empty<DanmakuComment>();
            }

            var envelopeJson = await completion.Task;
            return ParseDanmakuItems(envelopeJson);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<DanmakuComment>();
        }
        finally
        {
            core.WebMessageReceived -= OnWebMessageReceived;
        }
    }

    private static IReadOnlyList<DanmakuComment> ParseDanmakuItems(string? envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson))
        {
            return Array.Empty<DanmakuComment>();
        }

        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            if (!document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<DanmakuComment>();
            }

            return JsonSerializer.Deserialize<List<DanmakuComment>>(items.GetRawText())
                   ?? new List<DanmakuComment>();
        }
        catch
        {
            return Array.Empty<DanmakuComment>();
        }
    }

    private static async Task<SubtitleSnapshot> FetchSubtitlesInPageAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        string bvid,
        long aid,
        long cid,
        CancellationToken cancellationToken)
    {
        SubtitleSnapshot Failure(string error) => new()
        {
            Bvid = bvid,
            Aid = aid,
            Cid = cid,
            Error = error
        };

        if (string.IsNullOrWhiteSpace(bvid) || aid <= 0 || cid <= 0)
        {
            return Failure("无法确认字幕所属视频，已停止加载");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var prefix = $"BILI_SUBTITLES:{requestId}:";
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
            var prefixJson = JsonSerializer.Serialize(prefix);
            var bvidJson = JsonSerializer.Serialize(bvid);
            var script = $$"""
                (() => {
                    const prefix = {{prefixJson}};
                    const identity = { bvid: {{bvidJson}}, aid: {{aid}}, cid: {{cid}} };
                    const mismatch = '字幕与当前视频不匹配，已阻止加载';
                    const failed = '字幕加载失败，请重新打开视频重试';
                    const post = payload => {
                        try { chrome.webview.postMessage(prefix + JSON.stringify({ ...payload, ...identity })); } catch (_) {}
                    };

                    const readJson = async (url, authenticated = false) => {
                        const controller = new AbortController();
                        const timer = setTimeout(() => controller.abort(), 4000);
                        try {
                            const response = await fetch(url, {
                                method: 'GET',
                                // Only the API needs the login session. Subtitle CDN responses may
                                // use Access-Control-Allow-Origin: *, which rejects credentialed reads.
                                credentials: authenticated ? 'include' : 'omit',
                                cache: 'no-store',
                                signal: controller.signal
                            });
                            const text = await response.text();
                            let payload;
                            try { payload = JSON.parse(text.replace(/^\uFEFF/, '')); }
                            catch (_) { throw new Error('字幕接口返回了无法识别的数据'); }
                            if (!response.ok) throw new Error(`HTTP ${response.status}`);
                            return payload;
                        } finally {
                            clearTimeout(timer);
                        }
                    };

                    const verifiedUrl = value => {
                        let valueText = String(value || '').trim();
                        if (!valueText) throw new Error(failed);
                        if (valueText.startsWith('//')) valueText = 'https:' + valueText;
                        const url = new URL(valueText);
                        if (url.protocol === 'http:') url.protocol = 'https:';
                        if (url.protocol !== 'https:' || url.username || url.password || url.port ||
                            !/(^|\.)(hdslb|bilibili)\.com$/i.test(url.hostname)) throw new Error(mismatch);

                        // Current AI production filenames encode aid + cid + a 32-digit hash.
                        // Reject a different video's file even when metadata claims our cid.
                        const aiPrefix = '/bfs/ai_subtitle/prod/';
                        if (url.pathname.startsWith(aiPrefix)) {
                            const expected = new RegExp('^' + identity.aid + identity.cid + '[a-f0-9]{32}(?:\\.json)?$', 'i');
                            if (!expected.test(url.pathname.slice(aiPrefix.length))) throw new Error(mismatch);
                        }
                        return url.href;
                    };

                    const readMetadata = async (url, requireIdentity) => {
                        const payload = await readJson(url, true);
                        if (Number(payload?.code) !== 0 || !payload?.data) throw new Error(failed);
                        const data = payload.data;
                        const returned = { bvid: data.bvid, aid: data.aid, cid: data.cid ?? data.oid };
                        for (const key of ['bvid', 'aid', 'cid']) {
                            if ((requireIdentity || returned[key] != null) &&
                                String(returned[key]) !== String(identity[key])) throw new Error(mismatch);
                        }
                        return data;
                    };

                    const simplifyCues = body => {
                        const cues = [];
                        for (const item of Array.isArray(body) ? body : []) {
                            const from = Number(item?.from);
                            const rawTo = Number(item?.to);
                            const content = String(item?.content || '').trim();
                            if (!Number.isFinite(from) || !content) continue;
                            const to = Number.isFinite(rawTo) && rawTo > from ? rawTo : from + 2;
                            cues.push({
                                f: Math.max(0, from),
                                t: Math.max(0, to),
                                c: content.slice(0, 500),
                                l: Number(item?.location) || 2
                            });
                            if (cues.length >= 12000) break;
                        }
                        cues.sort((a, b) => a.f - b.f || a.t - b.t);
                        return cues;
                    };

                    (async () => {
                            // The legacy /x/player/v2 list can point at unrelated AI subtitles.
                            // Read the cid-scoped subtitle configuration used by the DM service first.
                            const dmParams = new URLSearchParams({ aid: String(identity.aid), oid: String(identity.cid), type: '1' });
                            let data;
                            try {
                                data = await readMetadata('https://api.bilibili.com/x/v2/dm/view?' + dmParams, false);
                            } catch (error) {
                                if (error.message === mismatch) throw error;
                            }
                            if (!Array.isArray(data?.subtitle?.subtitles) || data.subtitle.subtitles.length === 0) {
                                const params = new URLSearchParams({ bvid: identity.bvid, aid: String(identity.aid), cid: String(identity.cid) });
                                try {
                                    data = await readMetadata('https://api.bilibili.com/x/player/wbi/v2?' + params, true);
                                } catch (error) {
                                    // A valid empty DM configuration is still useful when fallback is unavailable.
                                    if (!data || error.message === mismatch) throw error;
                                }
                            }
                            const subtitle = data.subtitle || {};
                            const sourceTracks = Array.isArray(subtitle.subtitles)
                                ? subtitle.subtitles.slice(0, 16)
                                : [];
                            const preferredLanguage = String(subtitle.lan || '');
                            let trackError = '';
                            const tracks = await Promise.all(sourceTracks.map(async (item, index) => {
                                try {
                                    const url = verifiedUrl(item?.subtitle_url);
                                    const content = await readJson(url);
                                    const language = String(item?.lan || '');
                                    const name = String(item?.lan_doc || language || `字幕 ${index + 1}`);
                                    const cues = simplifyCues(content?.body);
                                    if (cues.length === 0) throw new Error(failed);
                                    return {
                                        id: String(item?.id_str || item?.id || index),
                                        lan: language,
                                        name: name.slice(0, 80),
                                        ai: Number(item?.type) === 1 || /^ai-/i.test(language) || /AI|自动/.test(name),
                                        def: preferredLanguage
                                            ? language === preferredLanguage
                                            : index === 0,
                                        cues
                                    };
                                } catch (error) {
                                    if (error.message === mismatch || !trackError) {
                                        trackError = error.message === mismatch ? mismatch : failed;
                                    }
                                    return null;
                                }
                            }));

                            post({
                                tracks: tracks.filter(Boolean),
                                login: !!data.need_login_subtitle && sourceTracks.length === 0,
                                error: tracks.some(Boolean) ? '' : trackError
                            });
                        })().catch(error => post({ tracks: [], login: false,
                            error: error.message === mismatch ? mismatch : failed }));
                })()
                """;

            await core.ExecuteScriptAsync(script);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(14), cancellationToken);
            var completedTask = await Task.WhenAny(completion.Task, timeoutTask);
            if (completedTask != completion.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Failure("字幕加载超时，请重新打开视频重试");
            }

            var json = await completion.Task;
            var snapshot = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SubtitleSnapshot>(json);
            return snapshot is not null && snapshot.Bvid == bvid && snapshot.Aid == aid && snapshot.Cid == cid
                ? snapshot
                : Failure("字幕与当前视频不匹配，已阻止加载");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Failure("字幕加载失败，请重新打开视频重试");
        }
        finally
        {
            core.WebMessageReceived -= OnWebMessageReceived;
        }
    }

    private static async Task<VideoCommentSnapshot> FetchCommentsInPageAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        long aid,
        long ownerMid,
        int knownTotal,
        CancellationToken cancellationToken)
    {
        if (aid <= 0)
        {
            return new VideoCommentSnapshot
            {
                TotalCount = Math.Max(0, knownTotal),
                HotError = "未取得视频 aid",
                LatestError = "未取得视频 aid"
            };
        }

        var requestId = Guid.NewGuid().ToString("N");
        var prefix = $"BILI_COMMENTS:{requestId}:";
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
            var prefixJson = JsonSerializer.Serialize(prefix);
            var ownerMidJson = JsonSerializer.Serialize(ownerMid.ToString());
            var script = $$"""
                (() => {
                    const prefix = {{prefixJson}};
                    const aid = '{{aid}}';
                    const ownerMid = {{ownerMidJson}};
                    const knownTotal = {{Math.Max(0, knownTotal)}};
                    const post = payload => {
                        try { chrome.webview.postMessage(prefix + JSON.stringify(payload)); } catch (_) {}
                    };

                    const cleanUrl = value => {
                        const url = String(value || '').trim();
                        if (!url) return '';
                        if (url.startsWith('//')) return 'https:' + url;
                        if (url.startsWith('http://')) return 'https://' + url.slice(7);
                        return url;
                    };

                    const simplify = (reply, includeChildren) => {
                        const content = reply?.content || {};
                        const member = reply?.member || {};
                        const emotes = {};
                        for (const [text, value] of Object.entries(content.emote || {})) {
                            const url = cleanUrl(value?.url || value?.gif_url);
                            if (text && url) emotes[String(text)] = url;
                        }

                        const pictures = Array.isArray(content.pictures)
                            ? content.pictures
                                .map(item => cleanUrl(item?.img_src || item?.url || item?.img_url))
                                .filter(Boolean)
                                .slice(0, 9)
                            : [];
                        const children = includeChildren && Array.isArray(reply?.replies)
                            ? reply.replies.slice(0, 3).map(item => simplify(item, false))
                            : [];

                        return {
                            id: String(reply?.rpid_str || reply?.rpid || ''),
                            n: String(member?.uname || '哔哩哔哩用户').slice(0, 80),
                            a: cleanUrl(member?.avatar || member?.face),
                            m: String(content.message || '').slice(0, 1600),
                            t: Number(reply?.ctime) || 0,
                            l: Number(reply?.like) || 0,
                            r: Number(reply?.rcount) || 0,
                            p: pictures,
                            e: emotes,
                            u: ownerMid !== '0' && String(member?.mid || '') === ownerMid,
                            c: children
                        };
                    };

                    const readJson = async url => {
                        const response = await fetch(url, {
                            method: 'GET',
                            credentials: 'include',
                            cache: 'no-store'
                        });
                        const text = await response.text();
                        let payload;
                        try { payload = JSON.parse(text); }
                        catch (_) { throw new Error('评论接口返回了无法识别的数据'); }
                        if (!response.ok || Number(payload?.code) !== 0) {
                            throw new Error(String(payload?.message || payload?.msg || `HTTP ${response.status}`));
                        }
                        return payload;
                    };

                    const mergeUnique = (target, replies) => {
                        const known = new Set(target.map(item => String(item?.rpid_str || item?.rpid || '')));
                        for (const reply of replies || []) {
                            const id = String(reply?.rpid_str || reply?.rpid || '');
                            if (id && known.has(id)) continue;
                            target.push(reply);
                            if (id) known.add(id);
                        }
                    };

                    const fetchMain = async mode => {
                        const replies = [];
                        let next = 0;
                        let total = knownTotal;
                        let isEnd = false;
                        let fallbackPage = 1;
                        for (let page = 0; page < 3; page++) {
                            const params = new URLSearchParams({
                                oid: aid,
                                type: '1',
                                mode: String(mode),
                                next: String(next),
                                plat: '1',
                                ps: '20',
                                web_location: '1315875'
                            });
                            const payload = await readJson('https://api.bilibili.com/x/v2/reply/main?' + params.toString());
                            const data = payload?.data || {};
                            const batch = Array.isArray(data.replies) ? data.replies : [];
                            if (page === 0 && mode === 3 && data.top) {
                                mergeUnique(replies, [data.top.upper, data.top.admin, data.top.vote].filter(Boolean));
                            }
                            mergeUnique(replies, batch);
                            total = Number(data.cursor?.all_count ?? data.page?.count ?? total) || total;
                            fallbackPage = page + 2;
                            isEnd = !!data.cursor?.is_end || batch.length === 0;
                            if (isEnd) break;
                            const candidate = Number(data.cursor?.next);
                            if (!Number.isFinite(candidate) || candidate === next) {
                                isEnd = true;
                                break;
                            }
                            next = candidate;
                        }
                        return {
                            items: replies.slice(0, 60).map(item => simplify(item, true)),
                            total,
                            paging: { source: 'main', next: String(next), page: fallbackPage, end: isEnd }
                        };
                    };

                    const fetchLegacy = async mode => {
                        const replies = [];
                        let total = knownTotal;
                        const sort = mode === 3 ? 2 : 0;
                        let nextPage = 1;
                        let isEnd = false;
                        for (let page = 1; page <= 3; page++) {
                            const params = new URLSearchParams({
                                oid: aid,
                                type: '1',
                                sort: String(sort),
                                pn: String(page),
                                ps: '20',
                                nohot: mode === 3 ? '0' : '1'
                            });
                            const payload = await readJson('https://api.bilibili.com/x/v2/reply?' + params.toString());
                            const data = payload?.data || {};
                            const batch = Array.isArray(data.replies) ? data.replies : [];
                            if (page === 1 && mode === 3 && data.top) {
                                mergeUnique(replies, [data.top.upper, data.top.admin, data.top.vote].filter(Boolean));
                            }
                            mergeUnique(replies, batch);
                            total = Number(data.page?.count ?? data.cursor?.all_count ?? total) || total;
                            nextPage = page + 1;
                            isEnd = batch.length < 20;
                            if (isEnd) break;
                        }
                        return {
                            items: replies.slice(0, 60).map(item => simplify(item, true)),
                            total,
                            paging: { source: 'legacy', next: '', page: nextPage, end: isEnd }
                        };
                    };

                    const fetchMode = async mode => {
                        try { return await fetchMain(mode); }
                        catch (mainError) {
                            try { return await fetchLegacy(mode); }
                            catch (legacyError) {
                                const message = legacyError?.message || mainError?.message || '评论读取失败';
                                return {
                                    items: [],
                                    total: knownTotal,
                                    error: String(message),
                                    paging: { source: 'main', next: '0', page: 1, end: false }
                                };
                            }
                        }
                    };

                    Promise.all([fetchMode(3), fetchMode(2)])
                        .then(([hot, latest]) => post({
                            total: Math.max(knownTotal, hot.total || 0, latest.total || 0),
                            hot: hot.items || [],
                            latest: latest.items || [],
                            hotError: hot.error || '',
                            latestError: latest.error || '',
                            hotPaging: hot.paging,
                            latestPaging: latest.paging
                        }))
                        .catch(error => post({
                            total: knownTotal,
                            hot: [],
                            latest: [],
                            hotError: String(error),
                            latestError: String(error)
                        }));
                })()
                """;

            await core.ExecuteScriptAsync(script);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            var completedTask = await Task.WhenAny(completion.Task, timeoutTask);
            if (completedTask != completion.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new VideoCommentSnapshot
                {
                    TotalCount = Math.Max(0, knownTotal),
                    HotError = "评论读取超时",
                    LatestError = "评论读取超时"
                };
            }

            var json = await completion.Task;
            return string.IsNullOrWhiteSpace(json)
                ? new VideoCommentSnapshot { TotalCount = Math.Max(0, knownTotal) }
                : JsonSerializer.Deserialize<VideoCommentSnapshot>(json) ??
                  new VideoCommentSnapshot { TotalCount = Math.Max(0, knownTotal) };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new VideoCommentSnapshot
            {
                TotalCount = Math.Max(0, knownTotal),
                HotError = ex.Message,
                LatestError = ex.Message
            };
        }
        finally
        {
            core.WebMessageReceived -= OnWebMessageReceived;
        }
    }

    private static PlaybackManifest WithCommunityData(
        PlaybackManifest manifest,
        string bvid,
        long aid,
        long cid,
        int pageNumber,
        IReadOnlyList<VideoPart> parts,
        long ownerMid,
        IReadOnlyList<DanmakuComment> danmaku,
        SubtitleSnapshot subtitles,
        VideoCommentSnapshot comments) =>
        new()
        {
            Bvid = bvid,
            Aid = aid,
            Cid = cid,
            PageNumber = pageNumber,
            Parts = parts,
            OwnerMid = ownerMid,
            Sources = manifest.Sources,
            AvailableQualities = manifest.AvailableQualities,
            Danmaku = danmaku,
            Subtitles = subtitles,
            Comments = comments
        };

    private static async Task<string?> TryReadPagePlayInfoAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        VideoContext context)
    {
        try
        {
            var raw = await core.ExecuteScriptAsync(
                $$"""
                (() => {
                    const info = window.__playinfo__;
                    const data = info?.data ?? info?.result;
                    // For a multi-P video the fallback must prove ownership with its cid.
                    // last_play_cid is watch history, not the identity of these media tracks.
                    return JSON.stringify({{context.Parts.Count}} <= 1 || Number(data?.cid) === {{context.Cid}}
                        ? info : null);
                })()
                """);
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
            var audioUrls = Array.Empty<string>();
            if (dashElement.TryGetProperty("audio", out var audioArray) &&
                audioArray.ValueKind == JsonValueKind.Array)
            {
                var audioTracks = audioArray.EnumerateArray()
                    .Select(ParseTrack)
                    .Where(track => track is not null && !string.IsNullOrWhiteSpace(track.Url))
                    .Select(track => track!)
                    // HTML5 can play AAC/Opus. Highest-bandwidth is often Dolby/Hi-Res, which
                    // the local <audio>/<video> element rejects with MediaError 4.
                    .OrderBy(track => AudioCodecRank(track.Codec))
                    .ThenByDescending(track => track.Bandwidth)
                    .ToList();

                selectedAudio = audioTracks.FirstOrDefault();
                audioUrls = audioTracks
                    .SelectMany(track => track.Urls)
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(UrlRank)
                    .ToArray();
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
                    AudioUrls = audioUrls,
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
        var urls = ReadUrls(element);
        if (urls.Count == 0)
        {
            return null;
        }

        var rankedUrls = urls
            .OrderBy(UrlRank)
            .ThenBy(static url => url, StringComparer.Ordinal)
            .ToArray();
        var url = rankedUrls[0];

        var quality = element.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id)
            ? id
            : 0;
        var bandwidth = element.TryGetProperty("bandwidth", out var bandwidthElement) &&
                        bandwidthElement.TryGetInt64(out var value)
            ? value
            : 0;
        var codec = GetString(element, "codecs") ?? string.Empty;

        return new Track(quality, url, codec, bandwidth, rankedUrls);
    }

    private static IReadOnlyList<string> ReadUrls(JsonElement element)
    {
        var urls = new List<string>();

        void Add(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) &&
                !urls.Contains(url, StringComparer.Ordinal))
            {
                urls.Add(url);
            }
        }

        Add(GetString(element, "baseUrl"));
        Add(GetString(element, "base_url"));
        Add(GetString(element, "url"));

        foreach (var propertyName in new[] { "backupUrl", "backup_url" })
        {
            if (!element.TryGetProperty(propertyName, out var backups) ||
                backups.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in backups.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    Add(item.GetString());
                }
            }
        }

        return urls;
    }

    private static int UrlRank(string url)
    {
        // PCDN hosts (mcdn / szbdyd, often :8082) frequently fail inside WebView2 media
        // elements with MediaError 4 even though official upos mirrors of the same file work.
        if (url.Contains("mcdn.", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("szbdyd.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".bilivideo.cn:8082", StringComparison.OrdinalIgnoreCase))
        {
            return 20;
        }

        if (url.Contains("upos-", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (url.Contains("bilivideo.com", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 10;
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

    private static int AudioCodecRank(string codec)
    {
        if (codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)) return 0;
        if (codec.Contains("opus", StringComparison.OrdinalIgnoreCase)) return 1;
        if (codec.Contains("flac", StringComparison.OrdinalIgnoreCase)) return 8;
        if (codec.Contains("ec-3", StringComparison.OrdinalIgnoreCase) ||
            codec.Contains("ac-3", StringComparison.OrdinalIgnoreCase) ||
            codec.Contains("ec3", StringComparison.OrdinalIgnoreCase) ||
            codec.Contains("ac3", StringComparison.OrdinalIgnoreCase))
        {
            return 9;
        }

        return 5;
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
        public int PageNumber { get; set; } = 1;
        public List<VideoPart> Parts { get; set; } = [];
        public string Bvid { get; set; } = string.Empty;
        public long OwnerMid { get; set; }
        public int ReplyCount { get; set; }
    }

    private sealed record Track(
        int Quality,
        string Url,
        string Codec,
        long Bandwidth,
        IReadOnlyList<string> Urls);
#endif
}
