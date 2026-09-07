using BiliBiliPlayer.Models;
using BiliBiliPlayer.Services;
using BiliBiliPlayer.ViewModels;
using BiliBiliPlayer.Views;

namespace BiliBiliPlayer;

public partial class MainPage : ContentPage
{
    private const double PullRefreshThreshold = 82;
    private const double PullRefreshMaxDistance = 116;
    private readonly BiliApiService _apiService = new();
    private readonly MainViewModel _viewModel;
    private CancellationTokenSource? _watchHistoryCloseCancellation;
    private bool _initialized;
    private bool _isHomeActive;
#if WINDOWS
    private Microsoft.UI.Xaml.Controls.ScrollViewer? _feedScrollViewer;
    private Microsoft.UI.Xaml.Controls.ScrollViewer? _watchHistoryScrollViewer;
    private Microsoft.UI.Xaml.UIElement? _feedPointerSurface;
    private readonly Microsoft.UI.Xaml.Input.PointerEventHandler _feedPointerPressedHandler;
    private readonly Microsoft.UI.Xaml.Input.PointerEventHandler _feedPointerMovedHandler;
    private readonly Microsoft.UI.Xaml.Input.PointerEventHandler _feedPointerReleasedHandler;
    private readonly Microsoft.UI.Xaml.Input.PointerEventHandler _feedPointerCanceledHandler;
    private bool _isPullPointerDown;
    private bool _isPullDragging;
    private bool _isPullRefreshRunning;
    private uint _pullPointerId;
    private double _pullStartX;
    private double _pullStartY;
    private double _pullDistance;
#endif

    public MainPage()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(_apiService);
        BindingContext = _viewModel;
#if WINDOWS
        _feedPointerPressedHandler = OnFeedPointerPressed;
        _feedPointerMovedHandler = OnFeedPointerMoved;
        _feedPointerReleasedHandler = OnFeedPointerReleased;
        _feedPointerCanceledHandler = OnFeedPointerCanceled;
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _isHomeActive = true;

        if (_initialized)
        {
            // Returning from the player can add a new record to the signed-in account.
            // Keep the old collection hidden for now and fetch a fresh first page on hover.
            _viewModel.InvalidateWatchHistory();
            return;
        }

        _initialized = true;
#if DEBUG && WINDOWS
        if (Diagnostics.NativePlaybackSmoke.IsRequested)
        {
            await Diagnostics.NativePlaybackSmoke.RunAsync(this);
            return;
        }
#endif
        var cookieHeader = await WebViewCookieBridge.GetBilibiliCookieHeaderAsync(SessionWebView);
        var profile = BiliSessionStore.LoadProfile();

        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            try
            {
                profile = await _apiService.GetCurrentUserAsync(cookieHeader);
            }
            catch
            {
                // Keep the locally cached public profile when login validation is temporarily offline.
            }
        }

        _viewModel.ConfigureSession(profile, cookieHeader);
        await _viewModel.InitializeAsync();
    }

    protected override void OnDisappearing()
    {
        _isHomeActive = false;
        CancelWatchHistoryClose();
        _viewModel.CloseWatchHistory();
        base.OnDisappearing();
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

#if WINDOWS
        if (Handler is null)
        {
            DetachFeedNativeHandlers();
            DetachWatchHistoryNativeScroll();
        }
#endif
    }

    private void OnFeedScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        if (!_isHomeActive ||
            e.LastVisibleItemIndex < 0 ||
            e.LastVisibleItemIndex < _viewModel.Videos.Count - 8)
        {
            return;
        }

        RequestMoreVideos();
    }

#if WINDOWS
    private async void OnFeedCollectionLoaded(object? sender, EventArgs e)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (FeedCollectionView.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement root)
            {
                AttachPullToRefresh(root);
                _feedScrollViewer ??=
                    FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(root);
                if (_feedScrollViewer is not null)
                {
                    _feedScrollViewer.ViewChanged += OnFeedScrollViewerViewChanged;
                    CheckNativeScrollPosition();
                    return;
                }
            }

            await Task.Delay(150);
        }
    }

    private void AttachPullToRefresh(Microsoft.UI.Xaml.UIElement pointerSurface)
    {
        if (ReferenceEquals(_feedPointerSurface, pointerSurface))
        {
            return;
        }

        DetachPullToRefresh();
        _feedPointerSurface = pointerSurface;
        pointerSurface.AddHandler(
            Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
            _feedPointerPressedHandler,
            true);
        pointerSurface.AddHandler(
            Microsoft.UI.Xaml.UIElement.PointerMovedEvent,
            _feedPointerMovedHandler,
            true);
        pointerSurface.AddHandler(
            Microsoft.UI.Xaml.UIElement.PointerReleasedEvent,
            _feedPointerReleasedHandler,
            true);
        pointerSurface.AddHandler(
            Microsoft.UI.Xaml.UIElement.PointerCanceledEvent,
            _feedPointerCanceledHandler,
            true);
        pointerSurface.AddHandler(
            Microsoft.UI.Xaml.UIElement.PointerCaptureLostEvent,
            _feedPointerCanceledHandler,
            true);
    }

    private void DetachPullToRefresh()
    {
        if (_feedPointerSurface is null)
        {
            return;
        }

        _feedPointerSurface.RemoveHandler(
            Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
            _feedPointerPressedHandler);
        _feedPointerSurface.RemoveHandler(
            Microsoft.UI.Xaml.UIElement.PointerMovedEvent,
            _feedPointerMovedHandler);
        _feedPointerSurface.RemoveHandler(
            Microsoft.UI.Xaml.UIElement.PointerReleasedEvent,
            _feedPointerReleasedHandler);
        _feedPointerSurface.RemoveHandler(
            Microsoft.UI.Xaml.UIElement.PointerCanceledEvent,
            _feedPointerCanceledHandler);
        _feedPointerSurface.RemoveHandler(
            Microsoft.UI.Xaml.UIElement.PointerCaptureLostEvent,
            _feedPointerCanceledHandler);
        _feedPointerSurface = null;
        ResetPullTracking();
    }

    private void DetachFeedNativeHandlers()
    {
        if (_feedScrollViewer is not null)
        {
            _feedScrollViewer.ViewChanged -= OnFeedScrollViewerViewChanged;
            _feedScrollViewer = null;
        }

        DetachPullToRefresh();
    }

    private void DetachWatchHistoryNativeScroll()
    {
        if (_watchHistoryScrollViewer is null)
        {
            return;
        }

        _watchHistoryScrollViewer.ViewChanged -= OnWatchHistoryScrollViewerViewChanged;
        _watchHistoryScrollViewer = null;
    }

    private void OnFeedPointerPressed(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isHomeActive ||
            _isPullRefreshRunning ||
            _viewModel.IsRefreshing ||
            _feedPointerSurface is null ||
            Navigation.ModalStack.Count != 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(_feedPointerSurface);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPullPointerDown = true;
        _isPullDragging = false;
        _pullDistance = 0;
        _pullPointerId = e.Pointer.PointerId;
        _pullStartX = point.Position.X;
        _pullStartY = point.Position.Y;
    }

    private void OnFeedPointerMoved(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPullPointerDown ||
            e.Pointer.PointerId != _pullPointerId ||
            _feedPointerSurface is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(_feedPointerSurface);
        if (!point.Properties.IsLeftButtonPressed)
        {
            var wasDragging = _isPullDragging;
            ResetPullTracking();
            if (wasDragging && !_isPullRefreshRunning)
            {
                _ = ResetPullVisualAsync();
            }

            return;
        }

        var deltaX = point.Position.X - _pullStartX;
        var deltaY = point.Position.Y - _pullStartY;
        if (!_isPullDragging)
        {
            if (deltaY <= 4)
            {
                return;
            }

            if (Math.Abs(deltaX) > deltaY)
            {
                ResetPullTracking();
                return;
            }

            _isPullDragging = true;
            _feedPointerSurface.CapturePointer(e.Pointer);
        }

        e.Handled = true;
        var pullDistance = Math.Clamp(deltaY * 0.52, 0, PullRefreshMaxDistance);
        UpdatePullVisual(pullDistance);
    }

    private async void OnFeedPointerReleased(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPullPointerDown || e.Pointer.PointerId != _pullPointerId)
        {
            return;
        }

        var shouldRefresh = _isPullDragging && _pullDistance >= PullRefreshThreshold;
        var wasDragging = _isPullDragging;
        ResetPullTracking();
        _feedPointerSurface?.ReleasePointerCapture(e.Pointer);

        if (!wasDragging)
        {
            return;
        }

        e.Handled = true;
        if (shouldRefresh)
        {
            await RunPullRefreshAsync();
        }
        else
        {
            await ResetPullVisualAsync();
        }
    }

    private async void OnFeedPointerCanceled(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPullPointerDown || e.Pointer.PointerId != _pullPointerId)
        {
            return;
        }

        var wasDragging = _isPullDragging;
        ResetPullTracking();
        if (wasDragging && !_isPullRefreshRunning)
        {
            e.Handled = true;
            await ResetPullVisualAsync();
        }
    }

    private void ResetPullTracking()
    {
        _isPullPointerDown = false;
        _isPullDragging = false;
        _pullPointerId = 0;
    }

    private void UpdatePullVisual(double pullDistance)
    {
        _pullDistance = pullDistance;
        // The indicator reaches full opacity at the same point that releasing triggers refresh.
        var progress = Math.Clamp(pullDistance / PullRefreshThreshold, 0, 1);
        PullRefreshIndicator.Opacity = progress;
        PullRefreshIndicator.Scale = 0.72 + (0.28 * progress);
        PullRefreshIndicator.TranslationY = -10 + (10 * progress);
        PullRefreshIcon.Rotation = -100 + (240 * progress);
    }

    private async Task RunPullRefreshAsync()
    {
        if (_isPullRefreshRunning)
        {
            return;
        }

        _isPullRefreshRunning = true;
        try
        {
            await Task.WhenAll(
                PullRefreshIndicator.FadeTo(1, 100, Easing.CubicOut),
                PullRefreshIndicator.ScaleTo(1, 100, Easing.CubicOut),
                PullRefreshIndicator.TranslateTo(0, 0, 100, Easing.CubicOut));

            var reloadTask = _viewModel.ReloadAsync();
            while (!reloadTask.IsCompleted)
            {
                PullRefreshIcon.Rotation %= 360;
                await PullRefreshIcon.RotateTo(
                    PullRefreshIcon.Rotation + 360,
                    650,
                    Easing.Linear);
            }

            await reloadTask;
        }
        finally
        {
            await ResetPullVisualAsync();
            _isPullRefreshRunning = false;
        }
    }

    private async Task ResetPullVisualAsync()
    {
        await Task.WhenAll(
            PullRefreshIndicator.FadeTo(0, 180, Easing.CubicIn),
            PullRefreshIndicator.ScaleTo(0.72, 180, Easing.CubicIn),
            PullRefreshIndicator.TranslateTo(0, -10, 180, Easing.CubicIn));
        _pullDistance = 0;
        PullRefreshIndicator.TranslationY = -10;
        PullRefreshIcon.Rotation = -100;
    }

    private void OnFeedScrollViewerViewChanged(
        object? sender,
        Microsoft.UI.Xaml.Controls.ScrollViewerViewChangedEventArgs e) =>
        CheckNativeScrollPosition();

    private void CheckNativeScrollPosition()
    {
        if (!_isHomeActive || _feedScrollViewer is null)
        {
            return;
        }

        var remainingHeight =
            _feedScrollViewer.ScrollableHeight - _feedScrollViewer.VerticalOffset;
        var preloadDistance = Math.Max(900, _feedScrollViewer.ViewportHeight * 1.5);
        if (remainingHeight <= preloadDistance)
        {
            RequestMoreVideos();
        }
    }

    private async Task EnsureWatchHistoryNativeScrollAsync()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (WatchHistoryCollectionView.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement root)
            {
                var scrollViewer =
                    FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(root);
                if (scrollViewer is not null)
                {
                    if (!ReferenceEquals(_watchHistoryScrollViewer, scrollViewer))
                    {
                        DetachWatchHistoryNativeScroll();
                        _watchHistoryScrollViewer = scrollViewer;
                        _watchHistoryScrollViewer.ViewChanged +=
                            OnWatchHistoryScrollViewerViewChanged;
                    }

                    CheckWatchHistoryNativeScrollPosition();
                    return;
                }
            }

            await Task.Delay(100);
        }
    }

    private void OnWatchHistoryScrollViewerViewChanged(
        object? sender,
        Microsoft.UI.Xaml.Controls.ScrollViewerViewChangedEventArgs e) =>
        CheckWatchHistoryNativeScrollPosition();

    private void CheckWatchHistoryNativeScrollPosition()
    {
        if (!_isHomeActive ||
            !_viewModel.IsWatchHistoryOpen ||
            _watchHistoryScrollViewer is null)
        {
            return;
        }

        var remainingHeight =
            _watchHistoryScrollViewer.ScrollableHeight -
            _watchHistoryScrollViewer.VerticalOffset;
        var preloadDistance = Math.Max(
            220,
            _watchHistoryScrollViewer.ViewportHeight * 0.6);
        if (remainingHeight <= preloadDistance)
        {
            RequestMoreWatchHistory();
        }
    }

    private static T? FindDescendant<T>(Microsoft.UI.Xaml.DependencyObject root)
        where T : Microsoft.UI.Xaml.DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
#else
    private void OnFeedCollectionLoaded(object? sender, EventArgs e)
    {
    }
#endif

    private void OnWatchHistoryCollectionLoaded(object? sender, EventArgs e)
    {
#if WINDOWS
        _ = EnsureWatchHistoryNativeScrollAsync();
#endif
    }

    private void OnWatchHistoryCollectionUnloaded(object? sender, EventArgs e)
    {
#if WINDOWS
        DetachWatchHistoryNativeScroll();
#endif
    }

    private void OnFeedCollectionUnloaded(object? sender, EventArgs e)
    {
#if WINDOWS
        DetachFeedNativeHandlers();
#endif
    }

    private void RequestMoreVideos()
    {
        if (_viewModel.LoadMoreCommand.CanExecute(null))
        {
            _viewModel.LoadMoreCommand.Execute(null);
        }
    }

    private async void OnSearchCompleted(object? sender, EventArgs e)
    {
        SearchEntry.Unfocus();
        await SearchAndScrollToTopAsync(SearchEntry.Text ?? string.Empty);
    }

    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.OldTextValue) || !string.IsNullOrEmpty(e.NewTextValue))
        {
            return;
        }

        await SearchAndScrollToTopAsync(string.Empty);
    }

    private async Task SearchAndScrollToTopAsync(string keyword)
    {
        await _viewModel.SearchAsync(keyword);

        if (_viewModel.Videos.Count > 0)
        {
            FeedCollectionView.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
        }
    }

    private async void OnVideoTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not VideoItem video)
        {
            return;
        }

        _viewModel.InvalidateWatchHistory();
        await Navigation.PushModalAsync(new PlayerPage(video, _viewModel.CookieHeader));
    }

    private async void OnAccountPointerEntered(object? sender, PointerEventArgs e)
    {
        CancelWatchHistoryClose();
        if (_viewModel.CanShowWatchHistory)
        {
            await _viewModel.OpenWatchHistoryAsync();
#if WINDOWS
            await EnsureWatchHistoryNativeScrollAsync();
#endif
        }
    }

    private void OnAccountPointerExited(object? sender, PointerEventArgs e) =>
        ScheduleWatchHistoryClose();

    private void OnWatchHistoryPointerEntered(object? sender, PointerEventArgs e) =>
        CancelWatchHistoryClose();

    private void OnWatchHistoryPointerExited(object? sender, PointerEventArgs e) =>
        ScheduleWatchHistoryClose();

    private void OnWatchHistoryScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        if (e.LastVisibleItemIndex >= 0 &&
            e.LastVisibleItemIndex >= _viewModel.WatchHistory.Count - 4)
        {
            RequestMoreWatchHistory();
        }
    }

    private async void OnWatchHistoryItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not WatchHistoryItem item)
        {
            return;
        }

        CancelWatchHistoryClose();
        _viewModel.CloseWatchHistory();
        _viewModel.InvalidateWatchHistory();
        await Navigation.PushModalAsync(new PlayerPage(item.ToVideoItem(), _viewModel.CookieHeader));
    }

    private void RequestMoreWatchHistory()
    {
        if (_viewModel.WatchHistoryLoadMoreCommand.CanExecute(null))
        {
            _viewModel.WatchHistoryLoadMoreCommand.Execute(null);
        }
    }

    private void ScheduleWatchHistoryClose()
    {
        CancelWatchHistoryClose();
        var cancellation = new CancellationTokenSource();
        _watchHistoryCloseCancellation = cancellation;
        _ = CloseWatchHistoryAfterDelayAsync(cancellation);
    }

    private async Task CloseWatchHistoryAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(180, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                _viewModel.CloseWatchHistory();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_watchHistoryCloseCancellation, cancellation))
            {
                _watchHistoryCloseCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelWatchHistoryClose()
    {
        var cancellation = _watchHistoryCloseCancellation;
        _watchHistoryCloseCancellation = null;
        cancellation?.Cancel();
    }

    private async void OnAccountTapped(object? sender, TappedEventArgs e)
    {
        CancelWatchHistoryClose();
        _viewModel.CloseWatchHistory();
        if (_viewModel.IsLoggedIn)
        {
            var shouldLogout = await DisplayAlert(
                "退出登录",
                "将清除此应用 WebView2 中保存的 B 站登录状态。",
                "退出",
                "取消");

            if (!shouldLogout)
            {
                return;
            }

            await WebViewCookieBridge.ClearAllCookiesAsync(SessionWebView);
            _viewModel.ClearProfile();
            await _viewModel.ReloadAsync();
            return;
        }

        var loginPage = new LoginPage(_apiService);
        await Navigation.PushModalAsync(loginPage);
        var profile = await loginPage.Completion;
        if (profile is not null)
        {
            var cookieHeader = await WebViewCookieBridge.GetBilibiliCookieHeaderAsync(SessionWebView);
            _viewModel.SetProfile(profile, cookieHeader);
            await _viewModel.ReloadAsync();
        }
    }
}
