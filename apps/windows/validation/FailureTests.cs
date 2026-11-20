using YourTube.App;
using YourTube.Core;
using static StateTests;

internal static class FailureTests
{
    public static async Task Run()
    {
        var poll = new TaskCompletionSource<YouTubeSession>();
        var api = new TestApi { Complete = () => poll.Task };
        var vault = new TestStore();
        var controller = new SessionController(api, vault, "fixture-device");
        var signingIn = controller.SignInAsync();
        Verify(controller.Authorization is not null && controller.Busy, "native device code is presented during polling");
        controller.Cancel();
        poll.SetResult(TestApi.Fresh);
        await signingIn;
        Verify(controller.Authorization is null && controller.Phase == SessionPhase.SignedOut && vault.Saves == 0, "cancelled polling cannot persist a late authorized session");

        api.Complete = () => Task.FromResult(TestApi.Fresh);
        await controller.SignInAsync();
        api.History = (_, _) => Task.FromException<HistoryPage>(new YouTubeException("auth_required", "Sign in again."));
        await controller.SelectAccountAsync(TestApi.A);
        Verify(controller.Phase == SessionPhase.SignedOut && controller.Status == "Sign in again." && vault.Value is null, "structured authentication errors revoke private state and vault");

        await controller.SignInAsync();
        api.History = (_, _) => Task.FromResult(new HistoryPage([new("ddddddddddd", "Fixture", "Channel", "1:00", null)], "retry-page", null));
        await controller.SelectAccountAsync(TestApi.A);
        api.History = (_, _) => Task.FromException<HistoryPage>(new HttpRequestException("RAW-TRANSPORT-MARKER"));
        await controller.LoadHistoryAsync(true);
        Verify(!controller.Status.Contains("RAW-TRANSPORT-MARKER") && controller.Videos.Count == 1 && controller.Continuation == "retry-page", "transport failures are sanitized and keep a retryable page");
        Verify(controller.Phase == SessionPhase.SignedIn && !controller.Busy, "transient history errors do not sign out");
        api.History = (_, continuation) => Task.FromResult(new HistoryPage([new("eeeeeeeeeee", "Retry", "Channel", "2:00", null)], null, null));
        await controller.LoadHistoryAsync(true);
        Verify(controller.Videos.Count == 2, "failed load-more can be retried");

        var refresh = new TaskCompletionSource<YouTubeSession>();
        controller.UtcNow = () => DateTimeOffset.UtcNow.AddHours(2);
        api.Refresh = () => refresh.Task;
        var stale = controller.SelectAccountAsync(TestApi.A);
        api.Refresh = () => Task.FromResult(TestApi.Fresh);
        await controller.SelectAccountAsync(TestApi.B);
        var saves = vault.Saves;
        refresh.SetResult(TestApi.Fresh);
        await stale;
        Verify(controller.SelectedAccount == TestApi.B && vault.Saves == saves, "account switch rejects stale refresh storage");
        Console.WriteLine("PASS: delayed polling cancellation, auth revocation, sanitized retryable errors and switch/refresh races.");
    }
}
