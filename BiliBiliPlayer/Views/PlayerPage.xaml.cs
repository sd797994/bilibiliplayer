using System.Globalization;
using System.Text.Json;
using BiliBiliPlayer.Models;
using BiliBiliPlayer.Services;

namespace BiliBiliPlayer.Views;

public partial class PlayerPage : ContentPage
{
    private const int MaximumPreferredQuality = 80;

    private readonly VideoItem _video;
    private readonly BiliPlaybackResolver _resolver = new();
    private readonly CancellationTokenSource _pageCancellation = new();
    private bool _isClosing;
    private bool _playerReady;
    private bool _hasStarted;
    private bool _isResolving;
    private int _selectedQuality;
    private PlaybackManifest? _playbackManifest;
    private WebView? _resolverWebView;
#if WINDOWS
    private bool _nativePlayerEventsAttached;
    private Microsoft.UI.Xaml.Controls.WebView2? _nativePlayerWebView;
#endif

    public PlayerPage(VideoItem video)
    {
        InitializeComponent();

        _video = video;
        TitleLabel.Text = video.Title;
        MetaLabel.Text = $"{video.OwnerName}  ·  {video.Bvid}";
        UpdateQualityUi(0);

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
        await ResolveAndPlayAsync(MaximumPreferredQuality, preservePosition: false, autoSelectHighest: true);
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
            if (!message.StartsWith("LOCAL_PLAYER_ERROR:", StringComparison.Ordinal))
            {
                return;
            }

            Dispatcher.Dispatch(() =>
            {
                // Signed CDN URLs may have expired or been rejected. Force the next Retry to
                // create a fresh temporary resolver instead of reusing the in-memory snapshot.
                _playbackManifest = null;
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

        QualityMenu.IsVisible = !QualityMenu.IsVisible;
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

    private async Task ResolveAndPlayAsync(int quality, bool preservePosition, bool autoSelectHighest = false)
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
            var position = preservePosition ? await GetPlaybackPositionAsync() : 0d;

            if (_playbackManifest is null)
            {
                // Resolve once at our highest UI quality. The returned DASH snapshot contains
                // all lower representations, then the temporary resolver WebView2 is destroyed.
                _playbackManifest = await ResolveWithTemporaryWebViewAsync(
                    MaximumPreferredQuality,
                    _pageCancellation.Token);
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
            await LoadPlaybackSourceAsync(source, position);

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
        var audioJson = JsonSerializer.Serialize(source.AudioUrl ?? string.Empty);
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

    private async Task<double> GetPlaybackPositionAsync()
    {
        try
        {
            var raw = await ExecutePlayerScriptAsync(
                "JSON.stringify(window.biliLocalPlayer?.state() ?? { time: 0 })");
            var json = DecodeScriptString(raw);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("time", out var timeElement) &&
                timeElement.TryGetDouble(out var time))
            {
                return Math.Max(0, time);
            }
        }
        catch
        {
        }

        return 0;
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
            :root{color-scheme:dark;--pink:#fb7299;--text:#f4f5f7;--muted:#a3a8b2;--panel:rgba(13,15,20,.92)}
            *{box-sizing:border-box}
            html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#08090c;color:var(--text);font-family:Segoe UI,Arial,sans-serif;user-select:none}
            #wrap{position:relative;width:100%;height:100%;display:flex;align-items:center;justify-content:center;background:#08090c}
            video{width:100%;height:100%;object-fit:contain;background:#08090c}
            #surface{position:absolute;inset:0;cursor:pointer}
            #shade{position:absolute;left:0;right:0;bottom:0;height:112px;background:linear-gradient(transparent,rgba(0,0,0,.78));pointer-events:none}
            #controls{position:absolute;left:14px;right:14px;bottom:10px;height:48px;display:flex;align-items:center;gap:10px;padding:0 10px;border:1px solid rgba(255,255,255,.08);border-radius:13px;background:rgba(15,17,22,.78);backdrop-filter:blur(12px);transition:opacity .18s,transform .18s}
            #wrap.playing.hide-ui #controls{opacity:0;transform:translateY(8px);pointer-events:none}
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
            audio{display:none}
            @keyframes spin{to{transform:rotate(360deg)}}
          </style>
        </head>
        <body>
          <div id="wrap">
            <video id="video" playsinline preload="auto"></video>
            <audio id="audio" preload="auto"></audio>
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
              <button id="fullscreen" class="icon" title="全屏">⛶</button>
            </div>
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

              let hasAudioTrack = false;
              let disposed = false;
              let userMuted = false;
              let userVolume = 1;
              let seeking = false;
              let syncTimer = 0;
              let hideTimer = 0;

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

              const updateTimeline = () => {
                const duration = Number.isFinite(video.duration) ? video.duration : 0;
                if (!seeking && duration > 0) {
                  seek.value = String(Math.round((video.currentTime / duration) * 1000));
                }
                time.textContent = `${formatTime(video.currentTime)} / ${formatTime(duration)}`;
              };

              const updateButtons = () => {
                const playing = !video.paused && !video.ended;
                wrap.classList.toggle('playing', playing);
                playButton.textContent = playing ? '❚❚' : '▶';
                centerPlay.textContent = '▶';
                muteButton.textContent = userMuted || userVolume <= 0.001 ? '🔇' : '🔊';
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
                if (!video.paused) {
                  hideTimer = setTimeout(() => wrap.classList.add('hide-ui'), 2200);
                }
              };

              playButton.addEventListener('click', togglePlay);
              centerPlay.addEventListener('click', togglePlay);
              surface.addEventListener('click', togglePlay);
              wrap.addEventListener('mousemove', wakeUi);
              wrap.addEventListener('mouseleave', () => { if (!video.paused) wrap.classList.add('hide-ui'); });

              muteButton.addEventListener('click', () => {
                userMuted = !userMuted;
                applyVolume();
              });

              volume.addEventListener('input', () => {
                userVolume = Math.max(0, Math.min(1, Number(volume.value) || 0));
                userMuted = userVolume <= 0.001;
                applyVolume();
              });

              seek.addEventListener('input', () => {
                seeking = true;
                const duration = Number.isFinite(video.duration) ? video.duration : 0;
                if (duration > 0) {
                  time.textContent = `${formatTime((Number(seek.value) / 1000) * duration)} / ${formatTime(duration)}`;
                }
              });

              seek.addEventListener('change', () => {
                const duration = Number.isFinite(video.duration) ? video.duration : 0;
                if (duration > 0) {
                  video.currentTime = (Number(seek.value) / 1000) * duration;
                  syncAudio(true);
                }
                seeking = false;
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
              video.addEventListener('seeking', () => syncAudio(true));
              video.addEventListener('seeked', startAudio);
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
                wrap.classList.remove('buffering');
                const err = video.error;
                postError('视频轨加载失败' + (err ? `（MediaError ${err.code}）` : ''));
              });
              audio.addEventListener('error', () => {
                const err = audio.error;
                postError('音频轨加载失败' + (err ? `（MediaError ${err.code}）` : ''));
              });

              document.addEventListener('keydown', event => {
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

                  video.removeAttribute('src');
                  audio.removeAttribute('src');
                  video.load();
                  audio.load();

                  hasAudioTrack = Boolean(audioUrl);
                  video.src = videoUrl;
                  if (hasAudioTrack) audio.src = audioUrl;
                  applyVolume();
                  video.load();
                  if (hasAudioTrack) audio.load();

                  const requestedStart = Math.max(0, Number(startTime) || 0);
                  const applyStart = () => {
                    if (requestedStart > 0 && Number.isFinite(video.duration)) {
                      video.currentTime = Math.min(requestedStart, Math.max(0, video.duration - 0.5));
                    }
                    syncAudio(true);
                    updateTimeline();
                  };

                  video.addEventListener('loadedmetadata', applyStart, { once: true });
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
                state() {
                  return { time: Number(video.currentTime) || 0, paused: video.paused };
                },
                dispose() {
                  disposed = true;
                  clearInterval(syncTimer);
                  clearTimeout(hideTimer);
                  video.pause();
                  audio.pause();
                  video.removeAttribute('src');
                  audio.removeAttribute('src');
                  video.load();
                  audio.load();
                }
              };

              applyVolume();
              updateTimeline();
              updateButtons();
            })();
          </script>
        </body>
        </html>
        """;
}
