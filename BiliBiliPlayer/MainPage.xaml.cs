using BiliBiliPlayer.Models;
using BiliBiliPlayer.Services;
using BiliBiliPlayer.ViewModels;
using BiliBiliPlayer.Views;

namespace BiliBiliPlayer;

public partial class MainPage : ContentPage
{
    private readonly BiliApiService _apiService = new();
    private readonly MainViewModel _viewModel;
    private bool _initialized;
    private bool _isHomeActive;
#if WINDOWS
    private Microsoft.UI.Input.InputKeyboardSource? _keyboardSource;
    private Microsoft.UI.Xaml.Controls.ScrollViewer? _feedScrollViewer;
#endif

    public MainPage()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(_apiService);
        BindingContext = _viewModel;
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

        for (var attempt = 0; attempt < 6 && _feedScrollViewer is null; attempt++)
        {
            if (FeedCollectionView.Handler?.PlatformView is Microsoft.UI.Xaml.DependencyObject root)
            {
                _feedScrollViewer = FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(root);
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

        await Navigation.PushModalAsync(new PlayerPage(video));
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
