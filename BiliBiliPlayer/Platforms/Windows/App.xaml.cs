using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace BiliBiliPlayer.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            var webViewDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BiliBiliPlayer",
                "WebView2");
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webViewDataFolder);

            const string autoplayArgument = "--autoplay-policy=no-user-gesture-required";
            var browserArguments = Environment.GetEnvironmentVariable(
                "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
            if (string.IsNullOrWhiteSpace(browserArguments))
            {
                Environment.SetEnvironmentVariable(
                    "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                    autoplayArgument);
            }
            else if (!browserArguments.Contains(autoplayArgument, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(
                    "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                    $"{browserArguments} {autoplayArgument}");
            }

            this.InitializeComponent();
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    }

}
