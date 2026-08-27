using System.Globalization;
using System.Text.Json;
using BiliBiliPlayer.Models;
using BiliBiliPlayer.Services;

namespace BiliBiliPlayer.Views;

public partial class PlayerPage : ContentPage
{
    private const int MaximumPreferredQuality = 80;
    private const string DanmakuPreferenceKey = "bili.player.danmaku.enabled";
    private const string VolumePreferenceKey = "bili.player.volume";

    private readonly VideoItem _video;
    private readonly BiliPlaybackResolver _resolver = new();
    private readonly BiliCommentService _commentService = new();
    private readonly CancellationTokenSource _pageCancellation = new();
    private readonly SemaphoreSlim _hotCommentLoadGate = new(1, 1);
    private readonly SemaphoreSlim _latestCommentLoadGate = new(1, 1);
    private readonly string _cookieHeader;
    private bool _isClosing;
    private bool _playerReady;
    private bool _hasStarted;
    private bool _isResolving;
    private bool _danmakuEnabled;
    private double _volume = 1d;
    private int _selectedQuality;
    private PlaybackManifest? _playbackManifest;
    private VideoCommentSnapshot? _commentSnapshot;
    private WebView? _resolverWebView;
#if WINDOWS
    private bool _nativePlayerEventsAttached;
    private Microsoft.UI.Xaml.Controls.WebView2? _nativePlayerWebView;
#endif

    public PlayerPage(VideoItem video, string cookieHeader = "")
    {
        InitializeComponent();

        _video = video;
        _cookieHeader = cookieHeader;
        TitleLabel.Text = video.Title;
        MetaLabel.Text = $"{video.OwnerName}  ·  {video.Bvid}";
        _danmakuEnabled = Preferences.Default.Get(DanmakuPreferenceKey, true);
        _volume = Math.Clamp(Preferences.Default.Get(VolumePreferenceKey, 1d), 0d, 1d);
        UpdateQualityUi(0);
        UpdateDanmakuButton();
        PlayerWebView.Source = new HtmlWebViewSource { Html = LocalPlayerHtml };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_hasStarted || _isClosing)
        {
            return;
        }

        _hasStarted = true;
        var resumePosition = PlaybackProgressStore.GetPosition(_video.Bvid);
        await ResolveAndPlayAsync(
            MaximumPreferredQuality,
            preservePosition: false,
            autoSelectHighest: true,
            initialPosition: resumePosition);
    }

#if WINDOWS
    private async void OnPlayerWebViewLoaded(object? sender, EventArgs e)
    {
        if (_nativePlayerEventsAttached ||
            PlayerWebView.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 nativeWebView)
        {
            return;
        }

        try
        {
            await nativeWebView.EnsureCoreWebView2Async();
            var core = nativeWebView.CoreWebView2;
            core.AddWebResourceRequestedFilter(
                "*",
                Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnPlayerWebResourceRequested;
            core.WebMessageReceived += OnPlayerWebMessageReceived;

            _nativePlayerWebView = nativeWebView;
            _nativePlayerEventsAttached = true;
        }
        catch
        {
            // The MAUI WebView remains usable; request-header injection may be unavailable.
        }
    }

    private void OnPlayerWebResourceRequested(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        // Bilibili CDN URLs are short-lived signed URLs. Keep the request in the same
        // first-party context that the website normally uses.
        args.Request.Headers.SetHeader("Referer", $"https://www.bilibili.com/video/{_video.Bvid}/");
        args.Request.Headers.SetHeader("Origin", "https://www.bilibili.com");
    }

    private void OnPlayerWebMessageReceived(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var message = args.TryGetWebMessageAsString();
            if (message.StartsWith("LOCAL_PLAYER_DANMAKU:", StringComparison.Ordinal))
            {
                var enabled = message.EndsWith("1", StringComparison.Ordinal);
                Dispatcher.Dispatch(() =>
                {
                    _danmakuEnabled = enabled;
                    Preferences.Default.Set(DanmakuPreferenceKey, enabled);
                    UpdateDanmakuButton();
                });
                return;
            }

            if (message.StartsWith("LOCAL_PLAYER_VOLUME:", StringComparison.Ordinal) &&
                double.TryParse(
                    message["LOCAL_PLAYER_VOLUME:".Length..],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var volume))
            {
                var clamped = Math.Clamp(volume, 0d, 1d);
                Dispatcher.Dispatch(() =>
                {
                    _volume = clamped;
                    Preferences.Default.Set(VolumePreferenceKey, clamped);
                });
                return;
            }

            if (message.StartsWith("LOCAL_PLAYER_COMMENTS_MORE:", StringComparison.Ordinal))
            {
                var mode = message["LOCAL_PLAYER_COMMENTS_MORE:".Length..];
                _ = LoadMoreCommentsAsync(mode);
                return;
            }

            if (!message.StartsWith("LOCAL_PLAYER_ERROR:", StringComparison.Ordinal))
            {
                return;
            }

            Dispatcher.Dispatch(() =>
            {
                // Signed CDN URLs may have expired or been rejected. Force the next Retry to
                // create a fresh temporary resolver instead of reusing the in-memory snapshot.
                _playbackManifest = null;
                _commentSnapshot = null;
                PlaybackStatusLabel.Text = "播放失败";
                LoadingLabel.Text = message["LOCAL_PLAYER_ERROR:".Length..];
                LoadingIndicator.IsRunning = false;
                RetryButton.IsVisible = true;
                PlayerLoadingOverlay.IsVisible = true;
            });
        }
        catch
        {
            // Ignore malformed messages from the local page.
        }
    }
#else
    private void OnPlayerWebViewLoaded(object? sender, EventArgs e)
    {
    }
#endif

    private void OnPlayerNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result == WebNavigationResult.Success)
        {
            _playerReady = true;
            return;
        }

        LoadingLabel.Text = "本地播放器初始化失败";
        LoadingIndicator.IsRunning = false;
        RetryButton.IsVisible = true;
    }

    private void OnQualityMenuClicked(object? sender, EventArgs e)
    {
        if (_isResolving || _isClosing)
        {
            return;
        }

        ShareOverlay.IsVisible = false;
        QualityMenu.IsVisible = !QualityMenu.IsVisible;
    }

    private async void OnDanmakuClicked(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        QualityMenu.IsVisible = false;
        ShareOverlay.IsVisible = false;
        _danmakuEnabled = !_danmakuEnabled;
        Preferences.Default.Set(DanmakuPreferenceKey, _danmakuEnabled);
        UpdateDanmakuButton();

        var visible = _danmakuEnabled ? "true" : "false";
        await ExecutePlayerScriptAsync(
            $"window.biliLocalPlayer?.setDanmakuVisible({visible}, false); 'ok'");
    }

    private void UpdateDanmakuButton()
    {
        DanmakuButton.Text = _danmakuEnabled ? "弹幕" : "弹幕关";
        DanmakuButton.TextColor = Color.FromArgb(_danmakuEnabled ? "#FB7299" : "#8A909C");
    }

    private void OnShareClicked(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        QualityMenu.IsVisible = false;
        if (ShareOverlay.IsVisible)
        {
            ShareOverlay.IsVisible = false;
            return;
        }

        BindShareCard();
        ShareOverlay.IsVisible = true;
    }

    private void OnShareOverlayTapped(object? sender, EventArgs e)
    {
        ShareOverlay.IsVisible = false;
    }

    private async void OnShareToWeChatClicked(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        ShareToWeChatButton.IsEnabled = false;
        RecommendationEntry.IsEnabled = false;
        ShareToWeChatButton.Text = "发送中...";
        ShareHintLabel.TextColor = Color.FromArgb("#9AA0AC");
        ShareHintLabel.Text = "正在生成并发送微信小卡片...";
        try
        {
            var result = await VideoShareService.SendToWeChatAsync(
                _video,
                RecommendationEntry.Text,
                _pageCancellation.Token);
            ShareHintLabel.Text = result.Message;
            ShareHintLabel.TextColor = Color.FromArgb(result.Success ? "#55C58A" : "#FF7B86");
            if (result.Success)
            {
                RecommendationEntry.Text = string.Empty;
            }
        }
        catch (Exception ex)
        {
            ShareHintLabel.Text = $"微信分享失败：{ex.Message}";
            ShareHintLabel.TextColor = Color.FromArgb("#FF7B86");
        }
        finally
        {
            ShareToWeChatButton.IsEnabled = true;
            RecommendationEntry.IsEnabled = true;
            ShareToWeChatButton.Text = "发送到微信群";
        }
    }

    private void BindShareCard()
    {
        ShareCoverImage.Source = string.IsNullOrWhiteSpace(_video.CoverUrl)
            ? null
            : _video.CoverUrl;
        ShareTitleLabel.Text = string.IsNullOrWhiteSpace(_video.Title)
            ? _video.Bvid
            : _video.Title;
        ShareMetaLabel.Text = $"UP主：{_video.OwnerName}\n播放：{_video.ViewCountText}";
        ShareDurationLabel.Text = _video.DurationText;
        ShareHintLabel.TextColor = Color.FromArgb("#9AA0AC");
        ShareHintLabel.Text = "将直接发送到固定微信群。";
    }

    private async void OnQualityOptionClicked(object? sender, EventArgs e)
    {
        if (_isResolving || _isClosing || sender is not Button button ||
            !int.TryParse(button.CommandParameter?.ToString(), out var quality))
        {
            return;
        }

        QualityMenu.IsVisible = false;
        if (quality == _selectedQuality)
        {
            return;
        }

        _selectedQuality = quality;
        UpdateQualityUi(quality);
        await ResolveAndPlayAsync(quality, preservePosition: true);
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
    {
        _playbackManifest = null;
        var hasPreviousQuality = _selectedQuality > 0;
        await ResolveAndPlayAsync(
            hasPreviousQuality ? _selectedQuality : MaximumPreferredQuality,
            preservePosition: true,
            autoSelectHighest: !hasPreviousQuality);
    }

    private async Task ResolveAndPlayAsync(
        int quality,
        bool preservePosition,
        bool autoSelectHighest = false,
        double initialPosition = 0d)
    {
        if (_isResolving || _isClosing)
        {
            return;
        }

        _isResolving = true;
        QualityMenu.IsVisible = false;
        QualityMenuButton.IsEnabled = false;
        RetryButton.IsVisible = false;
        LoadingIndicator.IsRunning = true;
        PlayerLoadingOverlay.IsVisible = true;

        var needsResolution = _playbackManifest is null;
        LoadingLabel.Text = needsResolution
            ? (autoSelectHighest ? "正在检测最高可用画质…" : $"正在解析 {BiliQualityNames.GetLabel(quality)}…")
            : $"正在切换 {BiliQualityNames.GetLabel(quality)}…";
        PlaybackStatusLabel.Text = needsResolution ? "解析中" : "切换中";

        try
        {
            var position = preservePosition
                ? await GetPlaybackPositionAsync()
                : Math.Max(0d, initialPosition);

            if (_playbackManifest is null)
            {
                // Resolve once at our highest UI quality. The returned DASH snapshot contains
                // all lower representations, then the temporary resolver WebView2 is destroyed.
                _playbackManifest = await ResolveWithTemporaryWebViewAsync(
                    MaximumPreferredQuality,
                    _pageCancellation.Token);
                _commentSnapshot = _playbackManifest.Comments;
            }

            var requestedQuality = autoSelectHighest
                ? _playbackManifest.GetHighestAvailableQuality(MaximumPreferredQuality)
                : quality;

            if (requestedQuality <= 0)
            {
                throw new InvalidOperationException("当前视频没有可播放的清晰度。");
            }

            var source = _playbackManifest.GetBestMatch(requestedQuality);

            await WaitForPlayerReadyAsync(_pageCancellation.Token);
            await ApplyVolumeAsync(_volume);
            await LoadPlaybackSourceAsync(source, position);
            await ApplyDanmakuAsync(_playbackManifest.Danmaku, _danmakuEnabled);
            _commentSnapshot ??= _playbackManifest.Comments;
            await ApplyVideoCommentsAsync(_commentSnapshot);

            _selectedQuality = source.ActualQuality;
            UpdateQualityUi(source.ActualQuality, _playbackManifest.AvailableQualities);
            PlaybackStatusLabel.Text = autoSelectHighest || source.ActualQuality == quality
                ? source.QualityLabel
                : $"{source.QualityLabel} · 已回退";
            LoadingLabel.Text = $"正在缓冲 {source.QualityLabel}…";

            await Task.Delay(180, _pageCancellation.Token);
            PlayerLoadingOverlay.IsVisible = false;
        }
        catch (OperationCanceledException) when (_isClosing)
        {
        }
        catch (Exception ex)
        {
            _playbackManifest = null;
            PlaybackStatusLabel.Text = "解析失败";
            LoadingLabel.Text = ex.Message;
            LoadingIndicator.IsRunning = false;
            RetryButton.IsVisible = true;
            PlayerLoadingOverlay.IsVisible = true;
        }
        finally
        {
            _isResolving = false;
            QualityMenuButton.IsEnabled = !_isClosing;
        }
    }

    private async Task<PlaybackManifest> ResolveWithTemporaryWebViewAsync(
        int maximumQuality,
        CancellationToken cancellationToken)
    {
        if (_resolverWebView is not null)
        {
            await DestroyResolverWebViewAsync(_resolverWebView);
        }

        var resolverWebView = new WebView
        {
            Source = "about:blank",
            WidthRequest = 1,
            HeightRequest = 1,
            Opacity = 0.01,
            InputTransparent = true,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start
        };

        _resolverWebView = resolverWebView;
        ResolverHost.Children.Add(resolverWebView);

        try
        {
            // Yield once so MAUI can attach a platform handler/CoreWebView2 to the newly added view.
            await Task.Yield();
            return await _resolver.ResolveManifestAsync(
                resolverWebView,
                _video.Bvid,
                maximumQuality,
                cancellationToken);
        }
        finally
        {
            await DestroyResolverWebViewAsync(resolverWebView);
        }
    }

    private Task DestroyResolverWebViewAsync(WebView resolverWebView)
    {
        try
        {
#if WINDOWS
            if (resolverWebView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 nativeWebView &&
                nativeWebView.CoreWebView2 is not null)
            {
                // Stop the Bilibili document immediately. DisconnectHandler below tears down the
                // platform WebView2/renderer; navigating to blank also drops page references first.
                nativeWebView.CoreWebView2.IsMuted = true;
                nativeWebView.CoreWebView2.Stop();
                nativeWebView.CoreWebView2.Navigate("about:blank");
            }
#endif
            resolverWebView.Source = "about:blank";
        }
        catch
        {
            // The resolver may already be torn down during page close/cancellation.
        }

        try
        {
            if (ResolverHost.Children.Contains(resolverWebView))
            {
                ResolverHost.Children.Remove(resolverWebView);
            }
        }
        catch
        {
        }

        try
        {
            // Removing a MAUI view from the visual tree is not guaranteed to disconnect its
            // native handler immediately, so explicitly disconnect it to release WebView2.
            resolverWebView.Handler?.DisconnectHandler();
        }
        catch
        {
        }

        if (ReferenceEquals(_resolverWebView, resolverWebView))
        {
            _resolverWebView = null;
        }

        return Task.CompletedTask;
    }

    private void UpdateQualityUi(int selectedQuality, IReadOnlyList<int>? availableQualities = null)
    {
        QualityMenuButton.Text = $"{BiliQualityNames.GetLabel(selectedQuality)}  ▾";

        var items = new (int Quality, Button Button)[]
        {
            (80, Quality1080Button),
            (64, Quality720Button),
            (32, Quality480Button),
            (16, Quality360Button)
        };

        foreach (var item in items)
        {
            var selected = item.Quality == selectedQuality;
            item.Button.Text = selected
                ? $"✓  {BiliQualityNames.GetLabel(item.Quality)}"
                : BiliQualityNames.GetLabel(item.Quality);
            item.Button.TextColor = Color.FromArgb(selected ? "#FB7299" : "#EDEFF3");
            item.Button.BackgroundColor = Color.FromArgb(selected ? "#2A2D35" : "#001A1D23");

            var hasAvailabilitySnapshot = availableQualities is not null && availableQualities.Count > 0;
            var isAvailable = !hasAvailabilitySnapshot || availableQualities!.Contains(item.Quality);

            // Before resolution all four choices can be shown. Once the manifest is known, only
            // expose qualities that this video/account actually returned so the user cannot select
            // a non-existent 1080P entry on a 720P-only upload.
            item.Button.IsVisible = isAvailable;
            item.Button.IsEnabled = isAvailable;
            item.Button.Opacity = isAvailable ? 1d : 0d;
        }
    }

    private async Task WaitForPlayerReadyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40 && !_playerReady; attempt++)
        {
            await Task.Delay(100, cancellationToken);
        }

        if (!_playerReady)
        {
            throw new TimeoutException("本地播放器初始化超时。");
        }
    }

    private async Task LoadPlaybackSourceAsync(PlaybackSource source, double position)
    {
        var videoJson = JsonSerializer.Serialize(source.VideoUrl);
        var audioUrls = source.AudioUrls.Count > 0
            ? source.AudioUrls
            : string.IsNullOrWhiteSpace(source.AudioUrl)
                ? Array.Empty<string>()
                : new[] { source.AudioUrl };
        var audioJson = JsonSerializer.Serialize(audioUrls);
        var script = $$"""
            (() => {
                if (!window.biliLocalPlayer) return 'missing';
                window.biliLocalPlayer.load({{videoJson}}, {{audioJson}}, {{position.ToString(CultureInfo.InvariantCulture)}});
                return 'ok';
            })()
            """;

        var result = await ExecutePlayerScriptAsync(script);
        if (!result.Contains("ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("本地播放器尚未准备好接收播放地址。");
        }
    }

    private async Task ApplyDanmakuAsync(IReadOnlyList<DanmakuComment> comments, bool visible)
    {
        var commentsJson = JsonSerializer.Serialize(comments);
        var visibleJson = visible ? "true" : "false";
        var script = $$"""
            (() => {
                if (!window.biliLocalPlayer) return 'missing';
                window.biliLocalPlayer.setDanmaku({{commentsJson}});
                window.biliLocalPlayer.setDanmakuVisible({{visibleJson}}, false);
                return 'ok';
            })()
            """;
        await ExecutePlayerScriptAsync(script);
    }

    private async Task ApplyVideoCommentsAsync(VideoCommentSnapshot comments)
    {
        var commentsJson = JsonSerializer.Serialize(comments);
        var script = $$"""
            (() => {
                if (!window.biliLocalPlayer) return 'missing';
                window.biliLocalPlayer.setComments({{commentsJson}});
                return 'ok';
            })()
            """;
        await ExecutePlayerScriptAsync(script);
    }

    private async Task LoadMoreCommentsAsync(string requestedMode)
    {
        var mode = string.Equals(requestedMode, "latest", StringComparison.Ordinal)
            ? "latest"
            : "hot";
        var gate = mode == "latest" ? _latestCommentLoadGate : _hotCommentLoadGate;

        bool entered;
        try
        {
            entered = await gate.WaitAsync(0, _pageCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!entered)
        {
            return;
        }

        try
        {
            if (_isClosing)
            {
                return;
            }

            if (_playbackManifest is null || _commentSnapshot is null)
            {
                await SendCommentPageToPlayerAsync(mode, new VideoCommentPage
                {
                    Error = "评论分页尚未准备好。"
                });
                return;
            }

            var paging = mode == "latest"
                ? _commentSnapshot.LatestPaging
                : _commentSnapshot.HotPaging;
            if (paging.IsEnd)
            {
                await SendCommentPageToPlayerAsync(mode, new VideoCommentPage
                {
                    TotalCount = _commentSnapshot.TotalCount,
                    Paging = paging
                });
                return;
            }

            VideoCommentPage page;
            try
            {
                page = await _commentService.GetPageAsync(
                    _playbackManifest.Aid,
                    _playbackManifest.OwnerMid,
                    _video.Bvid,
                    mode,
                    paging,
                    _cookieHeader,
                    _pageCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                page = new VideoCommentPage
                {
                    TotalCount = _commentSnapshot.TotalCount,
                    Paging = paging,
                    Error = string.IsNullOrWhiteSpace(ex.Message) ? "评论加载失败，请重试。" : ex.Message
                };
            }

            if (_isClosing)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(page.Error))
            {
                MergeCommentPage(mode, page);
            }

            await SendCommentPageToPlayerAsync(mode, page);
        }
        finally
        {
            gate.Release();
        }
    }

    private void MergeCommentPage(string mode, VideoCommentPage page)
    {
        if (_commentSnapshot is null)
        {
            return;
        }

        var current = _commentSnapshot;
        var merged = MergeUniqueComments(
            mode == "latest" ? current.Latest : current.Hot,
            page.Items);
        _commentSnapshot = mode == "latest"
            ? new VideoCommentSnapshot
            {
                TotalCount = Math.Max(current.TotalCount, page.TotalCount),
                Hot = current.Hot,
                Latest = merged,
                HotError = current.HotError,
                LatestError = string.Empty,
                HotPaging = current.HotPaging,
                LatestPaging = page.Paging
            }
            : new VideoCommentSnapshot
            {
                TotalCount = Math.Max(current.TotalCount, page.TotalCount),
                Hot = merged,
                Latest = current.Latest,
                HotError = string.Empty,
                LatestError = current.LatestError,
                HotPaging = page.Paging,
                LatestPaging = current.LatestPaging
            };
    }

    private static IReadOnlyList<VideoComment> MergeUniqueComments(
        IReadOnlyList<VideoComment> current,
        IReadOnlyList<VideoComment> incoming)
    {
        var merged = new List<VideoComment>(current.Count + incoming.Count);
        merged.AddRange(current);
        var knownIds = current
            .Select(item => item.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in incoming)
        {
            if (!string.IsNullOrWhiteSpace(item.Id) && !knownIds.Add(item.Id))
            {
                continue;
            }

            merged.Add(item);
        }

        return merged;
    }

    private async Task SendCommentPageToPlayerAsync(string mode, VideoCommentPage page)
    {
        var modeJson = JsonSerializer.Serialize(mode);
        var pageJson = JsonSerializer.Serialize(page);
        await ExecutePlayerScriptAsync(
            $"window.biliLocalPlayer?.appendComments({modeJson}, {pageJson}); 'ok'");
    }

    private async Task ApplyVolumeAsync(double volume)
    {
        var volumeJson = Math.Clamp(volume, 0d, 1d).ToString("0.###", CultureInfo.InvariantCulture);
        await ExecutePlayerScriptAsync(
            $"window.biliLocalPlayer?.setVolume({volumeJson}); 'ok'");
    }

    private async Task<double> GetPlaybackPositionAsync()
    {
        var state = await GetPlaybackStateAsync();
        return state.Position;
    }

    private async Task<PlaybackState> GetPlaybackStateAsync()
    {
        try
        {
            var raw = await ExecutePlayerScriptAsync(
                "JSON.stringify(window.biliLocalPlayer?.state() ?? { time: 0, duration: 0, ended: false })");
            var json = DecodeScriptString(raw);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var position = root.TryGetProperty("time", out var timeElement) &&
                           timeElement.TryGetDouble(out var time)
                ? Math.Max(0d, time)
                : 0d;
            var duration = root.TryGetProperty("duration", out var durationElement) &&
                           durationElement.TryGetDouble(out var parsedDuration)
                ? Math.Max(0d, parsedDuration)
                : 0d;
            var ended = root.TryGetProperty("ended", out var endedElement) &&
                        endedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                        endedElement.GetBoolean();

            return new PlaybackState(position, duration, ended);
        }
        catch
        {
        }

        return new PlaybackState(0d, 0d, false);
    }

    private async Task<string> ExecutePlayerScriptAsync(string script)
    {
        try
        {
#if WINDOWS
            if (_nativePlayerWebView?.CoreWebView2 is not null)
            {
                return await _nativePlayerWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
#endif
            return await PlayerWebView.EvaluateJavaScriptAsync(script) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DecodeScriptString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? raw;
        }
        catch
        {
            return raw;
        }
    }

    private async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync();

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }

    private async Task CloseAsync()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        ShareOverlay.IsVisible = false;
        QualityMenu.IsVisible = false;

        var playbackState = await GetPlaybackStateAsync();
        PlaybackProgressStore.SavePosition(
            _video.Bvid,
            playbackState.Position,
            playbackState.Duration,
            playbackState.Ended);

        _pageCancellation.Cancel();

        try
        {
            await ExecutePlayerScriptAsync("window.biliLocalPlayer?.dispose(); 'ok'");
        }
        catch
        {
        }

        PlayerWebView.Source = "about:blank";
        _playbackManifest = null;

        if (_resolverWebView is not null)
        {
            await DestroyResolverWebViewAsync(_resolverWebView);
        }

        await Navigation.PopModalAsync();
    }

    private const string LocalPlayerHtml = """
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <style>
            :root{color-scheme:dark;--pink:#fb7299;--text:#f4f5f7;--muted:#a3a8b2;--panel:rgba(13,15,20,.92);--comment-width:clamp(318px,36%,507px)}
            *{box-sizing:border-box}
            html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#08090c;color:var(--text);font-family:Segoe UI,Arial,sans-serif;user-select:none}
            #wrap{position:relative;width:100%;height:100%;display:flex;align-items:center;justify-content:center;background:#08090c}
            video{width:100%;height:100%;object-fit:contain;background:#08090c}
            #surface{position:absolute;inset:0;cursor:pointer}
            #shade{position:absolute;left:0;right:0;bottom:0;height:112px;background:linear-gradient(transparent,rgba(0,0,0,.78));pointer-events:none}
            #controls{position:absolute;left:14px;right:14px;bottom:10px;height:48px;display:flex;align-items:center;gap:10px;padding:0 10px;border:1px solid rgba(255,255,255,.08);border-radius:13px;background:rgba(15,17,22,.78);backdrop-filter:blur(12px);opacity:0;transform:translateY(8px);pointer-events:none;transition:opacity .18s,transform .18s}
            #wrap.controls-hover #controls{opacity:1;transform:translateY(0);pointer-events:auto}
            button{appearance:none;border:0;background:transparent;color:var(--text);font:inherit;cursor:pointer}
            .icon{width:32px;height:32px;border-radius:9px;display:grid;place-items:center;font-size:16px}
            .icon:hover{background:rgba(255,255,255,.09)}
            #time{min-width:82px;font-size:11px;color:#d4d7dc;font-variant-numeric:tabular-nums}
            #seek{flex:1;min-width:80px}
            input[type=range]{appearance:none;height:4px;border-radius:999px;background:rgba(255,255,255,.22);outline:none;cursor:pointer}
            input[type=range]::-webkit-slider-thumb{appearance:none;width:12px;height:12px;border-radius:50%;background:var(--pink);box-shadow:0 0 0 3px rgba(251,114,153,.16)}
            #volume{width:72px}
            #centerPlay{position:absolute;left:50%;top:50%;transform:translate(-50%,-50%);width:58px;height:58px;border-radius:50%;background:rgba(19,21,27,.86);border:1px solid rgba(255,255,255,.13);font-size:24px;display:grid;place-items:center;padding-left:3px;backdrop-filter:blur(10px);transition:opacity .15s,transform .15s}
            #centerPlay:hover{transform:translate(-50%,-50%) scale(1.05);background:rgba(28,31,39,.92)}
            #wrap.playing #centerPlay{opacity:0;pointer-events:none}
            #spinner{position:absolute;left:50%;top:50%;width:42px;height:42px;margin:-21px;border:3px solid rgba(255,255,255,.15);border-top-color:var(--pink);border-radius:50%;animation:spin .8s linear infinite;display:none}
            #wrap.buffering #spinner{display:block}
            #wrap.buffering #centerPlay{display:none}
            #hint{position:absolute;left:16px;top:14px;padding:7px 10px;border-radius:9px;background:rgba(0,0,0,.58);font-size:12px;opacity:0;transition:opacity .2s;pointer-events:none}
            #hint.show{opacity:1}
            #audio{position:absolute;width:0;height:0;overflow:hidden;opacity:0;pointer-events:none}
            #danmaku{position:absolute;inset:0;overflow:hidden;pointer-events:none;z-index:1}
            #danmaku.off{display:none}
            #danmaku.paused .dm{animation-play-state:paused}
            .dm{position:absolute;left:0;top:0;white-space:nowrap;font:600 20px/1.25 "Microsoft YaHei","Segoe UI",sans-serif;text-shadow:0 0 2px #000,1px 1px 2px #000,0 0 6px rgba(0,0,0,.55);pointer-events:none;will-change:transform}
            .dm.roll{left:100%;animation-name:dm-roll;animation-timing-function:linear;animation-fill-mode:forwards}
            .dm.hold{left:50%;transform:translateX(-50%);animation:dm-hold 4s linear forwards}
            #dmToggle.on{color:var(--pink)}
            #dmToggle.off{opacity:.42}
            #commentToggle{position:absolute;z-index:12;right:0;top:50%;width:38px;height:68px;padding:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:7px;border:1px solid rgba(255,255,255,.12);border-right:0;border-radius:13px 0 0 13px;background:rgba(15,17,22,.86);color:#d8dbe2;box-shadow:-6px 0 20px rgba(0,0,0,.3);backdrop-filter:blur(12px);opacity:0;pointer-events:none;transform:translate(7px,-50%);transition:opacity .16s,transform .16s,right .22s,background .16s,color .16s}
            #commentToggle::before{content:"";position:absolute;left:0;top:17px;bottom:17px;width:2px;border-radius:0 2px 2px 0;background:var(--pink);opacity:.82}
            #commentToggle:hover{background:rgba(28,31,39,.94);color:#fff}
            .comment-toggle-icon{width:18px;height:18px;fill:none;stroke:currentColor;stroke-width:1.7;stroke-linecap:round;stroke-linejoin:round}
            .comment-toggle-chevron{width:12px;height:12px;fill:none;stroke:var(--pink);stroke-width:2;stroke-linecap:round;stroke-linejoin:round;transition:transform .22s ease}
            #wrap.right-hover #commentToggle,#wrap.comments-open #commentToggle{opacity:1;pointer-events:auto;transform:translate(0,-50%)}
            #wrap.comments-open #commentToggle{right:var(--comment-width)}
            #wrap.comments-open .comment-toggle-chevron{transform:rotate(180deg)}
            #commentPanel{--panel-border:rgba(255,255,255,.10);position:absolute;z-index:11;right:0;top:0;bottom:0;width:var(--comment-width);display:grid;grid-template-rows:auto auto 1fr;background:rgba(15,17,22,.965);border-left:1px solid var(--panel-border);box-shadow:-18px 0 42px rgba(0,0,0,.42);backdrop-filter:blur(18px);transform:translateX(102%);visibility:hidden;transition:transform .22s ease,visibility 0s linear .22s}
            #wrap.comments-open #commentPanel{transform:translateX(0);visibility:visible;transition:transform .22s ease}
            .comment-head{display:grid;grid-template-columns:1fr auto;gap:8px;align-items:center;padding:14px 12px 9px;border-bottom:1px solid var(--panel-border)}
            .comment-title{font-size:15px;font-weight:700;color:#f5f6f8;letter-spacing:.2px}
            #commentCount{margin-left:5px;color:#9298a4;font-size:11px;font-weight:400}
            #commentClose{width:28px;height:28px;border-radius:8px;color:#aeb3bc;font-size:18px;line-height:1}
            #commentClose:hover{background:rgba(255,255,255,.08);color:#fff}
            .comment-tabs{display:flex;gap:4px;padding:7px 10px;border-bottom:1px solid var(--panel-border)}
            .comment-tab{position:relative;padding:6px 11px;border-radius:7px;color:#9ba1ac;font-size:12px}
            .comment-tab:hover{color:#eceef2;background:rgba(255,255,255,.05)}
            .comment-tab.active{color:#fff;background:rgba(251,114,153,.14)}
            .comment-tab.active::after{content:"";position:absolute;left:11px;right:11px;bottom:1px;height:2px;border-radius:2px;background:var(--pink)}
            #commentsList{min-height:0;overflow-y:auto;overscroll-behavior:contain;padding:3px 10px 18px;scrollbar-width:thin;scrollbar-color:#454a54 transparent}
            #commentsList::-webkit-scrollbar{width:6px}
            #commentsList::-webkit-scrollbar-thumb{background:#454a54;border-radius:9px}
            .comment-state{padding:36px 12px;color:#8f95a0;text-align:center;font-size:12px;line-height:1.7}
            .comment-load-more{display:block;width:100%;min-height:36px;margin:8px 0 0;padding:8px;border-radius:8px;color:#9298a4;font-size:11px;text-align:center;background:rgba(255,255,255,.035)}
            .comment-load-more:not(:disabled):hover{color:#fff;background:rgba(255,255,255,.075)}
            .comment-load-more.error{color:#ff9bb8;background:rgba(251,114,153,.08)}
            .comment-load-more:disabled{cursor:default;opacity:.78}
            .comment-item{display:grid;grid-template-columns:30px minmax(0,1fr);gap:8px;padding:11px 1px;border-bottom:1px solid rgba(255,255,255,.075)}
            .comment-avatar{width:30px;height:30px;border-radius:50%;object-fit:cover;background:#292d35;color:#cdd0d6;display:grid;place-items:center;font-size:11px;overflow:hidden}
            .comment-avatar-fallback{border:1px solid rgba(255,255,255,.08)}
            .comment-main{min-width:0}
            .comment-author{display:flex;align-items:center;min-width:0;gap:5px;color:#c9cdd4;font-size:11px;font-weight:600;line-height:18px}
            .comment-author-name{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
            .comment-up{flex:0 0 auto;padding:0 4px;border-radius:3px;background:rgba(251,114,153,.17);color:#ff94b3;font-size:9px;font-weight:700}
            .comment-message{margin-top:3px;color:#e3e5e9;font-size:12px;line-height:1.55;white-space:pre-wrap;overflow-wrap:anywhere;user-select:text}
            .comment-emote{width:22px;height:22px;object-fit:contain;vertical-align:-6px;margin:0 1px}
            .comment-media{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:4px;margin-top:7px}
            .comment-picture{width:100%;aspect-ratio:1;object-fit:cover;border-radius:6px;background:#252932;border:1px solid rgba(255,255,255,.07)}
            .comment-placeholder{display:inline-flex;align-items:center;justify-content:center;min-height:24px;border-radius:5px;color:#9aa0aa;background:#252932;font-size:10px}
            .comment-media .comment-placeholder{aspect-ratio:1}
            .comment-meta{display:flex;align-items:center;gap:10px;margin-top:6px;color:#858b96;font-size:10px;font-variant-numeric:tabular-nums}
            .comment-like{color:#9aa0ab}
            .comment-children{margin-top:7px;padding:6px 7px;border-radius:7px;background:rgba(255,255,255,.045);color:#cdd0d6;font-size:11px;line-height:1.5}
            .comment-child+.comment-child{margin-top:5px;padding-top:5px;border-top:1px solid rgba(255,255,255,.055)}
            .comment-child-name{margin-right:4px;color:#ff9bb8;font-weight:600}
            .comment-child-text{white-space:pre-wrap;overflow-wrap:anywhere;user-select:text}
            @media (max-width:480px){:root{--comment-width:calc(100% - 42px)}}
            @keyframes spin{to{transform:rotate(360deg)}}
            @keyframes dm-roll{from{transform:translateX(0)}to{transform:translateX(calc(-100vw - 100%))}}
            @keyframes dm-hold{0%,80%{opacity:1}100%{opacity:0}}
          </style>
        </head>
        <body>
          <div id="wrap">
            <video id="video" playsinline preload="auto"></video>
            <video id="audio" playsinline preload="auto"></video>
            <div id="danmaku"></div>
            <div id="surface" aria-hidden="true"></div>
            <div id="shade"></div>
            <button id="centerPlay" title="播放">▶</button>
            <div id="spinner"></div>
            <div id="hint"></div>
            <div id="controls">
              <button id="play" class="icon" title="播放/暂停">▶</button>
              <span id="time">0:00 / 0:00</span>
              <input id="seek" type="range" min="0" max="1000" value="0" aria-label="进度" />
              <button id="mute" class="icon" title="静音">🔊</button>
              <input id="volume" type="range" min="0" max="1" step="0.02" value="1" aria-label="音量" />
              <button id="dmToggle" class="icon on" title="弹幕">弹</button>
              <button id="fullscreen" class="icon" title="全屏">⛶</button>
            </div>
            <button id="commentToggle" title="打开评论" aria-label="打开评论" aria-controls="commentPanel" aria-expanded="false">
              <svg class="comment-toggle-icon" viewBox="0 0 24 24" aria-hidden="true">
                <path d="M5.5 5.5h13a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-7l-4.5 3v-3H5.5a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2Z" />
                <path d="M8 11.5h.01M12 11.5h.01M16 11.5h.01" />
              </svg>
              <svg class="comment-toggle-chevron" viewBox="0 0 12 12" aria-hidden="true"><path d="m7.5 2.5-3.5 3.5 3.5 3.5" /></svg>
            </button>
            <aside id="commentPanel" aria-hidden="true">
              <div class="comment-head">
                <div class="comment-title">评论<span id="commentCount">--</span></div>
                <button id="commentClose" title="关闭评论" aria-label="关闭评论">×</button>
              </div>
              <div class="comment-tabs" role="tablist">
                <button id="commentHotTab" class="comment-tab active" role="tab" aria-selected="true">按热度</button>
                <button id="commentLatestTab" class="comment-tab" role="tab" aria-selected="false">按时间</button>
              </div>
              <div id="commentsList"><div class="comment-state">评论读取中…</div></div>
            </aside>
          </div>
          <script>
            (() => {
              const wrap = document.getElementById('wrap');
              const video = document.getElementById('video');
              const audio = document.getElementById('audio');
              const surface = document.getElementById('surface');
              const playButton = document.getElementById('play');
              const centerPlay = document.getElementById('centerPlay');
              const muteButton = document.getElementById('mute');
              const fullscreenButton = document.getElementById('fullscreen');
              const seek = document.getElementById('seek');
              const volume = document.getElementById('volume');
              const time = document.getElementById('time');
              const hint = document.getElementById('hint');
              const dmLayer = document.getElementById('danmaku');
              const dmToggle = document.getElementById('dmToggle');
              const commentToggle = document.getElementById('commentToggle');
              const commentPanel = document.getElementById('commentPanel');
              const commentClose = document.getElementById('commentClose');
              const commentCount = document.getElementById('commentCount');
              const commentHotTab = document.getElementById('commentHotTab');
              const commentLatestTab = document.getElementById('commentLatestTab');
              const commentsList = document.getElementById('commentsList');

              let hasAudioTrack = false;
              let audioCandidates = [];
              let audioIndex = 0;
              let ignoreMediaError = false;
              let disposed = false;
              let userMuted = false;
              let userVolume = 1;
              let seeking = false;
              let syncTimer = 0;
              let hideTimer = 0;
              let dmItems = [];
              let dmNext = 0;
              let dmEnabled = true;
              let dmRaf = 0;
              let commentsOpen = false;
              let commentMode = 'hot';
              let commentSnapshot = { total: 0, hot: [], latest: [], hotError: '', latestError: '' };
              const commentLoadState = {
                hot: { loading: false, error: '' },
                latest: { loading: false, error: '' }
              };
              const dmRollLanes = [];
              const dmHoldLanes = [];

              const postError = text => {
                try { chrome.webview.postMessage('LOCAL_PLAYER_ERROR:' + text); } catch (_) {}
              };

              const showHint = text => {
                hint.textContent = text;
                hint.classList.add('show');
                setTimeout(() => hint.classList.remove('show'), 1200);
              };

              const formatTime = seconds => {
                if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
                const total = Math.floor(seconds);
                const h = Math.floor(total / 3600);
                const m = Math.floor((total % 3600) / 60);
                const s = total % 60;
                return h > 0
                  ? `${h}:${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}`
                  : `${m}:${String(s).padStart(2,'0')}`;
              };

              const formatCount = value => {
                const count = Math.max(0, Number(value) || 0);
                if (count >= 100000000) return `${(count / 100000000).toFixed(count >= 1000000000 ? 0 : 1).replace(/\.0$/, '')}亿`;
                if (count >= 10000) return `${(count / 10000).toFixed(count >= 100000 ? 0 : 1).replace(/\.0$/, '')}万`;
                return String(Math.floor(count));
              };

              const formatCommentTime = value => {
                const date = new Date((Number(value) || 0) * 1000);
                if (!Number.isFinite(date.getTime()) || date.getTime() <= 0) return '';
                const now = new Date();
                const two = number => String(number).padStart(2, '0');
                if (date.getFullYear() === now.getFullYear() &&
                    date.getMonth() === now.getMonth() && date.getDate() === now.getDate()) {
                  return `今天 ${two(date.getHours())}:${two(date.getMinutes())}`;
                }
                if (date.getFullYear() === now.getFullYear()) {
                  return `${date.getMonth() + 1}-${two(date.getDate())} ${two(date.getHours())}:${two(date.getMinutes())}`;
                }
                return `${date.getFullYear()}-${two(date.getMonth() + 1)}-${two(date.getDate())}`;
              };

              const makeTextPlaceholder = (text, className) => {
                const placeholder = document.createElement('span');
                placeholder.className = className || 'comment-placeholder';
                placeholder.textContent = text;
                return placeholder;
              };

              const makeAvatarFallback = name => {
                const fallback = document.createElement('span');
                fallback.className = 'comment-avatar comment-avatar-fallback';
                fallback.textContent = String(name || '哔').trim().slice(0, 1) || '哔';
                return fallback;
              };

              const appendRichText = (target, message, emotes) => {
                const text = String(message || '');
                const map = emotes && typeof emotes === 'object' ? emotes : {};
                const keys = Object.keys(map).filter(Boolean).sort((a, b) => b.length - a.length);
                if (keys.length === 0) {
                  target.appendChild(document.createTextNode(text));
                  return;
                }

                let cursor = 0;
                while (cursor < text.length) {
                  let hit = '';
                  let hitAt = -1;
                  for (const key of keys) {
                    const index = text.indexOf(key, cursor);
                    if (index >= 0 && (hitAt < 0 || index < hitAt || (index === hitAt && key.length > hit.length))) {
                      hit = key;
                      hitAt = index;
                    }
                  }
                  if (hitAt < 0) {
                    target.appendChild(document.createTextNode(text.slice(cursor)));
                    break;
                  }
                  if (hitAt > cursor) target.appendChild(document.createTextNode(text.slice(cursor, hitAt)));
                  const image = document.createElement('img');
                  image.className = 'comment-emote';
                  image.src = String(map[hit] || '');
                  image.alt = hit;
                  image.title = hit;
                  image.addEventListener('error', () => image.replaceWith(document.createTextNode('[表情]')), { once: true });
                  target.appendChild(image);
                  cursor = hitAt + hit.length;
                }
              };

              const makeCommentItem = item => {
                const row = document.createElement('article');
                row.className = 'comment-item';

                const avatarUrl = String(item?.a || '');
                if (avatarUrl) {
                  const avatar = document.createElement('img');
                  avatar.className = 'comment-avatar';
                  avatar.src = avatarUrl;
                  avatar.alt = '';
                  avatar.addEventListener('error', () => avatar.replaceWith(makeAvatarFallback(item?.n)), { once: true });
                  row.appendChild(avatar);
                } else {
                  row.appendChild(makeAvatarFallback(item?.n));
                }

                const main = document.createElement('div');
                main.className = 'comment-main';
                const author = document.createElement('div');
                author.className = 'comment-author';
                const authorName = document.createElement('span');
                authorName.className = 'comment-author-name';
                authorName.textContent = String(item?.n || '哔哩哔哩用户');
                author.appendChild(authorName);
                if (item?.u) {
                  const up = document.createElement('span');
                  up.className = 'comment-up';
                  up.textContent = 'UP';
                  author.appendChild(up);
                }
                main.appendChild(author);

                const message = document.createElement('div');
                message.className = 'comment-message';
                appendRichText(message, item?.m, item?.e);
                main.appendChild(message);

                const pictures = Array.isArray(item?.p) ? item.p.filter(Boolean).slice(0, 9) : [];
                if (pictures.length > 0) {
                  const media = document.createElement('div');
                  media.className = 'comment-media';
                  for (const url of pictures) {
                    const picture = document.createElement('img');
                    picture.className = 'comment-picture';
                    picture.src = String(url);
                    picture.alt = '评论图片';
                    picture.loading = 'lazy';
                    picture.addEventListener('error', () => picture.replaceWith(makeTextPlaceholder('[图片]')), { once: true });
                    media.appendChild(picture);
                  }
                  main.appendChild(media);
                }

                const meta = document.createElement('div');
                meta.className = 'comment-meta';
                const date = document.createElement('span');
                date.textContent = formatCommentTime(item?.t);
                meta.appendChild(date);
                if ((Number(item?.l) || 0) > 0) {
                  const like = document.createElement('span');
                  like.className = 'comment-like';
                  like.textContent = `赞 ${formatCount(item.l)}`;
                  meta.appendChild(like);
                }
                if ((Number(item?.r) || 0) > 0) {
                  const replies = document.createElement('span');
                  replies.textContent = `${formatCount(item.r)} 条回复`;
                  meta.appendChild(replies);
                }
                main.appendChild(meta);

                const children = Array.isArray(item?.c) ? item.c.slice(0, 3) : [];
                if (children.length > 0) {
                  const childBox = document.createElement('div');
                  childBox.className = 'comment-children';
                  for (const child of children) {
                    const childRow = document.createElement('div');
                    childRow.className = 'comment-child';
                    const childName = document.createElement('span');
                    childName.className = 'comment-child-name';
                    childName.textContent = `${String(child?.n || '用户')}：`;
                    const childText = document.createElement('span');
                    childText.className = 'comment-child-text';
                    appendRichText(childText, child?.m, child?.e);
                    childRow.append(childName, childText);
                    childBox.appendChild(childRow);
                  }
                  main.appendChild(childBox);
                }

                row.appendChild(main);
                return row;
              };

              const getCommentPaging = mode => mode === 'latest'
                ? commentSnapshot?.latestPaging
                : commentSnapshot?.hotPaging;

              const updateCommentCount = () => {
                const visibleTotal = Math.max(
                  Number(commentSnapshot?.total) || 0,
                  Array.isArray(commentSnapshot?.hot) ? commentSnapshot.hot.length : 0,
                  Array.isArray(commentSnapshot?.latest) ? commentSnapshot.latest.length : 0);
                commentCount.textContent = formatCount(visibleTotal);
              };

              const renderCommentFooter = () => {
                const items = Array.isArray(commentSnapshot?.[commentMode])
                  ? commentSnapshot[commentMode]
                  : [];
                const paging = getCommentPaging(commentMode);
                const state = commentLoadState[commentMode];
                let footer = commentsList.querySelector('.comment-load-more');
                if (!footer) {
                  footer = document.createElement('button');
                  footer.type = 'button';
                  footer.className = 'comment-load-more';
                  footer.addEventListener('click', event => {
                    event.stopPropagation();
                    requestMoreComments(true);
                  });
                  commentsList.appendChild(footer);
                }

                footer.classList.toggle('error', !!state.error);
                footer.disabled = state.loading || (!state.error && paging?.end !== false);
                if (state.loading) {
                  footer.textContent = '正在加载更多评论…';
                } else if (state.error) {
                  footer.textContent = `加载失败，点击重试 · ${state.error}`;
                } else if (paging?.end !== false) {
                  footer.textContent = items.length > 0 ? `已加载 ${items.length} 条 · 没有更多了` : '暂无评论';
                } else {
                  footer.textContent = `已加载 ${items.length} 条 · 继续下滑加载更多`;
                }
              };

              const requestMoreComments = force => {
                if (!commentsOpen || disposed) return;
                const paging = getCommentPaging(commentMode);
                const state = commentLoadState[commentMode];
                if (state.loading || (!state.error && paging?.end !== false)) return;
                if (!force) {
                  const remaining = commentsList.scrollHeight - commentsList.scrollTop - commentsList.clientHeight;
                  if (remaining > 220) return;
                }

                state.loading = true;
                state.error = '';
                renderCommentFooter();
                try {
                  chrome.webview.postMessage('LOCAL_PLAYER_COMMENTS_MORE:' + commentMode);
                } catch (_) {
                  state.loading = false;
                  state.error = '播放器通信失败';
                  renderCommentFooter();
                }
              };

              const appendCommentPage = (mode, page) => {
                const targetMode = mode === 'latest' ? 'latest' : 'hot';
                const state = commentLoadState[targetMode];
                state.loading = false;
                state.error = String(page?.error || '');

                const key = targetMode;
                const pagingKey = targetMode === 'latest' ? 'latestPaging' : 'hotPaging';
                const current = Array.isArray(commentSnapshot?.[key]) ? commentSnapshot[key] : [];
                const knownIds = new Set(current.map(item => String(item?.id || '')).filter(Boolean));
                const appended = [];
                if (!state.error) {
                  for (const item of Array.isArray(page?.items) ? page.items : []) {
                    const id = String(item?.id || '');
                    if (id && knownIds.has(id)) continue;
                    if (id) knownIds.add(id);
                    current.push(item);
                    appended.push(item);
                  }
                  commentSnapshot[key] = current;
                  if (page?.paging && typeof page.paging === 'object') {
                    commentSnapshot[pagingKey] = page.paging;
                  }
                  commentSnapshot.total = Math.max(
                    Number(commentSnapshot.total) || 0,
                    Number(page?.total) || 0,
                    current.length);
                }

                updateCommentCount();
                if (targetMode !== commentMode) return;
                const footer = commentsList.querySelector('.comment-load-more');
                for (const item of appended) {
                  commentsList.insertBefore(makeCommentItem(item), footer);
                }
                renderCommentFooter();
              };

              const renderComments = () => {
                const items = Array.isArray(commentSnapshot?.[commentMode])
                  ? commentSnapshot[commentMode]
                  : [];
                const error = String(commentMode === 'hot'
                  ? commentSnapshot?.hotError || ''
                  : commentSnapshot?.latestError || '');
                commentsList.replaceChildren();
                commentsList.scrollTop = 0;
                if (items.length === 0) {
                  const state = document.createElement('div');
                  state.className = 'comment-state';
                  state.textContent = error ? `评论暂时无法读取\n${error}` : '暂无评论';
                  commentsList.appendChild(state);
                  renderCommentFooter();
                  return;
                }
                const fragment = document.createDocumentFragment();
                for (const item of items) fragment.appendChild(makeCommentItem(item));
                commentsList.appendChild(fragment);
                renderCommentFooter();
              };

              const setCommentMode = mode => {
                commentMode = mode === 'latest' ? 'latest' : 'hot';
                const hot = commentMode === 'hot';
                commentHotTab.classList.toggle('active', hot);
                commentLatestTab.classList.toggle('active', !hot);
                commentHotTab.setAttribute('aria-selected', hot ? 'true' : 'false');
                commentLatestTab.setAttribute('aria-selected', hot ? 'false' : 'true');
                renderComments();
              };

              const setCommentsOpen = open => {
                commentsOpen = !!open;
                wrap.classList.toggle('comments-open', commentsOpen);
                wrap.classList.toggle('right-hover', commentsOpen);
                commentPanel.setAttribute('aria-hidden', commentsOpen ? 'false' : 'true');
                commentToggle.title = commentsOpen ? '关闭评论' : '打开评论';
                commentToggle.setAttribute('aria-label', commentToggle.title);
                commentToggle.setAttribute('aria-expanded', commentsOpen ? 'true' : 'false');
                clearTimeout(hideTimer);
                if (!commentsOpen) wakeUi();
              };

              const applyCommentSnapshot = snapshot => {
                commentSnapshot = snapshot && typeof snapshot === 'object'
                  ? snapshot
                  : { total: 0, hot: [], latest: [], hotError: '', latestError: '' };
                commentLoadState.hot.loading = false;
                commentLoadState.latest.loading = false;
                commentLoadState.hot.error = String(commentSnapshot.hotError || '');
                commentLoadState.latest.error = String(commentSnapshot.latestError || '');
                updateCommentCount();
                renderComments();
              };

              const updateTimeline = () => {
                const duration = Number.isFinite(video.duration) ? video.duration : 0;
                if (!seeking && duration > 0) {
                  seek.value = String(Math.round((video.currentTime / duration) * 1000));
                }
                const displayedTime = seeking && duration > 0
                  ? (Number(seek.value) / 1000) * duration
                  : video.currentTime;
                time.textContent = `${formatTime(displayedTime)} / ${formatTime(duration)}`;
              };

              const updateButtons = () => {
                const playing = !video.paused && !video.ended;
                wrap.classList.toggle('playing', playing);
                playButton.textContent = playing ? '❚❚' : '▶';
                centerPlay.textContent = '▶';
                muteButton.textContent = userMuted || userVolume <= 0.001 ? '🔇' : '🔊';
                dmLayer.classList.toggle('paused', !playing);
                dmToggle.classList.toggle('on', dmEnabled);
                dmToggle.classList.toggle('off', !dmEnabled);
              };

              const applyVolume = () => {
                // DASH normally gives us a video-only URL plus a separate audio URL. Force-mute
                // the video element whenever a separate audio track exists, so even an unusual
                // muxed video URL cannot create doubled/echoed sound.
                if (hasAudioTrack) {
                  video.muted = true;
                  audio.muted = userMuted;
                  audio.volume = userVolume;
                } else {
                  video.muted = userMuted;
                  video.volume = userVolume;
                }
                volume.value = String(userVolume);
                updateButtons();
              };

              const syncAudio = force => {
                if (!hasAudioTrack || !Number.isFinite(video.currentTime)) return;
                const drift = (audio.currentTime || 0) - video.currentTime;
                if (force || Math.abs(drift) > 0.09) {
                  try { audio.currentTime = video.currentTime; } catch (_) {}
                }
                audio.playbackRate = video.playbackRate;
              };

              const startAudio = async () => {
                if (!hasAudioTrack || video.paused) return;
                syncAudio(true);
                applyVolume();
                try { await audio.play(); } catch (_) {}
              };

              const dmColor = value => {
                const n = Number(value) || 16777215;
                return `rgb(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255})`;
              };

              const dmLaneCount = () => {
                const height = dmLayer.clientHeight || 360;
                return Math.max(4, Math.min(14, Math.floor((height * 0.72) / 28)));
              };

              const dmPickLane = (lanes, count) => {
                const now = performance.now();
                for (let i = 0; i < count; i++) {
                  if (!lanes[i] || lanes[i] < now) return i;
                }
                return Math.floor(Math.random() * count);
              };

              const spawnDanmaku = item => {
                if (!dmEnabled || !item || !item.x || dmLayer.childElementCount > 80) return;
                const el = document.createElement('div');
                el.className = 'dm';
                el.textContent = String(item.x);
                el.style.color = dmColor(item.c);
                const mode = Number(item.m) || 1;
                const lanes = dmLaneCount();
                const laneHeight = 28;
                if (mode === 4 || mode === 5) {
                  el.classList.add('hold');
                  const lane = dmPickLane(dmHoldLanes, lanes);
                  if (mode === 5) {
                    el.style.top = `${lane * laneHeight + 8}px`;
                  } else {
                    el.style.top = 'auto';
                    el.style.bottom = `${lane * laneHeight + 58}px`;
                  }
                  dmHoldLanes[lane] = performance.now() + 3800;
                } else {
                  el.classList.add('roll');
                  const lane = dmPickLane(dmRollLanes, lanes);
                  el.style.top = `${lane * laneHeight + 8}px`;
                  el.style.animationDuration = '8s';
                  dmRollLanes[lane] = performance.now() + 1800;
                }
                dmLayer.appendChild(el);
                el.addEventListener('animationend', () => el.remove());
              };

              const resetDanmakuCursor = () => {
                dmLayer.querySelectorAll('.dm').forEach(node => node.remove());
                const t = Number(video.currentTime) || 0;
                dmNext = 0;
                while (dmNext < dmItems.length && dmItems[dmNext].t < t) dmNext += 1;
              };

              const setDanmakuVisible = (on, notify) => {
                dmEnabled = !!on;
                dmLayer.classList.toggle('off', !dmEnabled);
                if (!dmEnabled) dmLayer.querySelectorAll('.dm').forEach(node => node.remove());
                else resetDanmakuCursor();
                updateButtons();
                if (notify) {
                  try { chrome.webview.postMessage('LOCAL_PLAYER_DANMAKU:' + (dmEnabled ? '1' : '0')); } catch (_) {}
                }
              };

              const dmTick = () => {
                if (disposed) return;
                dmRaf = requestAnimationFrame(dmTick);
                if (!dmEnabled || video.paused || video.ended) return;
                const t = video.currentTime;
                let spawned = 0;
                while (dmNext < dmItems.length && dmItems[dmNext].t <= t + 0.05) {
                  spawnDanmaku(dmItems[dmNext]);
                  dmNext += 1;
                  spawned += 1;
                  if (spawned >= 10) break;
                }
              };

              const togglePlay = async () => {
                try {
                  if (video.paused) {
                    await video.play();
                    await startAudio();
                  } else {
                    video.pause();
                  }
                } catch (_) {
                  showHint('点击播放');
                }
              };

              const wakeUi = () => {
                wrap.classList.remove('hide-ui');
                clearTimeout(hideTimer);
                if (!video.paused && !commentsOpen) {
                  hideTimer = setTimeout(() => wrap.classList.add('hide-ui'), 2200);
                }
              };

              playButton.addEventListener('click', togglePlay);
              centerPlay.addEventListener('click', togglePlay);
              surface.addEventListener('click', togglePlay);
              wrap.addEventListener('mousemove', event => {
                const bounds = wrap.getBoundingClientRect();
                const inRightZone = event.clientX >= bounds.left + bounds.width * 0.70;
                const inControlsZone = event.clientY >= bounds.bottom - 82;
                wrap.classList.toggle('right-hover', commentsOpen || inRightZone);
                wrap.classList.toggle('controls-hover', inControlsZone);
                wakeUi();
              });
              wrap.addEventListener('mouseleave', () => {
                wrap.classList.toggle('right-hover', commentsOpen);
                wrap.classList.remove('controls-hover');
                if (!video.paused && !commentsOpen) wrap.classList.add('hide-ui');
              });

              commentToggle.addEventListener('click', event => {
                event.stopPropagation();
                setCommentsOpen(!commentsOpen);
              });
              commentClose.addEventListener('click', event => {
                event.stopPropagation();
                setCommentsOpen(false);
              });
              commentHotTab.addEventListener('click', () => setCommentMode('hot'));
              commentLatestTab.addEventListener('click', () => setCommentMode('latest'));
              commentsList.addEventListener('scroll', () => requestMoreComments(false), { passive: true });

              muteButton.addEventListener('click', () => {
                userMuted = !userMuted;
                applyVolume();
              });

              dmToggle.addEventListener('click', event => {
                event.stopPropagation();
                setDanmakuVisible(!dmEnabled, true);
              });

              volume.addEventListener('input', () => {
                userVolume = Math.max(0, Math.min(1, Number(volume.value) || 0));
                userMuted = userVolume <= 0.001;
                applyVolume();
                try { chrome.webview.postMessage('LOCAL_PLAYER_VOLUME:' + String(userVolume)); } catch (_) {}
              });

              seek.addEventListener('input', () => {
                seeking = true;
                updateTimeline();
              });

              seek.addEventListener('change', () => {
                const duration = Number.isFinite(video.duration) ? video.duration : 0;
                if (duration > 0) {
                  video.currentTime = (Number(seek.value) / 1000) * duration;
                  syncAudio(true);
                }
                seeking = false;
                updateTimeline();
              });

              fullscreenButton.addEventListener('click', async () => {
                try {
                  if (!document.fullscreenElement) await document.documentElement.requestFullscreen();
                  else await document.exitFullscreen();
                } catch (_) {}
              });

              video.addEventListener('play', async () => {
                updateButtons();
                wakeUi();
                await startAudio();
              });
              video.addEventListener('pause', () => {
                if (hasAudioTrack) audio.pause();
                updateButtons();
                wakeUi();
              });
              video.addEventListener('seeking', () => {
                syncAudio(true);
                resetDanmakuCursor();
              });
              video.addEventListener('seeked', () => {
                resetDanmakuCursor();
                startAudio();
              });
              video.addEventListener('ratechange', () => syncAudio(true));
              video.addEventListener('waiting', () => {
                wrap.classList.add('buffering');
                if (hasAudioTrack) audio.pause();
              });
              video.addEventListener('playing', async () => {
                wrap.classList.remove('buffering');
                await startAudio();
              });
              video.addEventListener('canplay', () => wrap.classList.remove('buffering'));
              video.addEventListener('timeupdate', updateTimeline);
              video.addEventListener('durationchange', updateTimeline);
              video.addEventListener('ended', () => {
                if (hasAudioTrack) audio.pause();
                updateButtons();
              });
              video.addEventListener('error', () => {
                if (ignoreMediaError || disposed) return;
                wrap.classList.remove('buffering');
                const err = video.error;
                postError('视频轨加载失败' + (err ? `（MediaError ${err.code}）` : ''));
              });
              audio.addEventListener('error', () => {
                if (ignoreMediaError || disposed || !hasAudioTrack) return;
                const err = audio.error;
                if (err && err.code === 1) return;
                if (audioIndex + 1 < audioCandidates.length) {
                  audioIndex += 1;
                  audio.src = audioCandidates[audioIndex];
                  audio.load();
                  startAudio();
                  return;
                }
                // Video-only DASH can keep playing. Do not cover the picture with a fatal overlay.
                hasAudioTrack = false;
                applyVolume();
                showHint('音频无法播放，已静音继续');
              });

              document.addEventListener('keydown', event => {
                if (event.key === 'Escape' && commentsOpen) {
                  event.preventDefault();
                  setCommentsOpen(false);
                  return;
                }
                if (event.target instanceof HTMLElement &&
                    (event.target.closest('#commentPanel') || event.target === commentToggle)) {
                  return;
                }
                if (event.code === 'Space') { event.preventDefault(); togglePlay(); }
                else if (event.code === 'ArrowLeft') { video.currentTime = Math.max(0, video.currentTime - 5); syncAudio(true); showHint('-5 秒'); }
                else if (event.code === 'ArrowRight') { video.currentTime = Math.min(video.duration || Infinity, video.currentTime + 5); syncAudio(true); showHint('+5 秒'); }
                else if (event.key.toLowerCase() === 'm') { userMuted = !userMuted; applyVolume(); }
                else if (event.key.toLowerCase() === 'f') { fullscreenButton.click(); }
              });

              syncTimer = setInterval(() => {
                if (!disposed && hasAudioTrack && !video.paused) syncAudio(false);
              }, 200);

              window.biliLocalPlayer = {
                load(videoUrl, audioUrl, startTime) {
                  video.pause();
                  audio.pause();
                  wrap.classList.add('buffering');
                  wrap.classList.remove('hide-ui');

                  const audioList = (Array.isArray(audioUrl) ? audioUrl : [audioUrl])
                    .map(item => String(item || ''))
                    .filter(item => item.length > 0);

                  ignoreMediaError = true;
                  video.removeAttribute('src');
                  audio.removeAttribute('src');

                  audioCandidates = audioList;
                  audioIndex = 0;
                  hasAudioTrack = audioCandidates.length > 0;
                  video.src = videoUrl;
                  if (hasAudioTrack) audio.src = audioCandidates[0];
                  applyVolume();
                  video.load();
                  if (hasAudioTrack) audio.load();
                  setTimeout(() => { ignoreMediaError = false; }, 0);

                  const requestedStart = Math.max(0, Number(startTime) || 0);
                  const applyStart = () => {
                    if (requestedStart > 0 && Number.isFinite(video.duration)) {
                      video.currentTime = Math.min(requestedStart, Math.max(0, video.duration - 0.5));
                    }
                    syncAudio(true);
                    updateTimeline();
                  };

                  video.addEventListener('loadedmetadata', () => {
                    applyStart();
                    resetDanmakuCursor();
                  }, { once: true });
                  video.addEventListener('canplay', async () => {
                    try {
                      await video.play();
                      await startAudio();
                    } catch (_) {
                      wrap.classList.remove('buffering');
                      showHint('点击播放');
                    }
                  }, { once: true });
                },
                setDanmaku(items) {
                  dmItems = Array.isArray(items) ? items.slice() : [];
                  dmItems.sort((a, b) => (a.t || 0) - (b.t || 0));
                  resetDanmakuCursor();
                },
                setDanmakuVisible(on, notify) {
                  setDanmakuVisible(on, notify);
                },
                setComments(snapshot) {
                  applyCommentSnapshot(snapshot);
                },
                appendComments(mode, page) {
                  appendCommentPage(mode, page);
                },
                setVolume(value) {
                  userVolume = Math.max(0, Math.min(1, Number(value)));
                  if (!Number.isFinite(userVolume)) userVolume = 1;
                  userMuted = userVolume <= 0.001;
                  applyVolume();
                },
                state() {
                  return {
                    time: Number(video.currentTime) || 0,
                    duration: Number.isFinite(video.duration) ? video.duration : 0,
                    paused: video.paused,
                    ended: video.ended
                  };
                },
                dispose() {
                  disposed = true;
                  cancelAnimationFrame(dmRaf);
                  clearInterval(syncTimer);
                  clearTimeout(hideTimer);
                  video.pause();
                  audio.pause();
                  video.removeAttribute('src');
                  audio.removeAttribute('src');
                  video.load();
                  audio.load();
                  dmLayer.innerHTML = '';
                  commentsList.replaceChildren();
                }
              };

              applyVolume();
              updateTimeline();
              updateButtons();
              dmRaf = requestAnimationFrame(dmTick);
            })();
          </script>
        </body>
        </html>
        """;

    private sealed record PlaybackState(double Position, double Duration, bool Ended);
}
