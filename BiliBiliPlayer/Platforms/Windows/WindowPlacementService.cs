using Microsoft.Maui.Storage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace BiliBiliPlayer;

internal sealed class WindowPlacementService
{
    private const string HasPlacementKey = "window.placement.saved";
    private const string XKey = "window.placement.x";
    private const string YKey = "window.placement.y";
    private const string WidthKey = "window.placement.width";
    private const string HeightKey = "window.placement.height";
    private const string MaximizedKey = "window.placement.maximized";
    private const int DefaultWidth = 1180;
    private const int DefaultHeight = 780;
    private const int MinimumWidth = 820;
    private const int MinimumHeight = 600;

    private readonly AppWindow _appWindow;
    private readonly DispatcherQueueTimer _saveTimer;
    private RectInt32 _restoredBounds;
    private bool _isMaximized;

    public WindowPlacementService(AppWindow appWindow)
    {
        _appWindow = appWindow;
        _restoredBounds = CurrentBounds();
        _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();
        _appWindow.Changed += OnAppWindowChanged;
    }

    public void Restore()
    {
        if (!Preferences.Default.Get(HasPlacementKey, false))
        {
            return;
        }

        var savedBounds = new RectInt32(
            Preferences.Default.Get(XKey, _appWindow.Position.X),
            Preferences.Default.Get(YKey, _appWindow.Position.Y),
            Preferences.Default.Get(WidthKey, DefaultWidth),
            Preferences.Default.Get(HeightKey, DefaultHeight));
        _restoredBounds = FitToVisibleWorkArea(savedBounds);
        _appWindow.MoveAndResize(_restoredBounds);

        _isMaximized = Preferences.Default.Get(MaximizedKey, false);
        if (_isMaximized && _appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    public void Save()
    {
        _saveTimer.Stop();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Restored)
            {
                _restoredBounds = CurrentBounds();
                _isMaximized = false;
            }
            else if (presenter.State == OverlappedPresenterState.Maximized)
            {
                _isMaximized = true;
            }
        }

        Preferences.Default.Set(XKey, _restoredBounds.X);
        Preferences.Default.Set(YKey, _restoredBounds.Y);
        Preferences.Default.Set(WidthKey, _restoredBounds.Width);
        Preferences.Default.Set(HeightKey, _restoredBounds.Height);
        Preferences.Default.Set(MaximizedKey, _isMaximized);
        Preferences.Default.Set(HasPlacementKey, true);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        if (presenter.State == OverlappedPresenterState.Restored &&
            (args.DidPositionChange || args.DidSizeChange))
        {
            _restoredBounds = CurrentBounds();
        }

        if (args.DidPresenterChange)
        {
            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                _isMaximized = true;
            }
            else if (presenter.State == OverlappedPresenterState.Restored)
            {
                _isMaximized = false;
            }
        }

        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    private RectInt32 CurrentBounds() => new(
        _appWindow.Position.X,
        _appWindow.Position.Y,
        _appWindow.Size.Width,
        _appWindow.Size.Height);

    private static RectInt32 FitToVisibleWorkArea(RectInt32 bounds)
    {
        var displayArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var minimumWidth = Math.Min(MinimumWidth, workArea.Width);
        var minimumHeight = Math.Min(MinimumHeight, workArea.Height);
        var width = Math.Clamp(bounds.Width, minimumWidth, workArea.Width);
        var height = Math.Clamp(bounds.Height, minimumHeight, workArea.Height);
        var x = Math.Clamp(bounds.X, workArea.X, workArea.X + workArea.Width - width);
        var y = Math.Clamp(bounds.Y, workArea.Y, workArea.Y + workArea.Height - height);

        return new RectInt32(x, y, width, height);
    }
}
