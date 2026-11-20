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
    public string? Continuation { get; private set; }
    public event Action? Changed;
    public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
    private readonly RequestEpoch epoch = new();
    private YouTubeSession? session;

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
        Videos = []; Continuation = null; Phase = SessionPhase.SignedOut;
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
        if (Phase != SessionPhase.SignedIn || !Accounts.Contains(account)) return Task.CompletedTask;
        return FetchHistoryAsync(account, append: false, switching: true);
    }

    public Task LoadHistoryAsync(bool append = false)
    {
        if (Busy || Phase != SessionPhase.SignedIn || SelectedAccount is null || (append && Continuation is null)) return Task.CompletedTask;
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
            if (switching) { SelectedAccount = account; Videos = []; Continuation = null; }
            continuation = append ? Continuation : null;
            Busy = true; Status = append ? "Loading more history…" : "Loading history…"; Changed?.Invoke();
        });
        try
        {
            if (captured is null) return;
            captured = await EnsureFreshAsync(captured, op);
            if (captured is null) return;
            var page = await api.GetHistoryAsync(captured, account, continuation, op.Token);
            epoch.Commit(op.Id, () =>
            {
                Videos = (append ? Videos.Concat(page.Videos) : page.Videos).DistinctBy(v => v.VideoId).ToArray();
                Continuation = page.Continuation;
                Status = page.Message ?? (Videos.Count == 0 ? "No history cards were returned for this account." : $"{Videos.Count} history cards loaded. Select a video to open it in your browser.");
            });
        }
        catch (Exception error) { Fail(op.Id, error); }
        finally { Finish(op.Id); }
    }
    private async Task<YouTubeSession?> EnsureFreshAsync(YouTubeSession current, (long Id, CancellationToken Token) op)
    {
        if (current.ExpiresAt > UtcNow().AddSeconds(60)) return current;
        var fresh = await api.RefreshSessionAsync(current.RefreshToken, op.Token);
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
