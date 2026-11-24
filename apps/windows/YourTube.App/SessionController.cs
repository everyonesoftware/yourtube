using System.Security.Cryptography;
using System.Text;
using YourTube.Core;

namespace YourTube.App;

internal interface ITokenStore
{
    string? Load();
    void Save(string refreshToken);
    string? LoadAccountSelection();
    void SaveAccountSelection(string selection);
    void Clear();
}

internal interface IYouTubeApi
{
    Task<DeviceAuthorization> BeginSignInAsync(string deviceId, CancellationToken cancellationToken);
    Task<YouTubeSession> CompleteSignInAsync(DeviceAuthorization authorization, CancellationToken cancellationToken);
    Task<YouTubeSession> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<YouTubeAccount>> GetAccountsAsync(YouTubeSession session, CancellationToken cancellationToken);
    Task<HistoryPage> GetHistoryAsync(YouTubeSession session, YouTubeAccount account, string? continuation, CancellationToken cancellationToken);
    Task<SubscriptionsPage> GetSubscriptionsAsync(YouTubeSession session, YouTubeAccount account, string? continuation, CancellationToken cancellationToken);
    Task<SubscriptionsPage> GetSubscriptionChannelAsync(YouTubeSession session, YouTubeAccount account, SubscribedChannel channel, string? continuation, CancellationToken cancellationToken);
    Task<HomePage> GetHomeAsync(YouTubeSession session, YouTubeAccount account, string? continuation, CancellationToken cancellationToken);
    Task<YouTubeClient.WatchRecord> RecordWatchStartAsync(YouTubeSession session, YouTubeAccount account, string videoId, TimeSpan duration, TimeSpan position, CancellationToken cancellationToken);
    Task RecordWatchProgressAsync(YouTubeSession session, YouTubeAccount account, YouTubeClient.WatchRecord record, TimeSpan start, TimeSpan end, CancellationToken cancellationToken);
    Task<WatchLookupComparison> CompareWatchLookupsAsync(YouTubeSession session, YouTubeAccount account, string videoId, CancellationToken cancellationToken);
}

internal enum SessionPhase { Restoring, SignedOut, Authorizing, SignedIn }

internal sealed class SessionController(IYouTubeApi api, ITokenStore store, string deviceId)
{
    public SessionPhase Phase { get; private set; } = SessionPhase.Restoring;
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "Restoring your session…";
    public DeviceAuthorization? Authorization { get; private set; }
    public IReadOnlyList<YouTubeAccount> Accounts { get; private set; } = [];
    public YouTubeAccount? SelectedAccount { get; private set; }
    public IReadOnlyList<HistoryVideo> Videos { get; private set; } = [];
    public IReadOnlyList<SubscribedChannel> Channels { get; private set; } = [];
    public SubscribedChannel? SelectedSubscriptionChannel { get; private set; }
    public IReadOnlyList<HomeShelf> HomeShelves { get; private set; } = [];
    public bool Appending { get; private set; }
    public bool AppendFailed { get; private set; }
    public BrowseView View { get; private set; } = BrowseView.Home;
    public bool CanPlayFeed => Phase == SessionPhase.SignedIn && View != BrowseView.Settings && (!Busy || Appending);
    public async Task NavigateAsync(BrowseView view)
    {
        if (Phase != SessionPhase.SignedIn || View == view) return;
        var op = epoch.Begin();
        epoch.Commit(op.Id, () => { View = view; ClearFeed(); Busy = false; Status = view == BrowseView.Settings ? "Settings" : "Choose an account to load this view."; Changed?.Invoke(); });
        if (view != BrowseView.Settings && SelectedAccount is not null)
            await FetchHistoryAsync(SelectedAccount, append: false, switching: false);
    }
    public string? Continuation { get; private set; }
    public event Action? Changed;
    public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
    private readonly RequestEpoch epoch = new();
    private YouTubeSession? session;
    private readonly SemaphoreSlim watchGate = new(1);
    private readonly HashSet<string> consumedCursors = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, int>? subscriptionProgress;

    public async Task InitializeAsync()
    {
        var op = epoch.Begin();
        epoch.Commit(op.Id, () => { ClearPrivateState(); Busy = true; Phase = SessionPhase.Restoring; Status = "Restoring your session…"; Changed?.Invoke(); });
        try
        {
            var token = store.Load();
            if (string.IsNullOrEmpty(token))
            {
                epoch.Commit(op.Id, () => { Phase = SessionPhase.SignedOut; Status = "Sign in to load your YouTube feeds."; });
                return;
            }
            var restored = await api.RefreshSessionAsync(token, op.Token);
            if (!epoch.Commit(op.Id, () => CommitRefresh(token, restored))) return;
            restored = await EnsureFreshAsync(restored, op);
            if (restored is null) return;
            var accounts = await api.GetAccountsAsync(restored, op.Token);
            YouTubeAccount? remembered = null;
            if (!epoch.Commit(op.Id, () =>
            {
                var selection = ReadSelection(restored.RefreshToken);
                if (selection is not null)
                {
                    var matches = accounts.Where(account => AccountSelectionKey(account) == selection).Take(2).ToArray();
                    if (matches.Length == 1) remembered = matches[0];
                }
                Accounts = accounts;
                SelectedAccount = remembered;
                Phase = SessionPhase.SignedIn;
                Status = accounts.Count == 0 ? "No accounts returned. Sign out to try another login." : "Choose an account to load Home.";
            })) return;
            if (remembered is not null)
                await FetchHistoryAsync(remembered, append: false, switching: false, restoring: op);
        }
        catch (Exception error) { Fail(op.Id, error); }
        finally { Finish(op.Id); }
    }

    private void Fail(long id, Exception error)
    {
        epoch.Commit(id, () =>
        {
            Authorization = null;
            if (error is OperationCanceledException)
            {
                if (Phase is SessionPhase.Restoring or SessionPhase.Authorizing) ClearPrivateState();
                Status = "The operation was interrupted. Retry, or sign out to clear the saved login.";
                return;
            }
            if (error is YouTubeException { Code: "auth_required" })
            {
                ClearPrivateState();
                try { store.Clear(); } catch { Status = "Signed out, but Windows could not remove the saved login. Try Sign out again."; return; }
            }
            else if (Phase is SessionPhase.Restoring or SessionPhase.Authorizing) ClearPrivateState();
            Status = error is YouTubeException safe ? safe.Message : "This operation could not be completed. Check your connection or Windows credential storage, then retry.";
        });
    }

    private void Finish(long id) => epoch.Commit(id, () => { Busy = false; Appending = false; Changed?.Invoke(); });
    private void ClearPrivateState()
    {
        session = null; Authorization = null; Accounts = []; SelectedAccount = null;
        ClearFeed(); View = BrowseView.Home; Phase = SessionPhase.SignedOut;
    }
    private void ClearFeed()
    {
        subscriptionProgress = null;
        Channels = []; HomeShelves = []; SelectedSubscriptionChannel = null;
        ClearVideoPage();
    }
    private void ClearVideoPage()
    {
        consumedCursors.Clear();
        Appending = false;
        AppendFailed = false;
        Videos = []; Continuation = null;
    }
    public async Task SignInAsync()
    {
        var op = epoch.Begin();
        epoch.Commit(op.Id, () => { ClearPrivateState(); Busy = true; Phase = SessionPhase.Authorizing; Status = "Requesting a device code…"; Changed?.Invoke(); });
        try
        {
            // Clear a previous stored session before allowing a different login.
            if (!epoch.Commit(op.Id, store.Clear)) return;
            var authorization = await api.BeginSignInAsync(deviceId, op.Token);
            if (!epoch.Commit(op.Id, () => { Authorization = authorization; Status = "Enter this code on the activation page. Waiting for your authorization…"; Changed?.Invoke(); })) return;
            var signedIn = await api.CompleteSignInAsync(authorization, op.Token);
            if (!epoch.Commit(op.Id, () => { store.Save(signedIn.RefreshToken); session = signedIn; Authorization = null; })) return;
            signedIn = await EnsureFreshAsync(signedIn, op);
            if (signedIn is null) return;
            var accounts = await api.GetAccountsAsync(signedIn, op.Token);
            epoch.Commit(op.Id, () => { Accounts = accounts; Phase = SessionPhase.SignedIn; Status = accounts.Count == 0 ? "No accounts returned. Sign out to try another login." : "Choose an account to load Home."; });
        }
        catch (Exception error) { Fail(op.Id, error); }
        finally { Finish(op.Id); }
    }
    public Task SelectAccountAsync(YouTubeAccount account)
    {
        if (View == BrowseView.Settings || Phase != SessionPhase.SignedIn || !Accounts.Contains(account)) return Task.CompletedTask;
        return FetchHistoryAsync(account, append: false, switching: true);
    }

    private static string AccountSelectionKey(YouTubeAccount account) =>
        account.PageId is null ? "owner" : "page:" + account.PageId;

    // Qualify even the default owner by the login that supplied the account list.
    private static string SelectionPrefix(string refreshToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken))) + ":";

    private string? ReadSelection(string refreshToken)
    {
        var saved = store.LoadAccountSelection();
        var prefix = SelectionPrefix(refreshToken);
        return saved?.StartsWith(prefix, StringComparison.Ordinal) == true ? saved[prefix.Length..] : null;
    }

    private void CommitRefresh(string previousToken, YouTubeSession fresh)
    {
        var selection = previousToken != fresh.RefreshToken ? ReadSelection(previousToken) : null;
        store.Save(fresh.RefreshToken);
        if (selection is not null)
            store.SaveAccountSelection(SelectionPrefix(fresh.RefreshToken) + selection);
        session = fresh;
    }

    public Task LoadHistoryAsync(bool append = false)
    {
        if (View == BrowseView.Settings || Busy || Phase != SessionPhase.SignedIn || SelectedAccount is null || (append && Continuation is null)) return Task.CompletedTask;
        return FetchHistoryAsync(SelectedAccount, append, switching: false);
    }

    public Task SelectSubscriptionChannelAsync(SubscribedChannel? channel)
    {
        if (View != BrowseView.Subscriptions || Phase != SessionPhase.SignedIn || SelectedAccount is not { } account ||
            ReferenceEquals(channel, SelectedSubscriptionChannel) ||
            (channel is not null && !Channels.Any(item => ReferenceEquals(item, channel)))) return Task.CompletedTask;
        return FetchHistoryAsync(account, append: false, switching: false, changingSubscription: true, subscriptionChoice: channel);
    }

    private async Task FetchHistoryAsync(YouTubeAccount account, bool append, bool switching,
        (long Id, CancellationToken Token)? restoring = null, bool changingSubscription = false, SubscribedChannel? subscriptionChoice = null)
    {
        var op = restoring ?? epoch.Begin();
        YouTubeSession? captured = null;
        string? continuation = null;
        SubscribedChannel? channel = null;
        IReadOnlyDictionary<string, int>? progress = null;
        var view = View;
        epoch.Commit(op.Id, () =>
        {
            captured = session;
            if (switching)
            {
                if (captured is not null)
                    store.SaveAccountSelection(SelectionPrefix(captured.RefreshToken) + AccountSelectionKey(account));
                SelectedAccount = account;
                ClearFeed();
            }
            if (changingSubscription)
            {
                SelectedSubscriptionChannel = subscriptionChoice;
                ClearVideoPage();
            }
            channel = SelectedSubscriptionChannel;
            continuation = append ? Continuation : null;
            if (view == BrowseView.Subscriptions)
            {
                if (!append && !changingSubscription) subscriptionProgress = null;
                progress = subscriptionProgress;
            }
            var label = view switch { BrowseView.Home => "Home videos", BrowseView.Subscriptions => "subscriptions", _ => "history" };
            Appending = append;
            Busy = true; Status = append ? $"Loading more {label}…" : $"Loading {label}…"; Changed?.Invoke();
        });
        try
        {
            if (captured is null) return;
            captured = await EnsureFreshAsync(captured, op);
            if (captured is null) return;
            if (view == BrowseView.Home)
            {
                var home = await api.GetHomeAsync(captured, account, continuation, op.Token);
                epoch.Commit(op.Id, () =>
                {
                    if (!append || home.Shelves.Count > 0)
                    {
                        HomeShelves = append ? HomeShelves.Concat(home.Shelves).ToArray() : home.Shelves;
                        Videos = HomeShelves.SelectMany(shelf => shelf.Videos).ToArray();
                    }
                    var stopped = SetContinuation(continuation, home.Continuation, home.Shelves.Count > 0);
                    Status = stopped ? "YouTube repeated a Home page. Refresh to try again." : home.Message ?? "";
                });
                return;
            }
            if (view == BrowseView.Subscriptions)
            {
                var subscriptions = channel is null
                    ? await api.GetSubscriptionsAsync(captured, account, continuation, op.Token)
                    : await api.GetSubscriptionChannelAsync(captured, account, channel, continuation, op.Token);
                op.Token.ThrowIfCancellationRequested();
                if (progress is null && subscriptions.Videos.Any(video => video.PercentWatched is null))
                {
                    // Reserve this visit's lookup before awaiting; a channel change may cancel it.
                    if (!epoch.Commit(op.Id, () => subscriptionProgress = new Dictionary<string, int>())) return;
                    progress = await ReadSubscriptionProgressAsync(captured, account, op.Token);
                }
                epoch.Commit(op.Id, () =>
                {
                    subscriptionProgress = progress;
                    if (!append && channel is null) Channels = subscriptions.Channels;
                    var videos = progress is { Count: > 0 }
                        ? subscriptions.Videos.Select(video => video.PercentWatched is null && progress.TryGetValue(video.VideoId, out var percent)
                            ? video with { PercentWatched = percent } : video).ToArray()
                        : subscriptions.Videos;
                    var added = UpdateVideos(videos, append);
                    var stopped = SetContinuation(continuation, subscriptions.Continuation, added);
                    Status = stopped ? "YouTube repeated a subscription page. Refresh to try again." : subscriptions.Message ?? (Videos.Count == 0 ? "No subscription videos were returned for this account." : "");
                });
                return;
            }
            var page = await api.GetHistoryAsync(captured, account, continuation, op.Token);
            epoch.Commit(op.Id, () =>
            {
                var added = UpdateVideos(page.Videos, append);
                var stopped = SetContinuation(continuation, page.Continuation, added);
                Status = stopped ? "YouTube repeated a history page. Refresh to try again." : page.Message ?? (Videos.Count == 0 ? "No history cards were returned for this account." : "");
            });
        }
        catch (Exception error)
        {
            if (append) epoch.Commit(op.Id, () => AppendFailed = true);
            Fail(op.Id, error);
        }
        finally { if (restoring is null) Finish(op.Id); }
    }

    private async Task<IReadOnlyDictionary<string, int>> ReadSubscriptionProgressAsync(
        YouTubeSession captured, YouTubeAccount account, CancellationToken cancellationToken)
    {
        // This optional lookup must not hold the subscription grid behind a full History timeout.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        var progress = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            var history = await api.GetHistoryAsync(captured, account, null, budget.Token).WaitAsync(budget.Token);
            foreach (var video in history.Videos)
                if (video.PercentWatched is int percent) progress.TryAdd(video.VideoId, percent);
        }
        catch (YouTubeException) { } // The feed succeeded; missing progress is still unknown.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return progress;
    }

    private bool UpdateVideos(IReadOnlyList<HistoryVideo> page, bool append)
    {
        var updated = (append ? Videos.Concat(page) : page).DistinctBy(v => v.VideoId).ToArray();
        var added = updated.Length > Videos.Count;
        if (!append || added) Videos = updated;
        return added;
    }

    private bool SetContinuation(string? requested, string? next, bool addedItems)
    {
        if (requested is null) consumedCursors.Clear();
        else consumedCursors.Add(requested);
        var repeated = next is not null && consumedCursors.Contains(next);
        Continuation = repeated || string.IsNullOrWhiteSpace(next) ? null : next;
        // A page that makes no visual progress must not automatically chain into more requests.
        AppendFailed = requested is not null && !addedItems && Continuation is not null;
        return repeated;
    }

    public async Task<string?> TryRecordWatchStartAsync(YouTubeAccount account, string videoId, TimeSpan duration, TimeSpan position, CancellationToken playbackToken = default, Action<YouTubeClient.WatchRecord>? onStarted = null)
    {
        YouTubeClient.WatchRecord? record = null;
        var result = await RunWatchOperationAsync(account, async (fresh, ct) =>
        {
            record = await api.RecordWatchStartAsync(fresh, account, videoId, duration, position, ct);
            return "watch_sent";
        }, playbackToken);
        if (result == "watch_sent" && record is not null && !playbackToken.IsCancellationRequested && ReferenceEquals(account, SelectedAccount))
            onStarted?.Invoke(record);
        return result;
    }

    public Task<string?> TryRecordWatchProgressAsync(YouTubeAccount account, YouTubeClient.WatchRecord record, TimeSpan start, TimeSpan end, CancellationToken playbackToken) =>
        RunWatchOperationAsync(account, async (fresh, ct) =>
        {
            await api.RecordWatchProgressAsync(fresh, account, record, start, end, ct);
            return "watch_progress_sent";
        }, playbackToken);

    public Task<string?> TryCompareWatchLookupsAsync(YouTubeAccount account, string videoId, CancellationToken playbackToken = default) =>
        RunWatchOperationAsync(account, async (fresh, ct) =>
        {
            var result = await api.CompareWatchLookupsAsync(fresh, account, videoId, ct);
            return $"Baseline: {Describe(result.Baseline)}. TV fallback: {Describe(result.Candidate)}.";
        }, playbackToken);

    private static string Describe(WatchLookupResult result) => $"{result.Status}, tracking {(result.TrackingUsable ? "usable" : "not usable")}";

    private async Task<string?> RunWatchOperationAsync(YouTubeAccount account, Func<YouTubeSession, CancellationToken, Task<string>> run, CancellationToken playbackToken)
    {
        bool IsCurrent() => Phase == SessionPhase.SignedIn && !Busy && View != BrowseView.Settings && ReferenceEquals(account, SelectedAccount);
        if (!IsCurrent() || playbackToken.IsCancellationRequested) return null;
        var op = epoch.Snapshot();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(op.Token, playbackToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        bool entered = false;
        bool preparingAccount = false;
        bool Retired() => op.Token.IsCancellationRequested || playbackToken.IsCancellationRequested;
        try
        {
            await watchGate.WaitAsync(deadline.Token);
            entered = true;
            deadline.Token.ThrowIfCancellationRequested();
            if (!IsCurrent() || session is not { } current) return null;
            preparingAccount = true;
            var fresh = await EnsureFreshAsync(current, (op.Id, deadline.Token));
            deadline.Token.ThrowIfCancellationRequested();
            if (fresh is null || !IsCurrent()) return null;
            preparingAccount = false;
            var result = await run(fresh, deadline.Token);
            return Retired() ? null : deadline.IsCancellationRequested ? "watch_timed_out" : result;
        }
        catch (Exception) when (Retired()) { return null; }
        catch (Exception) when (preparingAccount) { return "watch_account_failed"; }
        catch (YouTubeException error) { return error.Code; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return "watch_timed_out"; }
        catch (Exception) { return "watch_failed"; }
        finally { if (entered) watchGate.Release(); }
    }

    private async Task<YouTubeSession?> EnsureFreshAsync(YouTubeSession current, (long Id, CancellationToken Token) op)
    {
        if (current.ExpiresAt > UtcNow().AddSeconds(60)) return current;
        var fresh = await api.RefreshSessionAsync(current.RefreshToken, op.Token);
        op.Token.ThrowIfCancellationRequested();
        return epoch.Commit(op.Id, () => CommitRefresh(current.RefreshToken, fresh)) ? fresh : null;
    }

    public void SignOut()
    {
        var op = epoch.Begin();
        epoch.Commit(op.Id, () =>
        {
            ClearPrivateState(); Busy = false; Status = "Signed out on this device.";
            try { store.Clear(); }
            catch { Status = "Signed out, but Windows could not remove the saved login. Try Sign out again."; }
            Changed?.Invoke();
        });
    }
    public void Close()
    {
        var op = epoch.Begin();
        epoch.Commit(op.Id, () => { ClearPrivateState(); Busy = false; });
        Changed = null;
    }
    public void Cancel() => SignOut();
}
