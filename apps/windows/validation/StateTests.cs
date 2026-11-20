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
        api.History = (account, continuation) => account == TestApi.A ? oldHistory.Task : Task.FromResult(new HistoryPage([new("bbbbbbbbbbb", "B result", "Channel", "3:00", 25)], "page2", null));
        var oldLoad = controller.SelectAccountAsync(TestApi.A);
        Verify(controller.Busy, "account history request enters busy state");
        await controller.SelectAccountAsync(TestApi.B);
        oldHistory.SetResult(new HistoryPage([new("aaaaaaaaaaa", "A result", "Channel", "2:00", null)], null, null));
        await oldLoad;
        Verify(controller.SelectedAccount == TestApi.B && controller.Videos.Single().VideoId == "bbbbbbbbbbb", "old account completion cannot replace current native feed");
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
    }

    internal static void Verify(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + message);
    }
}

internal sealed class TestStore : ITokenStore
{
    public string? Value;
    public int Saves;
    public string? Load() => Value;
    public void Save(string value) { Value = value; Saves++; }
    public void Clear() => Value = null;
}

internal sealed class TestApi : IYouTubeApi
{
    internal static readonly YouTubeAccount A = new("Account A", "@a", null);
    internal static readonly YouTubeAccount B = new("Account B", "@b", "page-b");
    internal static YouTubeSession Fresh => new("fixture-access", "fixture-rotation", DateTimeOffset.UtcNow.AddHours(1));
    public int RefreshCalls;
    public Func<Task<YouTubeSession>> Refresh = () => Task.FromResult(Fresh);
    public Task<DeviceAuthorization> BeginSignInAsync(string id, CancellationToken token) => Task.FromResult(new DeviceAuthorization("fixture-user-code", new Uri("https://www.google.com/device"), DateTimeOffset.UtcNow.AddMinutes(5), 5, "fixture-device-code"));
    public Func<Task<YouTubeSession>> Complete = () => Task.FromResult(Fresh);
    public Task<YouTubeSession> CompleteSignInAsync(DeviceAuthorization auth, CancellationToken token) => Complete();
    public Task<YouTubeSession> RefreshSessionAsync(string refresh, CancellationToken token) { RefreshCalls++; return Refresh(); }
    public Task<IReadOnlyList<YouTubeAccount>> GetAccountsAsync(YouTubeSession session, CancellationToken token) => Task.FromResult<IReadOnlyList<YouTubeAccount>>([A, B]);
    public Func<YouTubeAccount, string?, Task<SubscriptionsPage>> Subscriptions = (_, _) => Task.FromResult(new SubscriptionsPage([], [], null, null));
    public Task<SubscriptionsPage> GetSubscriptionsAsync(YouTubeSession session, YouTubeAccount account, string? continuation, CancellationToken token) => Subscriptions(account, continuation);
    public Func<YouTubeAccount, string?, Task<HistoryPage>> History = (_, _) => Task.FromResult(new HistoryPage([], null, null));
    public Task<HistoryPage> GetHistoryAsync(YouTubeSession session, YouTubeAccount account, string? continuation, CancellationToken token) => History(account, continuation);
}
