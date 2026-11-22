using YourTube.App;
using YourTube.Update;
using Xunit;
namespace YourTube.Update.Tests;
public sealed class FlowTests
{
    [Fact]
    public async Task Creating_flow_only_then_explicit_check_download_restart()
    {
        var b = new Backend(); int saved = 0;
        using var flow = new WindowsUpdateFlow(b, () => saved++);
        Assert.Equal(0, b.Checks);
        await flow.RestartAsync(); Assert.Equal(0, b.Applied);
        await flow.CheckAsync(); Assert.Equal(UpdatePhase.Available, flow.State.Phase);
        Assert.Equal(0, b.Downloads);
        await flow.DownloadAsync(); Assert.Equal(UpdatePhase.Ready, flow.State.Phase);
        Assert.Equal(100, flow.State.Progress); Assert.Equal(0, b.Applied);
        await flow.CheckAsync(); Assert.Equal(1, b.Checks);
        await flow.RestartAsync(); Assert.Equal(1, saved); Assert.Equal(1, b.Applied);
    }
    [Fact]
    public async Task Startup_discovery_is_once_and_shares_the_manual_check_gate()
    {
        var b = new Backend { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var flow = new WindowsUpdateFlow(b);
        var pending = flow.CheckOnStartupAsync();
        Assert.Equal(UpdatePhase.Checking, flow.State.Phase);
        await flow.CheckAsync();
        await flow.CheckOnStartupAsync();
        b.Hold.SetResult();
        await pending;
        await flow.CheckOnStartupAsync();
        Assert.Equal(UpdatePhase.Available, flow.State.Phase);
        Assert.Equal(1, b.Checks);
        Assert.Equal(0, b.Downloads);
        Assert.Equal(0, b.Applied);
    }

    [Fact]
    public async Task Startup_failure_leaves_manual_retry_available()
    {
        var b = new Backend { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var flow = new WindowsUpdateFlow(b);
        var pending = flow.CheckOnStartupAsync();
        b.Hold.SetException(new IOException());
        await pending;
        Assert.Equal(UpdatePhase.Error, flow.State.Phase);
        Assert.Null(flow.State.Version);
        b.Hold = null;
        b.Offer = null;
        await flow.CheckAsync();
        Assert.Equal(UpdatePhase.Current, flow.State.Phase);
        Assert.Null(flow.State.Version);
        Assert.Equal(2, b.Checks);
    }

    [Fact]
    public async Task Late_progress_cannot_rewind_ready()
    {
        var b = new Backend(); using var flow = new WindowsUpdateFlow(b);
        await flow.CheckAsync(); await flow.DownloadAsync();
        b.Progress!(25);
        Assert.Equal(UpdatePhase.Ready, flow.State.Phase);
        Assert.Equal(100, flow.State.Progress);
    }
    [Fact]
    public async Task Checked_notes_survive_download_but_not_a_replacement_offer()
    {
        var notes = new WindowsChangelog(new string('a', 40), new string('b', 40), ["Improve"], [], []);
        var b = new Backend { Changelog = notes };
        using var flow = new WindowsUpdateFlow(b);
        await flow.CheckAsync();
        Assert.Same(notes, flow.State.Changelog);
        b.Offer = new(0, 1, 4);
        b.Changelog = null;
        await flow.CheckAsync();
        Assert.Null(flow.State.Changelog);
        Assert.Equal(new Version(0, 1, 4), flow.State.Version);

        b.Changelog = notes;
        await flow.CheckAsync();
        b.Hold = new();
        var download = flow.DownloadAsync();
        Assert.Same(notes, flow.State.Changelog);
        b.Hold.SetResult();
        await download;
        Assert.Equal(UpdatePhase.Ready, flow.State.Phase);
        Assert.Same(notes, flow.State.Changelog);
        Assert.Equal(3, b.Checks);
    }
    [Fact]
    public async Task Busy_and_disposal_fence_late_checks_and_cancel_download()
    {
        var b = new Backend { Hold = new() }; var flow = new WindowsUpdateFlow(b);
        var pending = flow.CheckAsync(); await flow.CheckAsync(); Assert.Equal(1, b.Checks);
        flow.Dispose(); b.Hold.SetResult(); await pending;
        Assert.Equal(UpdatePhase.Checking, flow.State.Phase);
        await flow.DownloadAsync(); Assert.Equal(0, b.Downloads);
        var b2 = new Backend(); var f2 = new WindowsUpdateFlow(b2); await f2.CheckAsync();
        b2.Hold = new(); var download = f2.DownloadAsync(); f2.Dispose(); await download;
        Assert.Equal(UpdatePhase.Downloading, f2.State.Phase); Assert.Equal(0, b2.Applied);
    }
    [Fact]
    public async Task Failed_preservation_blocks_apply_and_retains_retry()
    {
        var b = new Backend(); using var f = new WindowsUpdateFlow(b, () => throw new IOException("secret/path/token"));
        await f.CheckAsync(); await f.DownloadAsync(); await f.RestartAsync();
        Assert.Equal(0, b.Applied); Assert.Equal(UpdatePhase.Ready, f.State.Phase);
        Assert.DoesNotContain("secret", f.State.Message);
    }
    [Fact]
    public async Task Loose_build_and_current_are_distinct()
    {
        var b = new Backend { IsInstalled = false }; using var f = new WindowsUpdateFlow(b);
        await f.CheckAsync(); Assert.Equal(UpdatePhase.NotInstalled, f.State.Phase); Assert.Equal(0, b.Checks);
        b.IsInstalled = true; b.Offer = null; await f.CheckAsync(); Assert.Equal(UpdatePhase.Current, f.State.Phase);
    }
    internal sealed class Backend : IWindowsUpdateBackend
    {
        public bool IsInstalled { get; set; } = true;
        public Version CurrentVersion => new(0, 1, 1);
        public Version? Offer = new(0, 1, 2);
        public WindowsChangelog? Changelog { get; set; }
        public Task<WindowsChangelog?> LoadChangelogAsync(CancellationToken token) => Task.FromResult(Changelog);
        public int Checks, Downloads, Applied;
        public TaskCompletionSource? Hold { get; set; }
        public Action<int>? Progress;
        public async Task<Version?> CheckAsync() { Checks++; if (Hold is not null) await Hold.Task; return Offer; }
        public async Task DownloadAsync(Version version, Action<int> progress, CancellationToken token) { Downloads++; Progress = progress; progress(50); if (Hold is not null) await Hold.Task.WaitAsync(token); progress(100); }
        public void ApplyAndRestart(Version version) => Applied++;
    }
}
