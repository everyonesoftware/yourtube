using YourTube.Core;

namespace YourTube.App;

internal interface ITokenStore
{
    string? Load();
    void Save(string refreshToken);
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
    Task RecordWatchStartAsync(YouTubeSession session, YouTubeAccount account, string videoId, TimeSpan duration, TimeSpan position, CancellationToken cancellationToken);
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
    public BrowseView View { get; private set; } = BrowseView.History;
    public async Task NavigateAsync(BrowseView view)
    {
        if (Phase != SessionPhase.SignedIn || View == view) return;
        var op = epoch.Begin();
        epoch.Commit(op.Id, () => { View = view; Videos = []; Channels = []; Continuation = null; Busy = false; Status = view == BrowseView.Settings ? "Settings" : "Choose an account to load this view."; Changed?.Invoke(); });
        if (view != BrowseView.Settings && SelectedAccount is not null)
            await FetchHistoryAsync(SelectedAccount, append: false, switching: false);
    }
    public string? Continuation { get; private set; }
    public event Action? Changed;
    public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
    private readonly RequestEpoch epoch = new();
    private YouTubeSession? session;
    private readonly SemaphoreSlim watchGate = new(1);
    private readonly HashSet<string> subscriptionCursors = new(StringComparer.Ordinal);

    public async Task InitializeAsync()
    {
        var op = epoch.Begin();
        epoch.Commit(op.Id, () => { ClearPrivateState(); Busy = true; Phase = SessionPhase.Restoring; Status = "Restoring your session…"; Changed?.Invoke(); });
        try
        {
            var token = store.Load();
            if (string.IsNullOrEmpty(token))
            {
                epoch.Commit(op.Id, () => { Phase = SessionPhase.SignedOut; Status = "Sign in to load your YouTube history."; });
                return;
            }
            var restored = await api.RefreshSessionAsync(token, op.Token);
            if (!epoch.Commit(op.Id, () => { store.Save(restored.RefreshToken); session = restored; })) return;
            restored = await EnsureFreshAsync(restored, op);
            if (restored is null) return;
            var accounts = await api.GetAccountsAsync(restored, op.Token);
            epoch.Commit(op.Id, () => { Accounts = accounts; Phase = SessionPhase.SignedIn; Status = accounts.Count == 0 ? "No accounts returned. Sign out to try another login." : "Choose an account to load history."; });
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

    private void Finish(long id) => epoch.Commit(id, () => { Busy = false; Changed?.Invoke(); });
    private void ClearPrivateState()
    {
        session = null; Authorization = null; Accounts = []; SelectedAccount = null;
        subscriptionCursors.Clear();
        Videos = []; Channels = []; Continuation = null; View = BrowseView.History; Phase = SessionPhase.SignedOut;
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
            epoch.Commit(op.Id, () => { Accounts = accounts; Phase = SessionPhase.SignedIn; Status = accounts.Count == 0 ? "No accounts returned. Sign out to try another login." : "Choose an account to load history."; });
        }
        catch (Exception error) { Fail(op.Id, error); }
        finally { Finish(op.Id); }
    }
    public Task SelectAccountAsync(YouTubeAccount account)
    {
        if (View == BrowseView.Settings || Phase != SessionPhase.SignedIn || !Accounts.Contains(account)) return Task.CompletedTask;
        return FetchHistoryAsync(account, append: false, switching: true);
    }

    public Task LoadHistoryAsync(bool append = false)
    {
        if (View == BrowseView.Settings || Busy || Phase != SessionPhase.SignedIn || SelectedAccount is null || (append && Continuation is null)) return Task.CompletedTask;
        return FetchHistoryAsync(SelectedAccount, append, switching: false);
    }

    private async Task FetchHistoryAsync(YouTubeAccount account, bool append, bool switching)
    {
        var op = epoch.Begin();
        YouTubeSession? captured = null;
        string? continuation = null;
        epoch.Commit(op.Id, () =>
        {
            captured = session;
            if (switching) { SelectedAccount = account; Videos = []; Channels = []; Continuation = null; }
            continuation = append ? Continuation : null;
            var label = View == BrowseView.Subscriptions ? "subscriptions" : "history";
            Busy = true; Status = append ? $"Loading more {label}…" : $"Loading {label}…"; Changed?.Invoke();
        });
        try
        {
            if (captured is null) return;
            captured = await EnsureFreshAsync(captured, op);
            if (captured is null) return;
            if (View == BrowseView.Subscriptions)
            {
                var subscriptions = await api.GetSubscriptionsAsync(captured, account, continuation, op.Token);
                epoch.Commit(op.Id, () =>
                {
                    Channels = append ? Channels : subscriptions.Channels;
                    Videos = (append ? Videos.Concat(subscriptions.Videos) : subscriptions.Videos).DistinctBy(v => v.VideoId).ToArray();
                    if (!append) subscriptionCursors.Clear();
                    if (continuation is not null) subscriptionCursors.Add(continuation);
                    var stopped = subscriptions.Continuation is not null && subscriptionCursors.Contains(subscriptions.Continuation);
                    Continuation = stopped || string.IsNullOrWhiteSpace(subscriptions.Continuation) ? null : subscriptions.Continuation;
                    Status = stopped ? "YouTube repeated a subscription page. Refresh to try again." : subscriptions.Message ?? (Videos.Count == 0 ? "No subscription videos were returned for this account." : "");
                });
                return;
            }
            var page = await api.GetHistoryAsync(captured, account, continuation, op.Token);
            epoch.Commit(op.Id, () =>
            {
                Videos = (append ? Videos.Concat(page.Videos) : page.Videos).DistinctBy(v => v.VideoId).ToArray();
                Continuation = page.Continuation;
                Status = page.Message ?? (Videos.Count == 0 ? "No history cards were returned for this account." : "");
            });
        }
        catch (Exception error) { Fail(op.Id, error); }
        finally { Finish(op.Id); }
    }
    public async Task<string?> TryRecordWatchStartAsync(YouTubeAccount account, string videoId, TimeSpan duration, TimeSpan position, CancellationToken playbackToken = default)
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
            await api.RecordWatchStartAsync(fresh, account, videoId, duration, position, deadline.Token);
            return Retired() ? null : deadline.IsCancellationRequested ? "watch_timed_out" : "watch_sent";
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
        return epoch.Commit(op.Id, () => { store.Save(fresh.RefreshToken); session = fresh; }) ? fresh : null;
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
