using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BiliBiliPlayer.Models;
using BiliBiliPlayer.Services;

namespace BiliBiliPlayer.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int RecommendationBatchSize = 18;
    private const int WatchHistoryInitialTargetCount = 8;
    private const int WatchHistoryInitialMaxPages = 4;
    private readonly BiliApiService _apiService;
    private readonly BiliWatchHistoryService _watchHistoryService;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _watchHistoryLoadGate = new(1, 1);
    private int _page = 1;
    private int _recommendationFreshIndex;
    private int _recommendationFreshIndexInHour;
    private int _popularPage;
    private DateTimeOffset _recommendationHourStartedAt = DateTimeOffset.UtcNow;
    private bool _hasMore = true;
    private bool _isBusy;
    private bool _isRefreshing;
    private bool _isLoadingMore;
    private bool _showErrorState;
    private string _errorMessage = string.Empty;
    private string _feedTitle = "热门推荐";
    private string _feedBadgeText = "每日更新";
    private string _feedCaption = "来自哔哩哔哩的热门内容";
    private string _cookieHeader = string.Empty;
    private string _searchKeyword = string.Empty;
    private bool _isSearchMode;
    private UserProfile? _profile;
    private WatchHistoryCursor? _watchHistoryCursor;
    private bool _watchHistoryHasMore = true;
    private bool _watchHistoryInitialized;
    private bool _isWatchHistoryOpen;
    private bool _isWatchHistoryBusy;
    private bool _isWatchHistoryLoadingMore;
    private string _watchHistoryErrorMessage = string.Empty;
    private int _watchHistoryGeneration;

    public MainViewModel(
        BiliApiService apiService,
        BiliWatchHistoryService? watchHistoryService = null)
    {
        _apiService = apiService;
        _watchHistoryService = watchHistoryService ?? new BiliWatchHistoryService();
        _profile = BiliSessionStore.LoadProfile();
        RefreshCommand = new Command(async () => await RefreshAsync());
        LoadMoreCommand = new Command(async () => await LoadMoreAsync());
        RetryCommand = new Command(async () => await RefreshAsync());
        WatchHistoryLoadMoreCommand = new Command(async () => await LoadMoreWatchHistoryAsync());
        WatchHistoryRetryCommand = new Command(async () => await ReloadWatchHistoryAsync());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<VideoItem> Videos { get; } = [];

    public ObservableCollection<WatchHistoryItem> WatchHistory { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand LoadMoreCommand { get; }

    public ICommand RetryCommand { get; }

    public ICommand WatchHistoryLoadMoreCommand { get; }

    public ICommand WatchHistoryRetryCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetProperty(ref _isRefreshing, value);
    }

    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        private set => SetProperty(ref _isLoadingMore, value);
    }

    public bool ShowErrorState
    {
        get => _showErrorState;
        private set => SetProperty(ref _showErrorState, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string FeedCaption
    {
        get => _feedCaption;
        private set => SetProperty(ref _feedCaption, value);
    }

    public string FeedTitle
    {
        get => _feedTitle;
        private set => SetProperty(ref _feedTitle, value);
    }

    public string FeedBadgeText
    {
        get => _feedBadgeText;
        private set => SetProperty(ref _feedBadgeText, value);
    }

    public bool IsLoggedIn => _profile is not null;

    public bool CanShowWatchHistory =>
        _profile is not null && !string.IsNullOrWhiteSpace(_cookieHeader);

    public bool IsWatchHistoryOpen
    {
        get => _isWatchHistoryOpen;
        private set => SetProperty(ref _isWatchHistoryOpen, value);
    }

    public bool IsWatchHistoryBusy
    {
        get => _isWatchHistoryBusy;
        private set
        {
            if (SetProperty(ref _isWatchHistoryBusy, value))
            {
                RaiseWatchHistoryStateProperties();
            }
        }
    }

    public bool IsWatchHistoryLoadingMore
    {
        get => _isWatchHistoryLoadingMore;
        private set
        {
            if (SetProperty(ref _isWatchHistoryLoadingMore, value))
            {
                RaiseWatchHistoryStateProperties();
            }
        }
    }

    public string WatchHistoryErrorMessage
    {
        get => _watchHistoryErrorMessage;
        private set
        {
            if (SetProperty(ref _watchHistoryErrorMessage, value))
            {
                RaiseWatchHistoryStateProperties();
            }
        }
    }

    public bool ShowWatchHistoryContent => WatchHistory.Count > 0;

    public bool ShowWatchHistoryEmpty =>
        !IsWatchHistoryBusy && WatchHistory.Count == 0 &&
        string.IsNullOrWhiteSpace(WatchHistoryErrorMessage);

    public bool ShowWatchHistoryError =>
        !IsWatchHistoryBusy && WatchHistory.Count == 0 &&
        !string.IsNullOrWhiteSpace(WatchHistoryErrorMessage);

    public bool ShowWatchHistoryFooter =>
        WatchHistory.Count > 0 && !IsWatchHistoryBusy;

    public string WatchHistoryFooterText => IsWatchHistoryLoadingMore
        ? "正在加载更多…"
        : !string.IsNullOrWhiteSpace(WatchHistoryErrorMessage)
            ? "加载更多失败，继续滚动可重试"
            : _watchHistoryHasMore
                ? "继续向下滚动加载更多"
                : "已经到底了";

    public string CookieHeader => _cookieHeader;

    public bool HasAvatar => !string.IsNullOrWhiteSpace(_profile?.AvatarUrl);

    public bool ShowAvatarFallback => !HasAvatar;

    public string AvatarUrl => _profile?.AvatarUrl ?? string.Empty;

    public string AccountInitial => _profile?.Initial ?? "登";

    public string AccountTitle => _profile?.UserName ?? "登录 B 站";

    public string AccountSubtitle => _profile is null
        ? "同步网页登录状态"
        : $"LV{_profile.Level} · 登录状态已保存";

    public string AccountActionText => _profile is null ? "登录" : "退出";

    public async Task InitializeAsync()
    {
        if (Videos.Count > 0 || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ConfigureSession(UserProfile? profile, string cookieHeader)
    {
        _profile = profile;
        _cookieHeader = cookieHeader;
        ResetRecommendationCursors();
        ResetWatchHistoryState();

        if (profile is null)
        {
            BiliSessionStore.ClearProfile();
        }
        else
        {
            BiliSessionStore.SaveProfile(profile);
        }

        UpdateFeedCaption();
        RaiseAccountProperties();
    }

    public void SetProfile(UserProfile profile, string cookieHeader) =>
        ConfigureSession(profile, cookieHeader);

    public Task ReloadAsync() => RefreshAsync();

    public async Task SearchAsync(string keyword)
    {
        IsBusy = true;
        await _loadGate.WaitAsync();
        try
        {
            _searchKeyword = keyword.Trim();
            _isSearchMode = !string.IsNullOrWhiteSpace(_searchKeyword);
            Videos.Clear();
            ShowErrorState = false;
            UpdateFeedCaption();
            await RefreshCoreAsync(lockAlreadyHeld: true);
        }
        finally
        {
            IsBusy = false;
            _loadGate.Release();
        }
    }

    public void ClearProfile()
    {
        _profile = null;
        _cookieHeader = string.Empty;
        ResetRecommendationCursors();
        ResetWatchHistoryState();
        BiliSessionStore.ClearProfile();
        UpdateFeedCaption();
        RaiseAccountProperties();
    }

    public async Task OpenWatchHistoryAsync()
    {
        if (!CanShowWatchHistory)
        {
            return;
        }

        IsWatchHistoryOpen = true;
        if (!_watchHistoryInitialized && !IsWatchHistoryBusy)
        {
            await ReloadWatchHistoryAsync();
        }
    }

    public void CloseWatchHistory() => IsWatchHistoryOpen = false;

    public void InvalidateWatchHistory()
    {
        _watchHistoryGeneration = NextPositiveCounter(_watchHistoryGeneration);
        _watchHistoryCursor = null;
        _watchHistoryHasMore = true;
        _watchHistoryInitialized = false;
        WatchHistoryErrorMessage = string.Empty;
        RaiseWatchHistoryStateProperties();
    }

    private async Task ReloadWatchHistoryAsync()
    {
        if (!CanShowWatchHistory || !await _watchHistoryLoadGate.WaitAsync(0))
        {
            return;
        }

        var generation = _watchHistoryGeneration;
        var cookieHeader = _cookieHeader;
        IsWatchHistoryBusy = true;
        WatchHistoryErrorMessage = string.Empty;
        _watchHistoryCursor = null;
        _watchHistoryHasMore = true;
        WatchHistory.Clear();
        RaiseWatchHistoryStateProperties();
        try
        {
            var existing = new HashSet<(string Bvid, long Cid, long ViewedAt)>();
            for (var attempt = 0;
                 attempt < WatchHistoryInitialMaxPages &&
                 _watchHistoryHasMore &&
                 WatchHistory.Count < WatchHistoryInitialTargetCount;
                 attempt++)
            {
                var page = await _watchHistoryService.GetPageAsync(
                    cookieHeader,
                    _watchHistoryCursor);
                if (generation != _watchHistoryGeneration || cookieHeader != _cookieHeader)
                {
                    return;
                }

                foreach (var item in page.Items)
                {
                    if (existing.Add((item.Bvid, item.Cid, item.ViewedAt)))
                    {
                        WatchHistory.Add(item);
                    }
                }

                _watchHistoryCursor = page.NextCursor;
                _watchHistoryHasMore = page.HasMore;
            }

            _watchHistoryInitialized = true;
        }
        catch (BiliWatchHistoryException exception)
        {
            WatchHistoryErrorMessage = exception.Code is -101 or -111
                ? "登录状态已失效，请重新登录"
                : $"历史记录暂时无法读取：{exception.Message}";
        }
        catch (TaskCanceledException)
        {
            WatchHistoryErrorMessage = "读取历史记录超时，请稍后重试";
        }
        catch (HttpRequestException)
        {
            WatchHistoryErrorMessage = "无法连接到哔哩哔哩，请检查网络";
        }
        catch
        {
            WatchHistoryErrorMessage = "历史记录暂时无法读取，请稍后重试";
        }
        finally
        {
            IsWatchHistoryBusy = false;
            RaiseWatchHistoryStateProperties();
            _watchHistoryLoadGate.Release();
        }
    }

    private async Task LoadMoreWatchHistoryAsync()
    {
        if (!CanShowWatchHistory || !_watchHistoryInitialized || !_watchHistoryHasMore ||
            WatchHistory.Count == 0 || !await _watchHistoryLoadGate.WaitAsync(0))
        {
            return;
        }

        var generation = _watchHistoryGeneration;
        var cookieHeader = _cookieHeader;
        var cursor = _watchHistoryCursor;
        IsWatchHistoryLoadingMore = true;
        WatchHistoryErrorMessage = string.Empty;
        try
        {
            var page = await _watchHistoryService.GetPageAsync(cookieHeader, cursor);
            if (generation != _watchHistoryGeneration || cookieHeader != _cookieHeader)
            {
                return;
            }

            var existing = WatchHistory
                .Select(item => (item.Bvid, item.Cid, item.ViewedAt))
                .ToHashSet();
            foreach (var item in page.Items)
            {
                if (existing.Add((item.Bvid, item.Cid, item.ViewedAt)))
                {
                    WatchHistory.Add(item);
                }
            }

            _watchHistoryCursor = page.NextCursor;
            _watchHistoryHasMore = page.HasMore;
        }
        catch
        {
            WatchHistoryErrorMessage = "加载更多失败";
        }
        finally
        {
            IsWatchHistoryLoadingMore = false;
            RaiseWatchHistoryStateProperties();
            _watchHistoryLoadGate.Release();
        }
    }

    private async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        await _loadGate.WaitAsync();
        try
        {
            await RefreshCoreAsync(lockAlreadyHeld: true);
        }
        finally
        {
            IsRefreshing = false;
            _loadGate.Release();
        }
    }

    private async Task RefreshCoreAsync(bool lockAlreadyHeld = false)
    {
        if (!lockAlreadyHeld)
        {
            await _loadGate.WaitAsync();
        }

        try
        {
            ShowErrorState = false;
            ErrorMessage = string.Empty;
            PopularPage result;
            if (_isSearchMode)
            {
                result = await _apiService.SearchVideosAsync(_searchKeyword, 1, _cookieHeader);
                UpdateFeedCaption();
            }
            else
            {
                var previousIds = Videos
                    .Select(video => video.Bvid)
                    .Where(bvid => !string.IsNullOrWhiteSpace(bvid))
                    .ToHashSet(StringComparer.Ordinal);
                result = await GetFreshFeedAsync(previousIds);
            }
            Videos.Clear();
            foreach (var video in result.Items.Where(IsPlayableVideo))
            {
                Videos.Add(video);
            }

            _page = 1;
            _hasMore = !result.NoMore;
            ShowErrorState = Videos.Count == 0;
            if (ShowErrorState)
            {
                ErrorMessage = "暂时没有获取到可播放内容，请稍后重试。";
            }
        }
        catch (Exception exception)
        {
            if (Videos.Count == 0)
            {
                ErrorMessage = GetFriendlyError(exception);
                ShowErrorState = true;
            }
        }
        finally
        {
            if (!lockAlreadyHeld)
            {
                _loadGate.Release();
            }
        }
    }

    private async Task LoadMoreAsync()
    {
        if (!_hasMore || Videos.Count == 0 || !await _loadGate.WaitAsync(0))
        {
            return;
        }

        IsLoadingMore = true;
        try
        {
            var existingIds = Videos.Select(video => video.Bvid).ToHashSet(StringComparer.Ordinal);
            var addedCount = 0;
            const int targetBatchSize = 36;

            // Recommendation batches can occasionally contain only items already shown.
            // Keep a larger buffer because the WinUI virtualized grid can skip threshold events.
            for (var attempt = 0;
                 attempt < 6 && _hasMore && addedCount < targetBatchSize;
                 attempt++)
            {
                var nextPage = _page + 1;
                var result = _isSearchMode
                    ? await _apiService.SearchVideosAsync(_searchKeyword, nextPage, _cookieHeader)
                    : await GetFeedBatchAsync();
                _page = nextPage;
                _hasMore = !result.NoMore;

                foreach (var video in result.Items.Where(IsPlayableVideo))
                {
                    if (existingIds.Add(video.Bvid))
                    {
                        Videos.Add(video);
                        addedCount++;
                    }
                }
            }
        }
        catch
        {
            // Keep the current feed intact. The next threshold event can retry the page.
        }
        finally
        {
            IsLoadingMore = false;
            _loadGate.Release();
        }
    }

    private static bool IsPlayableVideo(VideoItem video) =>
        !string.IsNullOrWhiteSpace(video.Bvid) && !string.IsNullOrWhiteSpace(video.Picture);

    private bool HasPersonalizedSession =>
        _profile is not null && !string.IsNullOrWhiteSpace(_cookieHeader);

    private async Task<PopularPage> GetFreshFeedAsync(IReadOnlySet<string> previousIds)
    {
        var items = new List<VideoItem>(RecommendationBatchSize);
        var acceptedIds = new HashSet<string>(StringComparer.Ordinal);
        var noMore = false;

        // A recommendation refresh can legitimately contain a little overlap. Advance through
        // additional recommendation contexts until the visible batch is actually new.
        for (var attempt = 0;
             attempt < 3 && items.Count < RecommendationBatchSize && !noMore;
             attempt++)
        {
            var batch = await GetFeedBatchAsync();
            noMore = batch.NoMore;
            foreach (var video in batch.Items.Where(IsPlayableVideo))
            {
                if (!previousIds.Contains(video.Bvid) && acceptedIds.Add(video.Bvid))
                {
                    items.Add(video);
                    if (items.Count >= RecommendationBatchSize)
                    {
                        break;
                    }
                }
            }
        }

        if (items.Count == 0)
        {
            throw new BiliApiException("暂时没有获取到新的推荐内容。", -1);
        }

        return new PopularPage
        {
            Items = items,
            NoMore = noMore
        };
    }

    private async Task<PopularPage> GetFeedBatchAsync()
    {
        var (freshIndex, freshIndexInHour) = NextRecommendationCursor();

        if (HasPersonalizedSession && _profile is not null)
        {
            try
            {
                var personalized = await _apiService.GetRecommendedAsync(
                    _cookieHeader,
                    freshIndex,
                    freshIndexInHour);
                if (personalized.Mid == _profile.Mid && personalized.Mid != 0)
                {
                    FeedCaption = $"你好，{_profile.UserName} · 已启用登录账号的个性化推荐";
                    return ToPopularPage(personalized);
                }

                // An expired login can still receive a valid anonymous dynamic feed.
                if (personalized.Mid == 0 && personalized.Items.Count > 0)
                {
                    FeedCaption = $"你好，{_profile.UserName} · 登录推荐暂不可用，已切换动态推荐";
                    return ToPopularPage(personalized);
                }
            }
            catch
            {
                // Retry the same recommendation context without account cookies below.
            }
        }

        try
        {
            var dynamicFeed = await _apiService.GetRecommendedAsync(
                string.Empty,
                freshIndex,
                freshIndexInHour);
            if (dynamicFeed.Items.Count > 0)
            {
                FeedCaption = _profile is null
                    ? "来自哔哩哔哩的动态推荐"
                    : $"你好，{_profile.UserName} · 当前展示动态推荐";
                return ToPopularPage(dynamicFeed);
            }
        }
        catch
        {
            // The stable popular feed remains the final availability fallback.
        }

        var popular = await _apiService.GetPopularAsync(NextPopularPage());
        FeedCaption = _profile is null
            ? "动态推荐暂不可用，已切换站内热门内容"
            : $"你好，{_profile.UserName} · 动态推荐暂不可用，已切换站内热门内容";
        return popular;
    }

    private (int FreshIndex, int FreshIndexInHour) NextRecommendationCursor()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _recommendationHourStartedAt >= TimeSpan.FromHours(1))
        {
            _recommendationHourStartedAt = now;
            _recommendationFreshIndexInHour = 0;
        }

        _recommendationFreshIndex = NextPositiveCounter(_recommendationFreshIndex);
        _recommendationFreshIndexInHour = NextPositiveCounter(_recommendationFreshIndexInHour);
        return (_recommendationFreshIndex, _recommendationFreshIndexInHour);
    }

    private int NextPopularPage()
    {
        _popularPage = NextPositiveCounter(_popularPage);
        return _popularPage;
    }

    private void ResetRecommendationCursors()
    {
        _recommendationFreshIndex = 0;
        _recommendationFreshIndexInHour = 0;
        _popularPage = 0;
        _recommendationHourStartedAt = DateTimeOffset.UtcNow;
    }

    private void ResetWatchHistoryState()
    {
        _watchHistoryGeneration = NextPositiveCounter(_watchHistoryGeneration);
        _watchHistoryCursor = null;
        _watchHistoryHasMore = true;
        _watchHistoryInitialized = false;
        IsWatchHistoryOpen = false;
        IsWatchHistoryBusy = false;
        IsWatchHistoryLoadingMore = false;
        WatchHistoryErrorMessage = string.Empty;
        WatchHistory.Clear();
        RaiseWatchHistoryStateProperties();
    }

    private static int NextPositiveCounter(int current) =>
        current == int.MaxValue ? 1 : current + 1;

    private static PopularPage ToPopularPage(RecommendedPage recommended) => new()
    {
        Items = recommended.Items,
        NoMore = false
    };

    private void UpdateFeedCaption()
    {
        if (_isSearchMode)
        {
            FeedTitle = "搜索结果";
            FeedBadgeText = "站内搜索";
            FeedCaption = $"正在展示“{_searchKeyword}”相关的 B 站视频";
            return;
        }

        FeedTitle = "热门推荐";
        FeedBadgeText = "每日更新";
        FeedCaption = _profile is null
            ? "来自哔哩哔哩的热门内容"
            : HasPersonalizedSession
                ? $"你好，{_profile.UserName} · 正在同步个性化推荐"
                : $"你好，{_profile.UserName} · 当前展示站内热门内容";
    }

    private static string GetFriendlyError(Exception exception) => exception switch
    {
        TaskCanceledException => "连接超时，请检查网络后重试。",
        HttpRequestException => "无法连接到哔哩哔哩，请检查网络后重试。",
        BiliApiException apiException => $"站内接口暂不可用：{apiException.Message}",
        _ => "加载失败，请稍后重试。"
    };

    private void RaiseAccountProperties()
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(CanShowWatchHistory));
        OnPropertyChanged(nameof(HasAvatar));
        OnPropertyChanged(nameof(ShowAvatarFallback));
        OnPropertyChanged(nameof(AvatarUrl));
        OnPropertyChanged(nameof(AccountInitial));
        OnPropertyChanged(nameof(AccountTitle));
        OnPropertyChanged(nameof(AccountSubtitle));
        OnPropertyChanged(nameof(AccountActionText));
    }

    private void RaiseWatchHistoryStateProperties()
    {
        OnPropertyChanged(nameof(ShowWatchHistoryContent));
        OnPropertyChanged(nameof(ShowWatchHistoryEmpty));
        OnPropertyChanged(nameof(ShowWatchHistoryError));
        OnPropertyChanged(nameof(ShowWatchHistoryFooter));
        OnPropertyChanged(nameof(WatchHistoryFooterText));
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
