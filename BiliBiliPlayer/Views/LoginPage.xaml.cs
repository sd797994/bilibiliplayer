using BiliBiliPlayer.Models;
using BiliBiliPlayer.Services;

namespace BiliBiliPlayer.Views;

public partial class LoginPage : ContentPage
{
    private readonly BiliApiService _apiService;
    private readonly TaskCompletionSource<UserProfile?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isClosing;

    public LoginPage(BiliApiService apiService)
    {
        InitializeComponent();
        _apiService = apiService;
    }

    public Task<UserProfile?> Completion => _completion.Task;

    private void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        WebLoadingOverlay.IsVisible = false;
        if (e.Result != WebNavigationResult.Success)
        {
            StatusLabel.Text = "登录页加载失败，请检查网络后返回重试";
            StatusLabel.TextColor = Color.FromArgb("#FF8A8A");
        }
    }

    private async void OnCompleteClicked(object? sender, EventArgs e)
    {
        CompleteButton.IsEnabled = false;
        CompleteButton.Text = "正在确认…";
        StatusLabel.Text = "正在读取 B 站登录状态";
        StatusLabel.TextColor = Color.FromArgb("#A7ABB5");

        try
        {
            var cookieHeader = await WebViewCookieBridge.GetBilibiliCookieHeaderAsync(LoginWebView);
            var profile = await _apiService.GetCurrentUserAsync(cookieHeader);
            if (profile is null)
            {
                StatusLabel.Text = "暂未检测到登录成功，请在网页中完成登录后再试";
                StatusLabel.TextColor = Color.FromArgb("#FF8A8A");
                return;
            }

            BiliSessionStore.SaveProfile(profile);
            await CloseAsync(profile);
        }
        catch (Exception)
        {
            StatusLabel.Text = "验证登录状态失败，请检查网络后再试";
            StatusLabel.TextColor = Color.FromArgb("#FF8A8A");
        }
        finally
        {
            CompleteButton.IsEnabled = true;
            CompleteButton.Text = "我已登录";
        }
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_isClosing)
        {
            _completion.TrySetResult(null);
        }
    }

    private async Task CloseAsync(UserProfile? profile)
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _completion.TrySetResult(profile);
        await Navigation.PopModalAsync();
    }
}
