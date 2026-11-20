using YourTube.App;
using YourTube.Update;
using Xunit;
namespace YourTube.Update.Tests;
public sealed class FlowTests
{
    [Fact]
    public async Task No_startup_work_then_explicit_check_download_restart()
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
    public async Task Late_progress_cannot_rewind_ready()
    {
        var b = new Backend(); using var flow = new WindowsUpdateFlow(b);
        await flow.CheckAsync(); await flow.DownloadAsync();
        b.Progress!(25);
        Assert.Equal(UpdatePhase.Ready, flow.State.Phase);
        Assert.Equal(100, flow.State.Progress);
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
        public int Checks, Downloads, Applied;
        public TaskCompletionSource? Hold { get; set; }
        public Action<int>? Progress;
        public async Task<Version?> CheckAsync() { Checks++; if (Hold is not null) await Hold.Task; return Offer; }
        public async Task DownloadAsync(Version version, Action<int> progress, CancellationToken token) { Downloads++; Progress = progress; progress(50); if (Hold is not null) await Hold.Task.WaitAsync(token); progress(100); }
        public void ApplyAndRestart(Version version) => Applied++;
    }
}
