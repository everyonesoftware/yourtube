using YourTube.App;
using YourTube.Core;

internal static class StateTests
{
    public static async Task Run()
    {
        var api = new TestApi();
        var vault = new TestStore { Value = "fixture-refresh" };
        var controller = new SessionController(api, vault, "fixture-device");
        Verify(controller.Phase == SessionPhase.Restoring, "startup authentication is unknown");
        await controller.InitializeAsync();
        Verify(controller.Phase == SessionPhase.SignedIn, "restore signs in using stored refresh token");
        Verify(api.RefreshCalls == 1 && controller.Accounts.Count == 2, "restore loads real API account results");
        Verify(vault.Saves == 1, "refresh rotation is persisted");
        Console.WriteLine("PASS: session restoration and refresh persistence.");
        var delayed = new TaskCompletionSource<YouTubeSession>();
        api.Refresh = () => delayed.Task;
        var restoring = controller.InitializeAsync();
        controller.SignOut();
        var saves = vault.Saves;
        delayed.SetResult(TestApi.Fresh);
        await restoring;
        Verify(controller.Phase == SessionPhase.SignedOut && controller.Accounts.Count == 0, "sign-out rejects delayed restore");
        Verify(vault.Value is null && vault.Saves == saves, "delayed restore cannot resurrect vault credentials");
        await controller.SignInAsync();
        Verify(controller.Phase == SessionPhase.SignedIn && controller.Accounts.Count == 2, "native device sign-in completes and loads account chooser");
        controller.Cancel();
        Verify(controller.Phase == SessionPhase.SignedOut && vault.Value is null, "cancel clears session and stored token");
        Console.WriteLine("PASS: sign-out/cancel invalidate delayed restore and device sign-in.");
        await controller.SignInAsync();
        var oldHistory = new TaskCompletionSource<HistoryPage>();
        await controller.NavigateAsync(BrowseView.History);
        api.History = (account, continuation) => account == TestApi.A ? oldHistory.Task : Task.FromResult(new HistoryPage([new("bbbbbbbbbbb", "B result", "Channel", "3:00", 25)], "page2", null));
        var oldLoad = controller.SelectAccountAsync(TestApi.A);
        Verify(controller.Busy, "account history request enters busy state");
        await controller.SelectAccountAsync(TestApi.B);
        oldHistory.SetResult(new HistoryPage([new("aaaaaaaaaaa", "A result", "Channel", "2:00", null)], null, null));
        await oldLoad;
        Verify(controller.SelectedAccount == TestApi.B && controller.Videos.Single().VideoId == "bbbbbbbbbbb", "old account completion cannot replace current native feed");
        Verify(controller.Status.Length == 0, "a successful history feed does not reserve space for a completion notice");
        Verify(!controller.Busy && controller.Continuation == "page2", "old completion cannot reset current pagination/busy state");
        api.History = (account, continuation) => { Verify(continuation == "page2", "load-more forwards exact continuation"); return Task.FromResult(new HistoryPage([new("bbbbbbbbbbb", "duplicate", "Channel", "3:00", null), new("ccccccccccc", "Next", "Channel", "4:00", null)], null, null)); };
        await controller.LoadHistoryAsync(true);
        Verify(controller.Videos.Count == 2 && controller.Continuation is null, "load-more appends actual results without duplicate video IDs");
        controller.SignOut();
        Verify(controller.Videos.Count == 0 && controller.SelectedAccount is null && controller.Accounts.Count == 0, "sign-out clears displayed private data");
        Console.WriteLine("PASS: account switches reject stale history; pagination and private-state clearing.");
        await controller.SignInAsync();
        controller.UtcNow = () => DateTimeOffset.UtcNow.AddHours(2);
        api.Refresh = () => Task.FromResult(TestApi.Fresh);
        await controller.NavigateAsync(BrowseView.History);
        api.History = (_, _) => Task.FromResult(new HistoryPage([], null, "Empty from provider"));
        var before = api.RefreshCalls;
        await controller.SelectAccountAsync(TestApi.A);
        Verify(api.RefreshCalls == before + 1, "history refreshes expiring access token first");
        Verify(controller.Status == "Empty from provider", "safe provider empty message is rendered honestly");
        var refreshLate = new TaskCompletionSource<YouTubeSession>();
        api.Refresh = () => refreshLate.Task;
        var refreshLoad = controller.LoadHistoryAsync();
        controller.SignOut();
        saves = vault.Saves;
        refreshLate.SetResult(TestApi.Fresh);
        await refreshLoad;
        Verify(vault.Value is null && vault.Saves == saves && controller.Videos.Count == 0, "late history token refresh cannot write after sign-out");
        Console.WriteLine("PASS: expiry refresh and late refresh vault protection.");
        controller.UtcNow = () => DateTimeOffset.UtcNow;
        vault.Value = "fixture-refresh";
        var closeLate = new TaskCompletionSource<YouTubeSession>();
        api.Refresh = () => closeLate.Task;
        var closingRestore = controller.InitializeAsync();
        controller.Close();
        saves = vault.Saves;
        closeLate.SetResult(TestApi.Fresh);
        await closingRestore;
        Verify(vault.Value == "fixture-refresh" && vault.Saves == saves, "closing preserves saved login but rejects pending credential writes");
        Verify(controller.Accounts.Count == 0, "closing clears private in-memory state");
        Console.WriteLine("PASS: close invalidates requests without signing out saved login.");
        var expiryApi = new TestApi();
        var expiry = new SessionController(expiryApi, new TestStore(), "fixture-device") { UtcNow = () => DateTimeOffset.UtcNow.AddHours(2) };
        await expiry.SignInAsync();
        Verify(expiryApi.RefreshCalls == 1, "account chooser refreshes expiring token before account request");
        Console.WriteLine("PASS: account enumeration refreshes expiring tokens.");
        expiry.UtcNow = () => DateTimeOffset.UtcNow;
        var pendingRestore = new TaskCompletionSource<YouTubeSession>();
        expiryApi.Refresh = () => pendingRestore.Task;
        var retry = expiry.InitializeAsync();
        Verify(expiry.Phase == SessionPhase.Restoring && expiry.Accounts.Count == 0 && expiry.Videos.Count == 0, "retry restoration clears old private state while auth is unknown");
        pendingRestore.SetException(new OperationCanceledException());
        await retry;
        Verify(expiry.Phase == SessionPhase.SignedOut && !expiry.Busy, "interrupted startup is retryable, not a stranded authorization view");
        Console.WriteLine("PASS: retry restoration and interrupted startup clear private state.");
        await WatchRecordAsync();
        await RememberAccountAsync();
        await RememberedAccountIsBoundToLoginAsync();
    }

    private static async Task RememberedAccountIsBoundToLoginAsync()
    {
        var store = new TestStore();
        var oldApi = new TestApi();
        var oldWindow = new SessionController(oldApi, store, "fixture-device");
        await oldWindow.SignInAsync();

        var anotherOwner = new YouTubeAccount("Another owner", null, null);
        var anotherLogin = new YouTubeSession("new-access", "new-refresh", DateTimeOffset.UtcNow.AddHours(1));
        var newApi = new TestApi
        {
            Complete = () => Task.FromResult(anotherLogin),
            Refresh = () => Task.FromResult(anotherLogin),
            Accounts = () => Task.FromResult<IReadOnlyList<YouTubeAccount>>([anotherOwner]),
            Home = (_, _) => throw new InvalidOperationException("Unexpected Home request for the replacement login")
        };
        var newWindow = new SessionController(newApi, store, "fixture-device");
        await newWindow.SignInAsync();
        await oldWindow.SelectAccountAsync(TestApi.A);
        var reopened = new SessionController(newApi, store, "fixture-device");
        await reopened.InitializeAsync();
        Verify(reopened.Phase == SessionPhase.SignedIn && reopened.SelectedAccount is null, "a stale window cannot select the default owner of a replacement login");
    }

    private static async Task RememberAccountAsync()
    {
        var store = new TestStore();
        var api = new TestApi();
        var first = new SessionController(api, store, "fixture-device");
        await first.SignInAsync();
        await first.SelectAccountAsync(TestApi.A);
        await first.SelectAccountAsync(TestApi.B);
        first.Close();

        var renamed = TestApi.B with { Name = "Renamed", Handle = "@renamed" };
        api.Accounts = () => Task.FromResult<IReadOnlyList<YouTubeAccount>>([renamed, TestApi.A]);
        api.Refresh = () => Task.FromResult(new YouTubeSession("rotated-access", "rotated-refresh", DateTimeOffset.UtcNow.AddHours(1)));
        var home = new TaskCompletionSource<HomePage>();
        var requests = new List<YouTubeAccount>();
        api.Home = (account, _) => { requests.Add(account); return home.Task; };
        var reopened = new SessionController(api, store, "fixture-device");
        var startup = reopened.InitializeAsync();
        Verify(ReferenceEquals(reopened.SelectedAccount, renamed), "reopening selects the latest identity by page ID, not account order or display text");
        Verify(reopened.Busy && requests.SequenceEqual([renamed]), "reopening immediately loads Home for the restored identity only");
        home.SetResult(new HomePage([new("Recommended", [new("bbbbbbbbbbb", "Restored", "Channel", "3:00", null)])], null, null));
        await startup;
        Verify(!reopened.Busy && reopened.Videos.Single().VideoId == "bbbbbbbbbbb", "the restored identity's feed is displayed");

        api.Accounts = () => Task.FromResult<IReadOnlyList<YouTubeAccount>>([TestApi.A]);
        requests.Clear();
        var missing = new SessionController(api, store, "fixture-device");
        await missing.InitializeAsync();
        Verify(missing.Phase == SessionPhase.SignedIn && missing.SelectedAccount is null && requests.Count == 0, "a missing identity falls back to the account chooser without browsing as another user");
        await missing.SelectAccountAsync(TestApi.A);
        missing.Close();

        var owner = new SessionController(api, store, "fixture-device");
        await owner.InitializeAsync();
        Verify(ReferenceEquals(owner.SelectedAccount, TestApi.A), "the default owner without a page ID is also remembered");
        owner.SignOut();
        Verify(store.Value is null && store.AccountSelection is null, "sign-out clears saved identity and token");
        await owner.SignInAsync();
        Verify(owner.SelectedAccount is null, "a new login still requires an explicit account choice");
        await owner.SelectAccountAsync(TestApi.A);
        var accounts = new TaskCompletionSource<IReadOnlyList<YouTubeAccount>>();
        api.Accounts = () => accounts.Task;
        var interrupted = new SessionController(api, store, "fixture-device");
        var restoring = interrupted.InitializeAsync();
        interrupted.SignOut();
        requests.Clear();
        accounts.SetResult([TestApi.A]);
        await restoring;
        Verify(interrupted.SelectedAccount is null && requests.Count == 0 && store.AccountSelection is null, "sign-out during account lookup prevents auto-selection and Home loading");
        Console.WriteLine("PASS: last identity is restored safely across launches.");
    }

    private static async Task WatchRecordAsync()
    {
        var api = new TestApi();
        var vault = new TestStore();
        var controller = new SessionController(api, vault, "fixture-device");
        var length = TimeSpan.FromMinutes(2);
        var position = TimeSpan.FromMilliseconds(250);
        await controller.SignInAsync();
        await controller.SelectAccountAsync(TestApi.B);
        Verify(await controller.TryRecordWatchStartAsync(TestApi.A, "abcdefghijk", length, position) is null && api.WatchCalls == 0, "watch writes cannot choose another identity");
        Verify(await controller.TryCompareWatchLookupsAsync(TestApi.A, "abcdefghijk") is null && api.CompareCalls == 0, "lookup comparison cannot choose another identity");
        Verify(await controller.TryCompareWatchLookupsAsync(TestApi.B, "abcdefghijk") == "Baseline: LOGIN_REQUIRED, tracking not usable. TV fallback: OK, tracking usable." && api.CompareCalls == 1 && api.WatchCalls == 0, "diagnostic compares without recording a start");
        api.Watch = (account, id, duration, start, _) =>
        {
            Verify(ReferenceEquals(account, TestApi.B) && id == "abcdefghijk" && duration == length && start == position, "watch data belongs to selected playback and identity");
            return Task.CompletedTask;
        };
        Verify(await controller.TryRecordWatchStartAsync(TestApi.B, "abcdefghijk", length, position) == "watch_sent" && api.WatchCalls == 1, "watch write uses selected identity");
        api.Watch = (_, _, _, _, _) => Task.FromException(new YouTubeException("watch_lookup_failed", "fixture"));
        Verify(await controller.TryRecordWatchStartAsync(TestApi.B, "abcdefghijk", length, position) == "watch_lookup_failed" && controller.Phase == SessionPhase.SignedIn && vault.Value is not null,
            "experimental watch lookup error preserves its stage and does not sign out or mutate the browse session");
        var delayed = new TaskCompletionSource<YouTubeSession>();
        controller.UtcNow = () => DateTimeOffset.UtcNow.AddHours(2);
        api.Refresh = () => Task.FromException<YouTubeSession>(new YouTubeException("auth_required", "fixture"));
        Verify(await controller.TryRecordWatchStartAsync(TestApi.B, "abcdefghijk", length, position) == "watch_account_failed" && api.WatchCalls == 2 && controller.Phase == SessionPhase.SignedIn,
            "account preparation failure is distinct and cannot send a watch request");
        api.Refresh = () => delayed.Task;
        var pending = controller.TryRecordWatchStartAsync(TestApi.B, "abcdefghijk", length, position);
        controller.SignOut();
        delayed.SetResult(TestApi.Fresh);
        Verify(await pending is null && api.WatchCalls == 2 && vault.Value is null, "sign-out during refresh prevents stale credentials and watch requests");
        controller.UtcNow = () => DateTimeOffset.UtcNow;
        await controller.SignInAsync();
        await controller.SelectAccountAsync(TestApi.B);
        using var replaced = new CancellationTokenSource();
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken dispatchedToken = default;
        api.Watch = (_, id, _, _, ct) =>
        {
            if (id != "abcdefghijk") return Task.CompletedTask;
            dispatchedToken = ct;
            return drain.Task;
        };
        var oldStart = controller.TryRecordWatchStartAsync(TestApi.B, "abcdefghijk", length, position, replaced.Token);
        replaced.Cancel();
        var nextStart = controller.TryRecordWatchStartAsync(TestApi.B, "zzzzzzzzzzz", length, position);
        Verify(dispatchedToken.IsCancellationRequested && !nextStart.IsCompleted && api.WatchCalls == 3, "replacing playback cancels its transport and the new start waits for the old call to drain");
        drain.SetResult();
        Verify(await oldStart is null && await nextStart == "watch_sent" && api.WatchCalls == 4, "replacement reports after the retired request drains");
        // The HTTP fixtures test the opaque record; this fake tests account/lifetime admission.
        var end = TimeSpan.FromSeconds(15);
        api.Progress = (account, start, finish) =>
            Verify(ReferenceEquals(account, TestApi.B) && start == position && finish == end, "progress retains the selected identity and sampled range");
        Verify(await controller.TryRecordWatchProgressAsync(TestApi.A, null!, position, end, default) is null && api.ProgressCalls == 0,
            "an unselected account cannot send progress");
        Verify(await controller.TryRecordWatchProgressAsync(TestApi.B, null!, position, end, default) == "watch_progress_sent" && api.ProgressCalls == 1,
            "confirmed progress is surfaced separately from a watch start");
        delayed = new TaskCompletionSource<YouTubeSession>();
        controller.UtcNow = () => DateTimeOffset.UtcNow.AddHours(2);
        api.Refresh = () => delayed.Task;
        var retiredProgress = controller.TryRecordWatchProgressAsync(TestApi.B, null!, position, end, default);
        controller.SignOut();
        delayed.SetResult(TestApi.Fresh);
        Verify(await retiredProgress is null && api.ProgressCalls == 1 && vault.Value is null,
            "sign-out during a progress refresh blocks the write and stale credentials");
        Console.WriteLine("PASS: watch reporting binds the selected identity and does not disrupt browsing on failure.");
    }

    internal static void Verify(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + message);
    }
}

internal sealed class TestStore : ITokenStore
{
    public string? Value;
    public string? AccountSelection;
    public int Saves;
    public string? Load() => Value;
    public void Save(string value) { Value = value; Saves++; }
    public string? LoadAccountSelection() => AccountSelection;
    public void SaveAccountSelection(string value) => AccountSelection = value;
    public void Clear() { Value = null; AccountSelection = null; }
}

internal sealed class TestApi : IYouTubeApi
{
    public int CompareCalls;
    public Task<WatchLookupComparison> CompareWatchLookupsAsync(YouTubeSession session, YouTubeAccount account, string videoId, CancellationToken cancellationToken)
    {
        CompareCalls++;
        StateTests.Verify(ReferenceEquals(account, B) && videoId == "abcdefghijk", "diagnostic uses the selected playback and identity");
        return Task.FromResult(new WatchLookupComparison(new("LOGIN_REQUIRED", false), new("OK", true)));
    }

    public int WatchCalls;
    public Func<YouTubeAccount, string, TimeSpan, TimeSpan, CancellationToken, Task> Watch = (_, _, _, _, _) => Task.CompletedTask;
    public async Task<YouTubeClient.WatchRecord> RecordWatchStartAsync(YouTubeSession session, YouTubeAccount account, string videoId, TimeSpan duration, TimeSpan position, CancellationToken cancellationToken)
    { WatchCalls++; await Watch(account, videoId, duration, position, cancellationToken); return null!; }
    public int ProgressCalls;
    public Action<YouTubeAccount, TimeSpan, TimeSpan>? Progress;
    public Task RecordWatchProgressAsync(YouTubeSession session, YouTubeAccount account, YouTubeClient.WatchRecord record, TimeSpan start, TimeSpan end, CancellationToken cancellationToken)
    { ProgressCalls++; Progress?.Invoke(account, start, end); return Task.CompletedTask; }
    internal static readonly YouTubeAccount A = new("Account A", "@a", null);
    internal static readonly YouTubeAccount B = new("Account B", "@b", "page-b");
    internal static YouTubeSession Fresh => new("fixture-access", "fixture-rotation", DateTimeOffset.UtcNow.AddHours(1));
    public int RefreshCalls;
    public Func<Task<YouTubeSession>> Refresh = () => Task.FromResult(Fresh);
    public Task<DeviceAuthorization> BeginSignInAsync(string id, CancellationToken token) => Task.FromResult(new DeviceAuthorization("fixture-user-code", new Uri("https://www.google.com/device"), DateTimeOffset.UtcNow.AddMinutes(5), 5, "fixture-device-code"));
    public Func<Task<YouTubeSession>> Complete = () => Task.FromResult(Fresh);
    public Task<YouTubeSession> CompleteSignInAsync(DeviceAuthorization auth, CancellationToken token) => Complete();
    public Task<YouTubeSession> RefreshSessionAsync(string refresh, CancellationToken token) { RefreshCalls++; return Refresh(); }
    public Func<Task<IReadOnlyList<YouTubeAccount>>> Accounts = () => Task.FromResult<IReadOnlyList<YouTubeAccount>>([A, B]);
    public Task<IReadOnlyList<YouTubeAccount>> GetAccountsAsync(YouTubeSession session, CancellationToken token) => Accounts();
    public Func<YouTubeAccount, string?, Task<SubscriptionsPage>> Subscriptions = (_, _) => Task.FromResult(new SubscriptionsPage([], [], null, null));
    public Task<SubscriptionsPage> GetSubscriptionsAsync(YouTubeSession session, YouTubeAccount account, string? continuation, CancellationToken token) => Subscriptions(account, continuation);
    public Func<YouTubeAccount, string?, Task<HomePage>> Home = (_, _) => Task.FromResult(new HomePage([], null, null));
    public Task<HomePage> GetHomeAsync(YouTubeSession session, YouTubeAccount account, string? continuation, CancellationToken token) => Home(account, continuation);
    public Func<YouTubeAccount, string?, Task<HistoryPage>> History = (_, _) => Task.FromResult(new HistoryPage([], null, null));
    public Task<HistoryPage> GetHistoryAsync(YouTubeSession session, YouTubeAccount account, string? continuation, CancellationToken token) => History(account, continuation);
}
