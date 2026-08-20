namespace BiliBiliPlayer
{
    public partial class App : Application
    {
#if WINDOWS
        private WindowPlacementService? _windowPlacementService;
#endif

        public App()
        {
            InitializeComponent();
            UserAppTheme = AppTheme.Dark;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell())
            {
                Title = "哔哩桌面",
                Width = 1180,
                Height = 780,
                MinimumWidth = 820,
                MinimumHeight = 600
            };

#if WINDOWS
            window.TitleBar = new TitleBar
            {
                Title = "哔哩桌面",
                BackgroundColor = Color.FromArgb("#0F1014"),
                ForegroundColor = Color.FromArgb("#F3F4F7"),
                HeightRequest = 36
            };

            window.Created += (_, _) =>
            {
                if (window.Handler?.PlatformView is not Microsoft.Maui.MauiWinUIWindow nativeWindow)
                {
                    return;
                }

                var titleBar = nativeWindow.AppWindow.TitleBar;
                titleBar.BackgroundColor = global::Windows.UI.Color.FromArgb(255, 20, 22, 28);
                titleBar.ForegroundColor = global::Windows.UI.Color.FromArgb(255, 235, 237, 242);
                titleBar.ButtonBackgroundColor = global::Windows.UI.Color.FromArgb(255, 20, 22, 28);
                titleBar.ButtonForegroundColor = global::Windows.UI.Color.FromArgb(255, 235, 237, 242);
                titleBar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(255, 43, 46, 56);
                titleBar.ButtonHoverForegroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255);
                titleBar.ButtonInactiveBackgroundColor = global::Windows.UI.Color.FromArgb(255, 15, 16, 20);
                titleBar.ButtonInactiveForegroundColor = global::Windows.UI.Color.FromArgb(255, 143, 148, 161);

                _windowPlacementService = new WindowPlacementService(nativeWindow.AppWindow);
                _windowPlacementService.Restore();
            };

            window.Destroying += (_, _) => _windowPlacementService?.Save();
#endif

            return window;
        }
    }
}
