using BiliBiliPlayer.Models;
using BiliBiliPlayer.Services;
using BiliBiliPlayer.ViewModels;
using BiliBiliPlayer.Views;

namespace BiliBiliPlayer;

public partial class MainPage : ContentPage
{
    private const double PullRefreshThreshold = 82;
    private const double PullRefreshMaxDistance = 116;
    private const double PullRefreshRestingDistance = 64;
    private readonly BiliApiService _apiService = new();
    private readonly MainViewModel _viewModel;
    private bool _initialized;
    private bool _isHomeActive;
#if WINDOWS
    private Microsoft.UI.Input.InputKeyboardSource? _keyboardSource;
    private Microsoft.UI.Xaml.Controls.ScrollViewer? _feedScrollViewer;
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

#if WINDOWS
        AttachWindowKeyboardSource();
#endif

        if (_initialized)
        {
            return;
        }

        _initialized = true;
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
        base.OnDisappearing();
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

#if WINDOWS
        if (Handler is null)
        {
            DetachWindowKeyboardSource();
            DetachFeedNativeHandlers();
        }
        else
        {
            AttachWindowKeyboardSource();
        }
#endif
    }

#if WINDOWS
    private void OnWindowKeyDown(
        Microsoft.UI.Input.InputKeyboardSource sender,
        Microsoft.UI.Input.KeyEventArgs args)
    {
        if (args.VirtualKey != Windows.System.VirtualKey.F5 ||
            !_isHomeActive ||
            Navigation.ModalStack.Count != 0)
        {
            return;
        }

        args.Handled = true;
        Dispatcher.Dispatch(async () => await _viewModel.ReloadAsync());
    }

    private void AttachWindowKeyboardSource()
    {
        if (_keyboardSource is not null ||
            Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement platformView ||
            platformView.XamlRoot?.ContentIsland is not { } contentIsland)
        {
            return;
        }

        _keyboardSource = Microsoft.UI.Input.InputKeyboardSource.GetForIsland(contentIsland);
        if (_keyboardSource is not null)
        {
            _keyboardSource.KeyDown += OnWindowKeyDown;
        }
    }

    private void DetachWindowKeyboardSource()
    {
        if (_keyboardSource is null)
        {
            return;
        }

        _keyboardSource.KeyDown -= OnWindowKeyDown;
        _keyboardSource = null;
    }
#endif

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
        AttachWindowKeyboardSource();

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

    private void OnFeedPointerPressed(
        object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isHomeActive ||
            _isPullRefreshRunning ||
            _viewModel.IsRefreshing ||
            _feedPointerSurface is null ||
            _feedScrollViewer is null ||
            _feedScrollViewer.VerticalOffset > 0.5 ||
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

        var shouldRefresh =
            _isPullDragging && FeedCollectionView.TranslationY >= PullRefreshThreshold;
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
        var progress = Math.Clamp(pullDistance / PullRefreshThreshold, 0, 1);
        FeedCollectionView.TranslationY = pullDistance;
        PullRefreshIndicator.Opacity = Math.Clamp((pullDistance - 8) / 48, 0, 1);
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
            await FeedCollectionView.TranslateTo(
                0,
                PullRefreshRestingDistance,
                140,
                Easing.CubicOut);

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
            FeedCollectionView.TranslateTo(0, 0, 220, Easing.CubicOut),
            PullRefreshIndicator.FadeTo(0, 180, Easing.CubicIn),
            PullRefreshIndicator.ScaleTo(0.72, 180, Easing.CubicIn));
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
        await _viewModel.SearchAsync(SearchEntry.Text ?? string.Empty);

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

        await Navigation.PushModalAsync(new PlayerPage(video, _viewModel.CookieHeader));
    }

    private async void OnAccountTapped(object? sender, TappedEventArgs e)
    {
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
