namespace BiliBiliPlayer.Services;

public static class WebViewCookieBridge
{
    public static async Task<string> GetBilibiliCookieHeaderAsync(WebView webView)
    {
#if WINDOWS
        var nativeWebView = await GetNativeWebViewAsync(webView);
        if (nativeWebView?.CoreWebView2 is null)
        {
            return string.Empty;
        }

        var cookies = await nativeWebView.CoreWebView2.CookieManager
            .GetCookiesAsync("https://www.bilibili.com/");
        return string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
#else
        await Task.CompletedTask;
        return string.Empty;
#endif
    }

    public static async Task ClearAllCookiesAsync(WebView webView)
    {
#if WINDOWS
        var nativeWebView = await GetNativeWebViewAsync(webView);
        nativeWebView?.CoreWebView2?.CookieManager.DeleteAllCookies();
#else
        await Task.CompletedTask;
#endif
    }

#if WINDOWS
    private static async Task<Microsoft.UI.Xaml.Controls.WebView2?> GetNativeWebViewAsync(WebView webView)
    {
        for (var attempt = 0; attempt < 20 && webView.Handler?.PlatformView is null; attempt++)
        {
            await Task.Delay(50);
        }

        if (webView.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 nativeWebView)
        {
            return null;
        }

        await nativeWebView.EnsureCoreWebView2Async();
        return nativeWebView;
    }
#endif
}
