using YourTube.App;
using YourTube.Core;
using static StateTests;

internal static class HomeStateTests
{
    public static async Task Run()
    {
        var recommended = new HomeShelf("Recommended", [new("aaaaaaaaaaa", "Fixture recommendation", "", "", null)]);
        var shorts = new HomeShelf("Shorts", [new("bbbbbbbbbbb", "Fixture Short", "", "", null)]);
        var extra = new HomeShelf("More videos", [new("ccccccccccc", "Fixture video", "", "", null)]);
        var api = new TestApi();
        var controller = new SessionController(api, new TestStore(), "fixture-device");
        api.Home = (account, cursor) =>
        {
            Verify(ReferenceEquals(account, TestApi.A), "Home requests use the selected identity");
            return Task.FromResult(cursor switch
            {
                null => new HomePage([recommended, shorts], "next", null),
                "next" => new HomePage([extra], "next", null),
                _ => throw new InvalidOperationException("Unexpected Home cursor.")
            });
        };
        await controller.SignInAsync();
        await controller.SelectAccountAsync(TestApi.A);
        Verify(controller.View == BrowseView.Home && controller.HomeShelves.SequenceEqual([recommended, shorts]), "signed-in account opens Home by default with the supplied shelves");
        Verify(ReferenceEquals(controller.Videos[0], recommended.Videos[0]), "Home playback uses the displayed card instance");
        await controller.LoadHistoryAsync(append: true);
        Verify(controller.HomeShelves.SequenceEqual([recommended, shorts, extra]) && controller.Continuation is null, "Home appends provider shelves and stops a repeated page cursor");
        await controller.LoadHistoryAsync();
        Verify(controller.HomeShelves.SequenceEqual([recommended, shorts]) && controller.Continuation == "next", "Home refresh replaces shelves and resets pagination");
        var late = new TaskCompletionSource<HomePage>();
        api.Home = (_, _) => late.Task;
        var pending = controller.LoadHistoryAsync(append: true);
        await controller.NavigateAsync(BrowseView.Settings);
        late.SetResult(new([extra], "stale", null));
        await pending;
        Verify(controller.HomeShelves.Count == 0 && controller.Videos.Count == 0 && controller.Continuation is null && !controller.Busy, "late Home response cannot restore private shelves after navigation");
        Console.WriteLine("PASS: Home default route, card identity, shelf pagination, refresh and stale results.");
    }
}
