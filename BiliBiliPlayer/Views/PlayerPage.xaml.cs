using System.Globalization;
using System.Text.Json;
using BiliBiliPlayer.Models;
using BiliBiliPlayer.Services;

namespace BiliBiliPlayer.Views;

public partial class PlayerPage : ContentPage
{
    private const int MaximumPreferredQuality = 80;
    private const string DanmakuPreferenceKey = "bili.player.danmaku.enabled";
    private const string SubtitlePreferenceKey = "bili.player.subtitle.enabled";
    private const string SubtitleLanguagePreferenceKey = "bili.player.subtitle.language";
    private const string SubtitleFontScalePreferenceKey = "bili.player.subtitle.fontScale";
    private const string SubtitleBottomPreferenceKey = "bili.player.subtitle.bottomPercent";
    private const string VolumePreferenceKey = "bili.player.volume";

    private readonly VideoItem _video;
    private readonly BiliPlaybackResolver _resolver = new();
    private readonly BiliCommentService _commentService = new();
    private readonly BiliWatchHistoryService _watchHistoryService = new();
    private readonly CancellationTokenSource _pageCancellation = new();
    private readonly SemaphoreSlim _hotCommentLoadGate = new(1, 1);
    private readonly SemaphoreSlim _latestCommentLoadGate = new(1, 1);
    private readonly string _cookieHeader;
    private bool _isClosing;
    private bool _playerReady;
    private bool _hasStarted;
    private bool _isResolving;
    private int _watchHistoryReportAttempted;
    private bool _danmakuEnabled;
    private bool _subtitleEnabled;
    private string _subtitleLanguage = string.Empty;
    private double _subtitleFontScale = 1d;
    private double _subtitleBottomPercent = 8d;
    private double _volume = 1d;
    private int _selectedQuality;
    private int _requestedPageNumber = 1;
    private IReadOnlyList<VideoPart> _parts = Array.Empty<VideoPart>();
    private PlaybackManifest? _playbackManifest;
    private VideoCommentSnapshot? _commentSnapshot;
    private WebView? _resolverWebView;
    private IReadOnlyList<WeChatShareContact> _shareContacts = Array.Empty<WeChatShareContact>();
    private WeChatShareContact? _selectedShareContact;
    private bool _isLoadingShareContacts;
    private bool _isSendingShare;
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
        _subtitleEnabled = Preferences.Default.Get(SubtitlePreferenceKey, true);
        _subtitleLanguage = Preferences.Default.Get(SubtitleLanguagePreferenceKey, string.Empty);
        _subtitleFontScale = Math.Clamp(Preferences.Default.Get(SubtitleFontScalePreferenceKey, 1d), 0.75d, 1.5d);
        _subtitleBottomPercent = Math.Clamp(Preferences.Default.Get(SubtitleBottomPreferenceKey, 8d), 4d, 25d);
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
        _requestedPageNumber = PlaybackProgressStore.GetPageNumber(_video.Bvid);
        await ResolveAndPlayAsync(
            MaximumPreferredQuality,
            preservePosition: false,
            autoSelectHighest: true);
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
            if (message.StartsWith("LOCAL_PLAYER_PART:", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(message["LOCAL_PLAYER_PART:".Length..]);
                var root = document.RootElement;
                if (root.TryGetProperty("bvid", out var bvid) && bvid.GetString() == _video.Bvid &&
                    root.TryGetProperty("cid", out var cid) && cid.TryGetInt64(out var selectedCid) &&
                    root.TryGetProperty("page", out var page) && page.TryGetInt32(out var pageNumber) &&
                    _parts.Any(part => part.Cid == selectedCid && part.PageNumber == pageNumber))
                {
                    Dispatcher.Dispatch(() => _ = SwitchPartAsync(pageNumber));
                }
                return;
            }

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

            if (message.StartsWith("LOCAL_PLAYER_SUBTITLE:", StringComparison.Ordinal))
            {
                var payload = message["LOCAL_PLAYER_SUBTITLE:".Length..];
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var enabled = root.TryGetProperty("enabled", out var enabledElement) &&
                              enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                              enabledElement.GetBoolean();
                var language = root.TryGetProperty("language", out var languageElement) &&
                               languageElement.ValueKind == JsonValueKind.String
                    ? languageElement.GetString() ?? string.Empty
                    : string.Empty;
                var fontScale = root.TryGetProperty("fontScale", out var scaleElement) &&
                                scaleElement.TryGetDouble(out var scale) && double.IsFinite(scale)
                    ? Math.Clamp(scale, 0.75d, 1.5d)
                    : 1d;
                var bottomPercent = root.TryGetProperty("bottomPercent", out var bottomElement) &&
                                    bottomElement.TryGetDouble(out var bottom) && double.IsFinite(bottom)
                    ? Math.Clamp(bottom, 4d, 25d)
                    : 8d;

                Dispatcher.Dispatch(() =>
                {
                    _subtitleEnabled = enabled;
                    _subtitleLanguage = language;
                    _subtitleFontScale = fontScale;
                    _subtitleBottomPercent = bottomPercent;
                    Preferences.Default.Set(SubtitlePreferenceKey, enabled);
                    Preferences.Default.Set(SubtitleLanguagePreferenceKey, language);
                    Preferences.Default.Set(SubtitleFontScalePreferenceKey, fontScale);
                    Preferences.Default.Set(SubtitleBottomPreferenceKey, bottomPercent);
                });
                return;
            }

            if (message.StartsWith("LOCAL_PLAYER_COMMENTS_MORE:", StringComparison.Ordinal))
            {
                var mode = message["LOCAL_PLAYER_COMMENTS_MORE:".Length..];
                _ = LoadMoreCommentsAsync(mode);
                return;
            }

            if (message.StartsWith("LOCAL_PLAYER_WATCHED:", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(message["LOCAL_PLAYER_WATCHED:".Length..]);
                var root = document.RootElement;
                var bvid = root.TryGetProperty("bvid", out var bvidElement)
                    ? bvidElement.GetString() ?? string.Empty
                    : string.Empty;
                var aid = root.TryGetProperty("aid", out var aidElement) && aidElement.TryGetInt64(out var parsedAid)
                    ? parsedAid
                    : 0;
                var cid = root.TryGetProperty("cid", out var cidElement) && cidElement.TryGetInt64(out var parsedCid)
                    ? parsedCid
                    : 0;
                var position = root.TryGetProperty("time", out var timeElement) &&
                               timeElement.TryGetDouble(out var parsedTime) && double.IsFinite(parsedTime)
                    ? Math.Max(0d, parsedTime)
                    : 0d;

                var manifest = _playbackManifest;
                if (bvid == _video.Bvid && manifest is not null &&
                    aid == manifest.Aid && cid == manifest.Cid)
                {
                    _ = ReportWatchHistoryOnceAsync(bvid, aid, cid, position);
                }
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

    private async void OnOpenWebClicked(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        QualityMenu.IsVisible = false;
        ShareOverlay.IsVisible = false;

        var videoUrl = new Uri(
            $"https://www.bilibili.com/video/{Uri.EscapeDataString(_video.Bvid)}/");
        try
        {
            if (!await Launcher.Default.OpenAsync(videoUrl))
            {
                await DisplayAlert("无法打开网页", "系统没有找到可用的默认浏览器。", "知道了");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("无法打开网页", ex.Message, "知道了");
        }
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
        _ = LoadShareContactsAsync();
    }

    private void OnShareOverlayTapped(object? sender, EventArgs e)
    {
        ShareOverlay.IsVisible = false;
    }

    private async void OnShareToWeChatClicked(object? sender, EventArgs e)
    {
        var contact = _selectedShareContact;
        if (_isClosing || contact is null)
        {
            if (!_isClosing)
            {
                ShareHintLabel.Text = "请先选择一个联系人或群聊。";
                ShareHintLabel.TextColor = Color.FromArgb("#FF7B86");
            }
            return;
        }

        _isSendingShare = true;
        UpdateShareControls();
        ShareToWeChatButton.Text = "发送中...";
        ShareHintLabel.TextColor = Color.FromArgb("#9AA0AC");
        ShareHintLabel.Text = $"正在生成卡片并发送给 {contact.DisplayName}...";
        try
        {
            var result = await VideoShareService.SendToWeChatAsync(
                _video,
                contact,
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
            _isSendingShare = false;
            ShareToWeChatButton.Text = "发送给所选联系人";
            UpdateShareControls();
        }
    }

    private async void OnRefreshShareContactsClicked(object? sender, EventArgs e)
    {
        await LoadShareContactsAsync(forceRefresh: true);
    }

    private void OnShareContactSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedShareContact = e.CurrentSelection.FirstOrDefault() as WeChatShareContact;
        if (_selectedShareContact is null)
        {
            ShareHintLabel.Text = "请选择接收卡片的联系人或群聊。";
        }
        else
        {
            ShareHintLabel.Text = $"将发送给：{_selectedShareContact.DisplayName}（{_selectedShareContact.KindText}）";
        }
        ShareHintLabel.TextColor = Color.FromArgb("#9AA0AC");
        UpdateShareControls();
    }

    private async Task LoadShareContactsAsync(bool forceRefresh = false)
    {
        if (_isClosing || _isLoadingShareContacts || (!forceRefresh && _shareContacts.Count > 0))
        {
            return;
        }

        _isLoadingShareContacts = true;
        ShareContactsStatusLabel.Text = "读取中...";
        ShareHintLabel.Text = "正在读取微信最近联系人...";
        ShareHintLabel.TextColor = Color.FromArgb("#9AA0AC");
        UpdateShareControls();
        try
        {
            var previousWxid = _selectedShareContact?.Wxid;
            _shareContacts = await VideoShareService.GetRecentContactsAsync(_pageCancellation.Token);
            ShareContactsView.ItemsSource = _shareContacts;
            _selectedShareContact = string.IsNullOrWhiteSpace(previousWxid)
                ? null
                : _shareContacts.FirstOrDefault(contact => contact.Wxid == previousWxid);
            ShareContactsView.SelectedItem = _selectedShareContact;
            ShareContactsStatusLabel.Text = $"{_shareContacts.Count} 个";
            ShareHintLabel.Text = _shareContacts.Count == 0
                ? "没有读到可分享的最近联系人，请在微信中产生一次会话后刷新。"
                : _selectedShareContact is null
                    ? "请选择接收卡片的联系人或群聊。"
                    : $"将发送给：{_selectedShareContact.DisplayName}（{_selectedShareContact.KindText}）";
        }
        catch (OperationCanceledException) when (_pageCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _shareContacts = Array.Empty<WeChatShareContact>();
            _selectedShareContact = null;
            ShareContactsView.ItemsSource = _shareContacts;
            ShareContactsStatusLabel.Text = "读取失败";
            ShareHintLabel.Text = ex.Message;
            ShareHintLabel.TextColor = Color.FromArgb("#FF7B86");
        }
        finally
        {
            _isLoadingShareContacts = false;
            UpdateShareControls();
        }
    }

    private void UpdateShareControls()
    {
        var isIdle = !_isLoadingShareContacts && !_isSendingShare;
        RefreshShareContactsButton.IsEnabled = isIdle;
        ShareContactsView.IsEnabled = isIdle;
        RecommendationEntry.IsEnabled = isIdle;
        ShareToWeChatButton.IsEnabled = isIdle && _selectedShareContact is not null;
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
        ShareHintLabel.Text = _selectedShareContact is null
            ? "请选择接收卡片的联系人或群聊。"
            : $"将发送给：{_selectedShareContact.DisplayName}（{_selectedShareContact.KindText}）";
        UpdateShareControls();
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

    private Task SwitchPartAsync(int pageNumber)
    {
        if (_isResolving || _isClosing || _parts.Count <= 1 ||
            _playbackManifest?.PageNumber == pageNumber ||
            !_parts.Any(part => part.PageNumber == pageNumber))
        {
            return Task.CompletedTask;
        }

        return ResolveAndPlayAsync(
            _selectedQuality > 0 ? _selectedQuality : MaximumPreferredQuality,
            preservePosition: false,
            pageNumber: pageNumber);
    }

    private async Task ResolveAndPlayAsync(
        int quality,
        bool preservePosition,
        bool autoSelectHighest = false,
        int? pageNumber = null)
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

        var switchingPart = pageNumber.HasValue && pageNumber.Value != _playbackManifest?.PageNumber;
        var needsResolution = switchingPart || _playbackManifest is null;
        LoadingLabel.Text = needsResolution
            ? (switchingPart ? $"正在加载 P{pageNumber}…" : autoSelectHighest ? "正在检测最高可用画质…" : $"正在解析 {BiliQualityNames.GetLabel(quality)}…")
            : $"正在切换 {BiliQualityNames.GetLabel(quality)}…";
        try
        {
            var previousState = new PlaybackState(0, 1, 0d, 0d, false);
            // OnAppearing runs before the WebView handler/navigation may exist. Only an
            // already initialized player needs to be suspended or have its position read.
            // Calling MAUI EvaluateJavaScriptAsync here on first open can never complete.
            if (_playerReady)
            {
                await ExecutePlayerScriptAsync("window.biliLocalPlayer?.setPartsBusy(true); 'ok'");
                previousState = await GetPlaybackStateAsync();
                // Cancel pending autoplay callbacks as well as pausing both media elements.
                await ExecutePlayerScriptAsync("window.biliLocalPlayer?.suspend(); 'ok'");
            }
            if (switchingPart)
            {
                SavePlaybackProgress(previousState);
                _requestedPageNumber = pageNumber!.Value;
                _playbackManifest = null;
            }

            _pageCancellation.Token.ThrowIfCancellationRequested();

            if (_playbackManifest is null)
            {
                // Resolve once at our highest UI quality. The returned DASH snapshot contains
                // all lower representations, then the temporary resolver WebView2 is destroyed.
                _playbackManifest = await ResolveWithTemporaryWebViewAsync(
                    MaximumPreferredQuality,
                    _pageCancellation.Token);
                // All P of one BV share comments; retain already loaded comment pages.
                _commentSnapshot ??= _playbackManifest.Comments;
            }

            _pageCancellation.Token.ThrowIfCancellationRequested();
            _parts = _playbackManifest.Parts;
            _requestedPageNumber = _playbackManifest.PageNumber;
            var position = preservePosition && previousState.Duration > 0 && previousState.Cid == _playbackManifest.Cid
                ? previousState.Position
                : PlaybackProgressStore.GetPosition(_video.Bvid, _playbackManifest.Cid);
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
            await ApplyPartsAsync(_playbackManifest);
            await ApplyDanmakuAsync(_playbackManifest.Danmaku, _danmakuEnabled);
            await ApplySubtitlesAsync(
                _playbackManifest.Subtitles,
                _subtitleEnabled,
                _subtitleLanguage,
                _subtitleFontScale,
                _subtitleBottomPercent);
            _commentSnapshot ??= _playbackManifest.Comments;
            await ApplyVideoCommentsAsync(_commentSnapshot);

            _selectedQuality = source.ActualQuality;
            UpdateQualityUi(source.ActualQuality, _playbackManifest.AvailableQualities);
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
            LoadingLabel.Text = ex.Message;
            LoadingIndicator.IsRunning = false;
            RetryButton.IsVisible = true;
            PlayerLoadingOverlay.IsVisible = true;
        }
        finally
        {
            _isResolving = false;
            QualityMenuButton.IsEnabled = !_isClosing;
            if (!_isClosing)
            {
                await ExecutePlayerScriptAsync("window.biliLocalPlayer?.setPartsBusy(false); 'ok'");
            }
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
                cancellationToken,
                pageNumber: _requestedPageNumber);
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
        var identityJson = JsonSerializer.Serialize(new
        {
            bvid = _video.Bvid,
            aid = _playbackManifest?.Aid ?? 0,
            cid = _playbackManifest?.Cid ?? 0,
            page = _playbackManifest?.PageNumber ?? 1
        });
        var script = $$"""
            (() => {
                if (!window.biliLocalPlayer) return 'missing';
                window.biliLocalPlayer.load({{videoJson}}, {{audioJson}}, {{position.ToString(CultureInfo.InvariantCulture)}}, {{identityJson}});
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

    private async Task ApplyPartsAsync(PlaybackManifest manifest)
    {
        var snapshotJson = JsonSerializer.Serialize(new
        {
            bvid = manifest.Bvid,
            aid = manifest.Aid,
            cid = manifest.Cid,
            page = manifest.PageNumber,
            parts = manifest.Parts
        });
        await ExecutePlayerScriptAsync($"window.biliLocalPlayer?.setParts({snapshotJson}); 'ok'");
        var currentPart = manifest.Parts.FirstOrDefault(part => part.Cid == manifest.Cid);
        MetaLabel.Text = $"{_video.OwnerName}  ·  {_video.Bvid}" +
            (manifest.Parts.Count > 1 && currentPart is not null
                ? $"  ·  P{currentPart.PageNumber}/{manifest.Parts.Count} {currentPart.Title}"
                : string.Empty);
    }

    private async Task ApplySubtitlesAsync(
        SubtitleSnapshot subtitles,
        bool enabled,
        string preferredLanguage,
        double fontScale,
        double bottomPercent)
    {
        if (_playbackManifest is null || _playbackManifest.Bvid != _video.Bvid ||
            subtitles.Bvid != _video.Bvid || subtitles.Aid != _playbackManifest.Aid ||
            subtitles.Cid != _playbackManifest.Cid)
        {
            subtitles = new SubtitleSnapshot
            {
                Bvid = _video.Bvid,
                Aid = _playbackManifest?.Aid ?? 0,
                Cid = _playbackManifest?.Cid ?? 0,
                Error = "字幕与当前视频不匹配，已阻止加载"
            };
        }
        var snapshotJson = JsonSerializer.Serialize(subtitles);
        var enabledJson = enabled ? "true" : "false";
        var languageJson = JsonSerializer.Serialize(preferredLanguage ?? string.Empty);
        var fontScaleJson = Math.Clamp(fontScale, 0.75d, 1.5d).ToString(CultureInfo.InvariantCulture);
        var bottomJson = Math.Clamp(bottomPercent, 4d, 25d).ToString(CultureInfo.InvariantCulture);
        var script = $$"""
            (() => {
                if (!window.biliLocalPlayer) return 'missing';
                window.biliLocalPlayer.setSubtitles({{snapshotJson}}, {{enabledJson}}, {{languageJson}}, {{fontScaleJson}}, {{bottomJson}});
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

    private async Task<PlaybackState> GetPlaybackStateAsync()
    {
        try
        {
            var raw = await ExecutePlayerScriptAsync(
                "JSON.stringify(window.biliLocalPlayer?.state() ?? { time: 0, duration: 0, ended: false })");
            var json = DecodeScriptString(raw);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var cid = root.TryGetProperty("cid", out var cidElement) && cidElement.TryGetInt64(out var parsedCid)
                ? parsedCid : 0;
            var pageNumber = root.TryGetProperty("page", out var pageElement) && pageElement.TryGetInt32(out var parsedPage)
                ? parsedPage : 1;
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

            return new PlaybackState(cid, pageNumber, position, duration, ended);
        }
        catch
        {
        }

        return new PlaybackState(0, 1, 0d, 0d, false);
    }

    private void SavePlaybackProgress(PlaybackState state) =>
        PlaybackProgressStore.SavePosition(
            _video.Bvid, state.Cid, state.PageNumber, state.Position, state.Duration, state.Ended);

    private async Task ReportWatchHistoryOnceAsync(string bvid, long aid, long cid, double position)
    {
        if (string.IsNullOrWhiteSpace(_cookieHeader) ||
            Interlocked.Exchange(ref _watchHistoryReportAttempted, 1) != 0)
        {
            return;
        }

        try
        {
            // This request is deliberately independent of page cancellation. Once actual playback
            // has started, closing the modal immediately should not cancel the one history entry.
            await _watchHistoryService.ReportStartedAsync(bvid, aid, cid, position, _cookieHeader);
        }
        catch
        {
            // History is best-effort and must never interrupt playback or show a playback error.
        }
    }

    private Task<string> ExecutePlayerScriptAsync(string script)
    {
        // CloseAsync cancels page work before disposing the media, so cleanup commands
        // use the same bounded wait without inheriting the already-cancelled page token.
        var cancellationToken = _isClosing ? CancellationToken.None : _pageCancellation.Token;
#if WINDOWS
        var native = _nativePlayerWebView ??
            PlayerWebView.Handler?.PlatformView as Microsoft.UI.Xaml.Controls.WebView2;
        var core = native?.CoreWebView2;
        return PlayerScriptExecutor.ExecuteAsync(
            _playerReady && core is not null,
            async () => await core!.ExecuteScriptAsync(script),
            cancellationToken);
#else
        return PlayerScriptExecutor.ExecuteAsync(
            _playerReady && PlayerWebView.Handler is not null,
            () => PlayerWebView.EvaluateJavaScriptAsync(script),
            cancellationToken);
#endif
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
        SavePlaybackProgress(playbackState);

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
            #controls{position:absolute;z-index:5;left:14px;right:14px;bottom:10px;height:48px;display:flex;align-items:center;gap:10px;padding:0 10px;border:1px solid rgba(255,255,255,.08);border-radius:13px;background:rgba(15,17,22,.78);backdrop-filter:blur(12px);opacity:0;transform:translateY(8px);pointer-events:none;transition:opacity .18s,transform .18s}
            #wrap.controls-hover #controls,#wrap.subtitle-menu-open #controls,#wrap.part-menu-open #controls{opacity:1;transform:translateY(0);pointer-events:auto}
            button{appearance:none;border:0;background:transparent;color:var(--text);font:inherit;cursor:pointer}
            .icon{width:32px;height:32px;border-radius:9px;display:grid;place-items:center;font-size:16px}
            .icon:hover{background:rgba(255,255,255,.09)}
            #time{min-width:82px;font-size:11px;color:#d4d7dc;font-variant-numeric:tabular-nums}
            #seek{flex:1;min-width:80px}
            input[type=range]{appearance:none;height:4px;border-radius:999px;background:rgba(255,255,255,.22);outline:none;cursor:pointer}
            input[type=range]::-webkit-slider-thumb{appearance:none;width:12px;height:12px;border-radius:50%;background:var(--pink);box-shadow:0 0 0 3px rgba(251,114,153,.16)}
            #volume{width:72px}
            #centerPlay{position:absolute;left:50%;top:50%;transform:translate(-50%,-50%);width:58px;height:58px;border-radius:50%;background:rgba(19,21,27,.86);border:1px solid rgba(255,255,255,.13);display:grid;place-items:center;padding:0;backdrop-filter:blur(10px);transition:opacity .15s,transform .15s}
            #centerPlay::before{content:"";width:0;height:0;border-top:10px solid transparent;border-bottom:10px solid transparent;border-left:16px solid currentColor;transform:translateX(3px)}
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
            #partToggle{width:44px;flex-shrink:0;font-size:13px;font-weight:600}
            #partToggle.on{color:var(--pink)}
            #partToggle:disabled{color:#a3a8b2;opacity:.38;cursor:default;background:transparent}
            #partMenu{position:absolute;z-index:10;right:98px;bottom:68px;width:min(340px,calc(100% - 28px));max-height:calc(100% - 90px);display:none;flex-direction:column;padding:8px;border:1px solid rgba(255,255,255,.12);border-radius:12px;background:rgba(20,22,28,.97);box-shadow:0 10px 30px rgba(0,0,0,.4);backdrop-filter:blur(18px)}
            #partMenu.open{display:flex}
            .part-menu-head{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:7px 9px 12px;font-size:13px;font-weight:600}
            #partCount{font-size:11px;font-weight:400;color:#898f9a}
            #partList{overflow:auto;min-height:0;scrollbar-width:thin;scrollbar-color:#3f424c transparent}
            .part-option{width:100%;min-height:44px;display:flex;align-items:center;gap:10px;padding:9px;border-radius:8px;text-align:left;font-size:12px}
            .part-option:hover{background:rgba(255,255,255,.07)}
            .part-option.selected{color:var(--pink);background:rgba(251,114,153,.1)}
            .part-number{min-width:23px;font-variant-numeric:tabular-nums;color:#898f9a}
            .part-option.selected .part-number{color:var(--pink)}
            .part-name{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
            .part-duration{font-size:11px;color:#898f9a;font-variant-numeric:tabular-nums}
            .part-check{width:12px}
            @media (max-width:480px){#partMenu{right:14px}}
            #subtitles{position:absolute;z-index:3;pointer-events:none;display:flex;justify-content:center;text-align:center;visibility:hidden;transition:bottom .12s,left .12s,width .12s}
            #subtitles.visible{visibility:visible}
            #subtitleText{max-width:100%;padding:3px 10px 5px;border-radius:4px;background:rgba(0,0,0,.68);color:#fff;font-family:"Microsoft YaHei","Segoe UI",sans-serif;font-weight:500;line-height:1.4;text-shadow:0 1px 2px #000;white-space:pre-line;overflow-wrap:anywhere}
            #subtitleToggle{width:44px;font-size:13px;font-weight:600}
            #subtitleToggle.on{color:var(--pink)}
            #subtitleToggle.off{color:#a3a8b2}
            #subtitleToggle.unavailable{opacity:.42}
            #subtitleMenu{position:absolute;z-index:10;right:58px;bottom:68px;width:220px;max-height:calc(100% - 90px);overflow:auto;padding:8px;border:1px solid rgba(255,255,255,.12);border-radius:12px;background:rgba(20,22,28,.97);box-shadow:0 10px 30px rgba(0,0,0,.4);backdrop-filter:blur(18px);display:none}
            #subtitleMenu.open{display:block}
            .subtitle-menu-title{padding:5px 9px 8px;color:#898f9a;font-size:11px}
            .subtitle-option{width:100%;min-height:34px;display:flex;align-items:center;gap:7px;padding:7px 9px;border-radius:7px;text-align:left;color:#e1e4e9;font-size:12px}
            .subtitle-option:hover{background:rgba(255,255,255,.07)}
            .subtitle-option.selected{color:var(--pink);background:rgba(251,114,153,.08)}
            .subtitle-option-name{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
            .subtitle-ai{border:1px solid currentColor;border-radius:4px;padding:0 3px;font-size:8px;line-height:13px;opacity:.72}
            .subtitle-check{width:12px;font-size:12px}
            #subtitleSettingsButton{margin-top:5px;padding-top:10px;border-top:1px solid rgba(255,255,255,.08);border-radius:0}
            #subtitleSettings{padding:7px 9px 4px;font-size:11px;color:#aeb4bf}
            #subtitleSettings[hidden]{display:none}
            .subtitle-setting{display:grid;grid-template-columns:1fr auto;gap:9px;align-items:center;margin:6px 0 15px}
            .subtitle-setting input{grid-column:1 / -1;width:100%}
            #subtitleReset{width:100%;padding:6px;border-radius:6px;background:rgba(255,255,255,.05);color:#b4bac5;font-size:11px}
            #subtitleReset:hover{color:#fff;background:rgba(255,255,255,.09)}
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
            <div id="subtitles"><div id="subtitleText"></div></div>
            <div id="surface" aria-hidden="true"></div>
            <div id="shade"></div>
            <button id="centerPlay" title="播放" aria-label="播放"></button>
            <div id="spinner"></div>
            <div id="hint"></div>
            <div id="controls">
              <button id="play" class="icon" title="播放/暂停">▶</button>
              <span id="time">0:00 / 0:00</span>
              <input id="seek" type="range" min="0" max="1000" value="0" aria-label="进度" />
              <button id="mute" class="icon" title="静音">🔊</button>
              <input id="volume" type="range" min="0" max="1" step="0.02" value="1" aria-label="音量" />
              <button id="partToggle" class="icon" title="选集读取中" aria-label="选集" aria-haspopup="dialog" aria-controls="partMenu" aria-expanded="false" disabled>选集</button>
              <button id="subtitleToggle" class="icon off unavailable" title="字幕读取中" aria-label="字幕" aria-haspopup="dialog" aria-controls="subtitleMenu" aria-expanded="false">字幕</button>
              <button id="fullscreen" class="icon" title="全屏">⛶</button>
            </div>
            <div id="partMenu" role="dialog" aria-label="选集" aria-hidden="true">
              <div class="part-menu-head"><span>选集</span><span id="partCount"></span></div>
              <div id="partList" role="menu" aria-label="视频分 P"></div>
            </div>
            <div id="subtitleMenu" role="dialog" aria-label="字幕" aria-hidden="true">
              <div class="subtitle-menu-title">字幕</div>
              <div id="subtitleTracks" role="menu" aria-label="字幕语言"></div>
              <button id="subtitleSettingsButton" class="subtitle-option" aria-expanded="false" aria-controls="subtitleSettings"><span class="subtitle-option-name">字幕设置</span><span>⌄</span></button>
              <div id="subtitleSettings" hidden>
                <label class="subtitle-setting">字号<span id="subtitleSizeValue">100%</span><input id="subtitleSize" type="range" min="75" max="150" step="5" value="100" aria-label="字幕字号" /></label>
                <label class="subtitle-setting">距画面底部<span id="subtitleBottomValue">8%</span><input id="subtitleBottom" type="range" min="4" max="25" step="1" value="8" aria-label="字幕位置" /></label>
                <button id="subtitleReset">恢复默认样式</button>
              </div>
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
              const partToggle = document.getElementById('partToggle');
              const partMenu = document.getElementById('partMenu');
              const partList = document.getElementById('partList');
              const partCount = document.getElementById('partCount');
              const subtitleLayer = document.getElementById('subtitles');
              const subtitleText = document.getElementById('subtitleText');
              const subtitleToggle = document.getElementById('subtitleToggle');
              const subtitleMenu = document.getElementById('subtitleMenu');
              const subtitleTrackList = document.getElementById('subtitleTracks');
              const subtitleSettingsButton = document.getElementById('subtitleSettingsButton');
              const subtitleSettings = document.getElementById('subtitleSettings');
              const subtitleSize = document.getElementById('subtitleSize');
              const subtitleBottom = document.getElementById('subtitleBottom');
              const subtitleSizeValue = document.getElementById('subtitleSizeValue');
              const subtitleBottomValue = document.getElementById('subtitleBottomValue');
              const subtitleReset = document.getElementById('subtitleReset');
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
              let subtitleTracks = [];
              let subtitleTrackId = '';
              let subtitleEnabled = true;
              let subtitleRequiresLogin = false;
              let subtitleError = '';
              let currentVideoKey = '';
              let currentIdentity = null;
              let watchHistoryNotified = false;
              let partItems = [];
              let partMenuOpen = false;
              let partsBusy = false;
              let loadGeneration = 0;
              let pendingMetadata = null;
              let pendingCanPlay = null;
              let subtitleMenuOpen = false;
              let controlsPointerInside = false;
              let subtitleLoading = false;
              let subtitleFontScale = 1;
              let subtitleBottomPercent = 8;
              let subtitleLastTime = -1;
              let subtitleLastText = '';
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

              const updatePartsButton = () => {
                const available = partItems.length > 1;
                const current = partItems.find(part => part.cid === Number(currentIdentity?.cid));
                partToggle.disabled = !available || partsBusy;
                partToggle.setAttribute('aria-disabled', partToggle.disabled ? 'true' : 'false');
                partToggle.classList.toggle('on', available && partMenuOpen);
                partToggle.title = partsBusy ? '正在加载选集…'
                  : available ? `选集 · P${current?.page || 1}/${partItems.length} ${current?.part || ''}`
                  : partItems.length === 1 ? '此视频只有 1 P' : '选集读取中';
                partToggle.setAttribute('aria-label', partToggle.title);
                partCount.textContent = available ? `P${current?.page || 1} / 共 ${partItems.length} P` : '';
              };

              const setPartMenuOpen = open => {
                partMenuOpen = !!open && !partsBusy && partItems.length > 1;
                if (partMenuOpen) {
                  setSubtitleMenuOpen(false);
                  setCommentsOpen(false);
                }
                partMenu.classList.toggle('open', partMenuOpen);
                wrap.classList.toggle('part-menu-open', partMenuOpen);
                wrap.classList.toggle('controls-hover', partMenuOpen || subtitleMenuOpen || controlsPointerInside);
                partMenu.setAttribute('aria-hidden', partMenuOpen ? 'false' : 'true');
                partToggle.setAttribute('aria-expanded', partMenuOpen ? 'true' : 'false');
                if (partMenuOpen) partList.querySelector('.selected')?.scrollIntoView({ block: 'nearest' });
                updatePartsButton();
                updateSubtitleLayout();
                wakeUi();
              };

              const setPartsBusy = busy => {
                partsBusy = !!busy;
                if (partsBusy) setPartMenuOpen(false);
                updatePartsButton();
              };

              const applyPartsSnapshot = snapshot => {
                if (disposed || !currentVideoKey || videoKey(snapshot) !== currentVideoKey) return false;
                const parts = (Array.isArray(snapshot?.parts) ? snapshot.parts : [])
                  .map(part => ({ cid: Number(part.cid), page: Number(part.page), part: String(part.part || ''), duration: Number(part.duration) || 0 }))
                  .filter(part => Number.isSafeInteger(part.cid) && part.cid > 0 && Number.isSafeInteger(part.page) && part.page > 0)
                  .sort((a, b) => a.page - b.page);
                if (!parts.some(part => part.cid === Number(currentIdentity?.cid))) return false;
                partItems = parts;
                partList.replaceChildren();
                for (const part of partItems) {
                  const selected = part.cid === Number(currentIdentity?.cid);
                  const option = document.createElement('button');
                  option.className = 'part-option' + (selected ? ' selected' : '');
                  option.title = `P${part.page} ${part.part}`;
                  option.setAttribute('role', 'menuitemradio');
                  option.setAttribute('aria-checked', selected ? 'true' : 'false');
                  for (const [className, text] of [
                    ['part-number', `P${part.page}`], ['part-name', part.part || `第 ${part.page} P`],
                    ['part-duration', formatTime(part.duration)], ['part-check', selected ? '✓' : '']
                  ]) {
                    const label = document.createElement('span');
                    label.className = className;
                    label.textContent = text;
                    option.appendChild(label);
                  }
                  option.addEventListener('click', () => {
                    if (disposed || partsBusy) return;
                    setPartMenuOpen(false);
                    if (part.cid === Number(currentIdentity?.cid)) return;
                    setPartsBusy(true);
                    try {
                      chrome.webview.postMessage('LOCAL_PLAYER_PART:' + JSON.stringify({
                        bvid: currentIdentity.bvid, cid: part.cid, page: part.page
                      }));
                    } catch (_) {
                      setPartsBusy(false);
                      showHint('选集切换失败，请重试');
                    }
                  });
                  partList.appendChild(option);
                }
                setPartMenuOpen(false);
                return true;
              };

              const selectedSubtitleTrack = () =>
                subtitleTracks.find(track => track.id === subtitleTrackId) || null;

              const notifySubtitlePreference = () => {
                try {
                  chrome.webview.postMessage('LOCAL_PLAYER_SUBTITLE:' + JSON.stringify({
                    enabled: subtitleEnabled,
                    language: selectedSubtitleTrack()?.lan || '',
                    fontScale: subtitleFontScale,
                    bottomPercent: subtitleBottomPercent
                  }));
                } catch (_) {}
              };

              const updateSubtitleLayout = () => {
                const width = wrap.clientWidth || 1;
                const height = wrap.clientHeight || 1;
                const sourceWidth = video.videoWidth || width;
                const sourceHeight = video.videoHeight || height;
                const scale = Math.min(width / sourceWidth, height / sourceHeight);
                const pictureWidth = sourceWidth * scale;
                const pictureHeight = sourceHeight * scale;
                const pictureBottom = (height - pictureHeight) / 2;
                let availableWidth = commentsOpen
                  ? Math.max(120, width - commentPanel.getBoundingClientRect().width - 16)
                  : width;
                if (subtitleMenuOpen) {
                  availableWidth = Math.min(availableWidth, Math.max(120,
                    subtitleMenu.getBoundingClientRect().left - wrap.getBoundingClientRect().left - 16));
                }
                if (partMenuOpen) {
                  availableWidth = Math.min(availableWidth, Math.max(120,
                    partMenu.getBoundingClientRect().left - wrap.getBoundingClientRect().left - 16));
                }
                const textWidth = Math.max(100, Math.min(pictureWidth * 0.88, availableWidth * 0.92));
                const controlsVisible = wrap.classList.contains('controls-hover') || subtitleMenuOpen || partMenuOpen;
                subtitleLayer.style.width = `${textWidth}px`;
                subtitleLayer.style.left = `${(availableWidth - textWidth) / 2}px`;
                subtitleLayer.style.bottom = `${Math.max(
                  pictureBottom + Math.max(12, pictureHeight * subtitleBottomPercent / 100),
                  controlsVisible ? 70 : 0)}px`;
                subtitleLayer.style.fontSize = `${Math.max(16, Math.min(34, pictureWidth * 0.026)) * subtitleFontScale}px`;
                subtitleSize.value = String(Math.round(subtitleFontScale * 100));
                subtitleBottom.value = String(Math.round(subtitleBottomPercent));
                subtitleSizeValue.textContent = `${Math.round(subtitleFontScale * 100)}%`;
                subtitleBottomValue.textContent = `${Math.round(subtitleBottomPercent)}%`;
              };

              const updateSubtitleCue = force => {
                const track = selectedSubtitleTrack();
                if (subtitleLoading || !subtitleEnabled || !track || video.ended) {
                  if (subtitleLastText) subtitleText.textContent = '';
                  subtitleLastText = '';
                  subtitleLastTime = -1;
                  subtitleLayer.classList.remove('visible');
                  return;
                }
                const time = Math.max(0, Number(video.currentTime) || 0);
                if (!force && time === subtitleLastTime) return;
                subtitleLastTime = time;
                let low = 0;
                let high = track.cues.length;
                while (low < high) {
                  const middle = (low + high) >>> 1;
                  if (track.cues[middle].f <= time) low = middle + 1;
                  else high = middle;
                }
                const active = [];
                // Prefix maximum end times also handle overlapping cues without scanning the track.
                for (let i = low - 1; i >= 0 && track.maxEnd[i] > time; i--) {
                  if (track.cues[i].t > time) active.unshift(track.cues[i].c);
                }
                const text = [...new Set(active)].join('\n');
                if (text !== subtitleLastText) {
                  subtitleLastText = text;
                  subtitleText.textContent = text;
                }
                subtitleLayer.classList.toggle('visible', text.length > 0);
              };

              const subtitleUnavailableReason = () => subtitleError ||
                (subtitleRequiresLogin ? '登录后可用字幕' : '此视频暂无可用字幕');

              const videoKey = identity => identity?.bvid && Number(identity.aid) > 0 && Number(identity.cid) > 0
                ? `${identity.bvid}:${identity.aid}:${identity.cid}` : '';

              const updateSubtitleButton = () => {
                const available = subtitleTracks.length > 0;
                const track = selectedSubtitleTrack();
                subtitleToggle.classList.toggle('on', available && subtitleEnabled);
                subtitleToggle.classList.toggle('off', !available || !subtitleEnabled);
                subtitleToggle.classList.toggle('unavailable', !available);
                subtitleToggle.setAttribute('aria-disabled', available ? 'false' : 'true');
                subtitleToggle.title = !available
                  ? subtitleUnavailableReason()
                  : subtitleEnabled ? `字幕：${track?.name || '已开启'}` : '字幕已关闭';
              };

              const setSubtitleMenuOpen = open => {
                if (open) setPartMenuOpen(false);
                subtitleMenuOpen = !!open && subtitleTracks.length > 0;
                subtitleMenu.classList.toggle('open', subtitleMenuOpen);
                wrap.classList.toggle('subtitle-menu-open', subtitleMenuOpen);
                wrap.classList.toggle('controls-hover', subtitleMenuOpen || partMenuOpen || controlsPointerInside);
                subtitleMenu.setAttribute('aria-hidden', subtitleMenuOpen ? 'false' : 'true');
                subtitleToggle.setAttribute('aria-expanded', subtitleMenuOpen ? 'true' : 'false');
                updateSubtitleLayout();
                wakeUi();
              };

              const setSubtitleSelection = (id, enabled, notify) => {
                if (subtitleTracks.some(track => track.id === id)) subtitleTrackId = id;
                subtitleEnabled = !!enabled;
                subtitleLastTime = -1;
                updateSubtitleButton();
                renderSubtitleMenu();
                updateSubtitleCue(true);
                setSubtitleMenuOpen(false);
                if (notify) notifySubtitlePreference();
              };

              const renderSubtitleMenu = () => {
                subtitleTrackList.replaceChildren();
                const addOption = (id, name, ai, selected, enabled) => {
                  const option = document.createElement('button');
                  option.className = 'subtitle-option' + (selected ? ' selected' : '');
                  option.setAttribute('role', 'menuitemradio');
                  option.setAttribute('aria-checked', selected ? 'true' : 'false');
                  const label = document.createElement('span');
                  label.className = 'subtitle-option-name';
                  label.textContent = name;
                  option.appendChild(label);
                  if (ai) {
                    const badge = document.createElement('span');
                    badge.className = 'subtitle-ai';
                    badge.textContent = 'AI';
                    option.appendChild(badge);
                  }
                  const check = document.createElement('span');
                  check.className = 'subtitle-check';
                  check.textContent = selected ? '✓' : '';
                  option.appendChild(check);
                  option.addEventListener('click', () => setSubtitleSelection(id, enabled, true));
                  subtitleTrackList.appendChild(option);
                };
                addOption(subtitleTrackId, '关闭', false, !subtitleEnabled, false);
                for (const track of subtitleTracks) {
                  const name = track.ai
                    ? track.name.replace(/（自动(?:生成|翻译)）|\(自动(?:生成|翻译)\)|（AI）|\(AI\)/gi, '').trim()
                    : track.name;
                  addOption(track.id, name || track.lan || '字幕', track.ai,
                    subtitleEnabled && track.id === subtitleTrackId, true);
                }
              };

              const applySubtitleSnapshot = (snapshot, enabled, preferredLanguage, fontScale, bottomPercent) => {
                if (disposed) return false;
                if (!currentVideoKey || videoKey(snapshot) !== currentVideoKey) {
                  // Late responses from a previous video/part must not replace current captions.
                  if (subtitleTracks.length === 0) {
                    subtitleError = '字幕与当前视频不匹配，已阻止加载';
                    updateSubtitleButton();
                  }
                  return false;
                }
                subtitleTracks = (Array.isArray(snapshot?.tracks) ? snapshot.tracks : []).map((item, index) => {
                  const cues = (Array.isArray(item?.cues) ? item.cues : [])
                    .map(cue => ({ f: Number(cue?.f), t: Number(cue?.t), c: String(cue?.c || '') }))
                    .filter(cue => Number.isFinite(cue.f) && Number.isFinite(cue.t) && cue.t > cue.f && cue.c)
                    .sort((a, b) => a.f - b.f || a.t - b.t);
                  let end = 0;
                  return {
                    id: String(item?.id || item?.lan || `track-${index}`),
                    lan: String(item?.lan || ''),
                    name: String(item?.name || item?.lan || '字幕'),
                    ai: !!item?.ai,
                    def: !!item?.def,
                    cues,
                    maxEnd: cues.map(cue => (end = Math.max(end, cue.t)))
                  };
                }).filter(track => track.cues.length > 0);
                const preferred = subtitleTracks.find(track => track.lan === preferredLanguage)
                  || subtitleTracks.find(track => track.def)
                  || subtitleTracks[0];
                subtitleTrackId = preferred?.id || '';
                subtitleEnabled = !!enabled;
                subtitleRequiresLogin = !!snapshot?.login;
                subtitleError = String(snapshot?.error || '');
                subtitleFontScale = Number.isFinite(Number(fontScale))
                  ? Math.max(0.75, Math.min(1.5, Number(fontScale))) : 1;
                subtitleBottomPercent = Number.isFinite(Number(bottomPercent))
                  ? Math.max(4, Math.min(25, Number(bottomPercent))) : 8;
                subtitleLastTime = -1;
                updateSubtitleButton();
                renderSubtitleMenu();
                setSubtitleMenuOpen(false);
                updateSubtitleCue(true);
                return true;
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
                if (open) setPartMenuOpen(false);
                commentsOpen = !!open;
                if (commentsOpen) setSubtitleMenuOpen(false);
                wrap.classList.toggle('comments-open', commentsOpen);
                wrap.classList.toggle('right-hover', commentsOpen);
                commentPanel.setAttribute('aria-hidden', commentsOpen ? 'false' : 'true');
                commentToggle.title = commentsOpen ? '关闭评论' : '打开评论';
                commentToggle.setAttribute('aria-label', commentToggle.title);
                commentToggle.setAttribute('aria-expanded', commentsOpen ? 'true' : 'false');
                clearTimeout(hideTimer);
                if (!commentsOpen) wakeUi();
                updateSubtitleLayout();
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

              const notifyWatchHistory = () => {
                const position = Number(video.currentTime) || 0;
                if (watchHistoryNotified || disposed || video.paused || video.ended ||
                    position < 1 || !videoKey(currentIdentity)) return;
                watchHistoryNotified = true;
                try {
                  chrome.webview.postMessage('LOCAL_PLAYER_WATCHED:' + JSON.stringify({
                    bvid: currentIdentity.bvid,
                    aid: Number(currentIdentity.aid),
                    cid: Number(currentIdentity.cid),
                    time: position
                  }));
                } catch (_) {}
              };

              const updateButtons = () => {
                const playing = !video.paused && !video.ended;
                wrap.classList.toggle('playing', playing);
                playButton.textContent = playing ? '❚❚' : '▶';
                muteButton.textContent = userMuted || userVolume <= 0.001 ? '🔇' : '🔊';
                dmLayer.classList.toggle('paused', !playing);
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
                updateSubtitleCue(false);
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
                if (!video.paused && !commentsOpen && !subtitleMenuOpen && !partMenuOpen) {
                  hideTimer = setTimeout(() => wrap.classList.add('hide-ui'), 2200);
                }
              };

              playButton.addEventListener('click', togglePlay);
              centerPlay.addEventListener('click', togglePlay);
              surface.addEventListener('click', () => {
                if (partMenuOpen) setPartMenuOpen(false);
                else if (subtitleMenuOpen) setSubtitleMenuOpen(false);
                else togglePlay();
              });
              wrap.addEventListener('mousemove', event => {
                const bounds = wrap.getBoundingClientRect();
                const inRightZone = event.clientX >= bounds.left + bounds.width * 0.70;
                const inControlsZone = event.clientY >= bounds.bottom - 82;
                controlsPointerInside = inControlsZone;
                wrap.classList.toggle('right-hover', commentsOpen || inRightZone);
                const changed = wrap.classList.contains('controls-hover') !== (inControlsZone || subtitleMenuOpen || partMenuOpen);
                wrap.classList.toggle('controls-hover', inControlsZone || subtitleMenuOpen || partMenuOpen);
                if (changed) updateSubtitleLayout();
                wakeUi();
              });
              wrap.addEventListener('mouseleave', () => {
                controlsPointerInside = false;
                wrap.classList.toggle('right-hover', commentsOpen);
                wrap.classList.toggle('controls-hover', subtitleMenuOpen || partMenuOpen);
                updateSubtitleLayout();
                if (!video.paused && !commentsOpen && !subtitleMenuOpen && !partMenuOpen) wrap.classList.add('hide-ui');
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

              partToggle.addEventListener('click', event => {
                event.stopPropagation();
                setPartMenuOpen(!partMenuOpen);
              });

              subtitleToggle.addEventListener('click', event => {
                event.stopPropagation();
                if (subtitleTracks.length === 0) {
                  showHint(subtitleUnavailableReason());
                  return;
                }
                setSubtitleMenuOpen(!subtitleMenuOpen);
              });
              subtitleSettingsButton.addEventListener('click', () => {
                subtitleSettings.hidden = !subtitleSettings.hidden;
                subtitleSettingsButton.setAttribute('aria-expanded', subtitleSettings.hidden ? 'false' : 'true');
              });
              subtitleSize.addEventListener('input', () => {
                subtitleFontScale = Math.max(0.75, Math.min(1.5, Number(subtitleSize.value) / 100));
                updateSubtitleLayout();
                notifySubtitlePreference();
              });
              subtitleBottom.addEventListener('input', () => {
                subtitleBottomPercent = Math.max(4, Math.min(25, Number(subtitleBottom.value)));
                updateSubtitleLayout();
                notifySubtitlePreference();
              });
              subtitleReset.addEventListener('click', () => {
                subtitleFontScale = 1;
                subtitleBottomPercent = 8;
                updateSubtitleLayout();
                notifySubtitlePreference();
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
                updateSubtitleCue(true);
              });
              video.addEventListener('seeking', () => {
                syncAudio(true);
                resetDanmakuCursor();
                updateSubtitleCue(true);
              });
              video.addEventListener('seeked', () => {
                resetDanmakuCursor();
                startAudio();
                updateSubtitleCue(true);
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
              video.addEventListener('timeupdate', () => updateSubtitleCue(false));
              video.addEventListener('timeupdate', notifyWatchHistory);
              video.addEventListener('durationchange', updateTimeline);
              video.addEventListener('ended', () => {
                if (hasAudioTrack) audio.pause();
                updateButtons();
                updateSubtitleCue(true);
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
                if (event.key === 'Escape' && partMenuOpen) {
                  event.preventDefault();
                  setPartMenuOpen(false);
                  partToggle.focus();
                  return;
                }
                if (event.key === 'Escape' && subtitleMenuOpen) {
                  event.preventDefault();
                  setSubtitleMenuOpen(false);
                  subtitleToggle.focus();
                  return;
                }
                if (event.key === 'Escape' && commentsOpen) {
                  event.preventDefault();
                  setCommentsOpen(false);
                  return;
                }
                if (event.target instanceof HTMLElement &&
                    (event.target.closest('#commentPanel') || event.target === commentToggle ||
                     event.target.closest('#subtitleMenu') || event.target === subtitleToggle ||
                     event.target.closest('#partMenu') || event.target === partToggle)) {
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

              const subtitleResizeObserver = new ResizeObserver(updateSubtitleLayout);
              subtitleResizeObserver.observe(wrap);
              document.addEventListener('fullscreenchange', updateSubtitleLayout);

              const cancelPendingLoad = () => {
                loadGeneration += 1;
                if (pendingMetadata) video.removeEventListener('loadedmetadata', pendingMetadata);
                if (pendingCanPlay) video.removeEventListener('canplay', pendingCanPlay);
                pendingMetadata = null;
                pendingCanPlay = null;
              };

              window.biliLocalPlayer = {
                suspend() {
                  cancelPendingLoad();
                  ignoreMediaError = true;
                  video.pause();
                  audio.pause();
                  setPartMenuOpen(false);
                },
                load(videoUrl, audioUrl, startTime, identity) {
                  if (disposed) return;
                  cancelPendingLoad();
                  const generation = loadGeneration;
                  const nextVideoKey = videoKey(identity);
                  if (!nextVideoKey || nextVideoKey !== currentVideoKey) {
                    dmItems = [];
                    resetDanmakuCursor();
                    partItems = [];
                    partList.replaceChildren();
                    subtitleTracks = [];
                    subtitleTrackId = '';
                    subtitleError = '';
                    subtitleRequiresLogin = false;
                    subtitleLastText = '';
                    subtitleText.textContent = '';
                    updateSubtitleButton();
                    renderSubtitleMenu();
                  }
                  currentVideoKey = nextVideoKey;
                  currentIdentity = nextVideoKey ? { ...identity, page: Math.max(1, Number(identity.page) || 1) } : null;
                  subtitleLoading = true;
                  subtitleLastTime = -1;
                  subtitleLayer.classList.remove('visible');
                  setSubtitleMenuOpen(false);
                  setPartMenuOpen(false);
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
                  setTimeout(() => { if (generation === loadGeneration) ignoreMediaError = false; }, 0);

                  const requestedStart = Math.max(0, Number(startTime) || 0);
                  const applyStart = () => {
                    if (Number.isFinite(video.duration)) {
                      video.currentTime = Math.min(requestedStart, Math.max(0, video.duration - 0.5));
                    }
                    syncAudio(true);
                    updateTimeline();
                  };

                  pendingMetadata = () => {
                    if (disposed || generation !== loadGeneration) return;
                    pendingMetadata = null;
                    subtitleLoading = false;
                    applyStart();
                    resetDanmakuCursor();
                    updateSubtitleLayout();
                    updateSubtitleCue(true);
                  };
                  video.addEventListener('loadedmetadata', pendingMetadata, { once: true });
                  pendingCanPlay = async () => {
                    if (disposed || generation !== loadGeneration) return;
                    pendingCanPlay = null;
                    try {
                      await video.play();
                      if (disposed || generation !== loadGeneration) return;
                      await startAudio();
                    } catch (_) {
                      if (disposed || generation !== loadGeneration) return;
                      wrap.classList.remove('buffering');
                      showHint('点击播放');
                    }
                  };
                  video.addEventListener('canplay', pendingCanPlay, { once: true });
                },
                setParts(snapshot) {
                  return applyPartsSnapshot(snapshot);
                },
                setPartsBusy(busy) {
                  setPartsBusy(busy);
                },
                setDanmaku(items) {
                  dmItems = Array.isArray(items) ? items.slice() : [];
                  dmItems.sort((a, b) => (a.t || 0) - (b.t || 0));
                  resetDanmakuCursor();
                },
                setDanmakuVisible(on, notify) {
                  setDanmakuVisible(on, notify);
                },
                setSubtitles(snapshot, enabled, preferredLanguage, fontScale, bottomPercent) {
                  return applySubtitleSnapshot(snapshot, enabled, preferredLanguage, fontScale, bottomPercent);
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
                    cid: Number(currentIdentity?.cid) || 0,
                    page: currentIdentity?.page || 1,
                    time: subtitleLoading ? 0 : Number(video.currentTime) || 0,
                    duration: !subtitleLoading && Number.isFinite(video.duration) ? video.duration : 0,
                    paused: video.paused,
                    ended: video.ended
                  };
                },
                dispose() {
                  disposed = true;
                  cancelPendingLoad();
                  cancelAnimationFrame(dmRaf);
                  clearInterval(syncTimer);
                  clearTimeout(hideTimer);
                  subtitleResizeObserver.disconnect();
                  document.removeEventListener('fullscreenchange', updateSubtitleLayout);
                  video.pause();
                  audio.pause();
                  video.removeAttribute('src');
                  audio.removeAttribute('src');
                  video.load();
                  audio.load();
                  dmLayer.innerHTML = '';
                  subtitleTracks = [];
                  subtitleText.textContent = '';
                  subtitleLayer.classList.remove('visible');
                  subtitleTrackList.replaceChildren();
                  partItems = [];
                  partList.replaceChildren();
                  setPartMenuOpen(false);
                  commentsList.replaceChildren();
                }
              };

              applyVolume();
              updateTimeline();
              updateButtons();
              updatePartsButton();
              updateSubtitleButton();
              updateSubtitleLayout();
              dmRaf = requestAnimationFrame(dmTick);
            })();
          </script>
        </body>
        </html>
        """;

    private sealed record PlaybackState(long Cid, int PageNumber, double Position, double Duration, bool Ended);
}
