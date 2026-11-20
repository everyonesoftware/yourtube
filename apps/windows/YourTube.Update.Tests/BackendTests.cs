using YourTube.Update;
using Xunit;
namespace YourTube.Update.Tests;
public sealed class BackendTests
{
    [Fact]
    public async Task Explicit_download_and_restart_are_separate_and_guarded()
    {
        var engine = new Engine(); int created = 0;
        var backend = new WindowsUpdateBackend(() => { created++; return engine; });
        Assert.Equal(0, created);
        var version = Assert.IsType<Version>(await backend.CheckAsync());
        Assert.Equal(new Version(0, 1, 2), version);
        Assert.Throws<InvalidOperationException>(() => backend.ApplyAndRestart(version));
        int progress = 0;
        await backend.DownloadAsync(version, value => progress = value, CancellationToken.None);
        Assert.Equal(100, progress); Assert.Equal(0, engine.Applied);
        backend.ApplyAndRestart(version);
        Assert.Equal(1, engine.Applied);
    }
    [Theory]
    [InlineData("older")]
    [InlineData("same")]
    [InlineData("identity")]
    [InlineData("downgrade")]
    public async Task Backend_rejects_unsafe_offer(string fault)
    {
        var e = new Engine { Offer = new(new Version(0, 1, fault == "older" ? 0 : fault == "same" ? 1 : 2), fault == "identity" ? "OtherApp" : "YourTubeWindows", fault == "downgrade") };
        var b = new WindowsUpdateBackend(() => e);
        await Assert.ThrowsAsync<InvalidDataException>(() => b.CheckAsync());
    }
    [Fact]
    public async Task Cancelled_download_and_recheck_invalidate_ready_package()
    {
        var e = new Engine(); var b = new WindowsUpdateBackend(() => e);
        var v = (await b.CheckAsync())!;
        await b.DownloadAsync(v, _ => {}, CancellationToken.None);
        e.CancelDownload = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => b.DownloadAsync(v, _ => {}, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => b.ApplyAndRestart(v));
        e.CancelDownload = false;
        await b.DownloadAsync(v, _ => {}, CancellationToken.None);
        e.Offer = null; await b.CheckAsync();
        Assert.Throws<InvalidOperationException>(() => b.ApplyAndRestart(v));
    }
    [Fact]
    public void Production_options_prohibit_downgrade_and_disable_deltas()
    {
        var options = WindowsUpdateBackend.CreateOptions();
        Assert.False(options.AllowVersionDowngrade);
        Assert.Equal("win-x64", options.ExplicitChannel);
        Assert.Equal(-1, options.MaximumDeltasBeforeFallback);
    }
    [Fact]
    public async Task Target_no_longer_newer_cannot_apply()
    {
        var e = new Engine(); var b = new WindowsUpdateBackend(() => e);
        var v = (await b.CheckAsync())!; await b.DownloadAsync(v, _ => {}, CancellationToken.None);
        e.CurrentVersion = v;
        Assert.Throws<InvalidOperationException>(() => b.ApplyAndRestart(v));
    }
    internal sealed class Engine : IWindowsUpdateEngine
    {
        public bool IsInstalled => true;
        public Version CurrentVersion { get; set; } = new(0, 1, 1);
        public UpdateOffer? Offer = new(new Version(0, 1, 2), "YourTubeWindows");
        public int Applied;
        public bool CancelDownload { get; set; }
        public Task<UpdateOffer?> CheckAsync() => Task.FromResult(Offer);
        public Task DownloadAsync(Action<int> progress, CancellationToken token) { if (CancelDownload) throw new OperationCanceledException(); progress(100); return Task.CompletedTask; }
        public void ApplyAndRestart() => Applied++;
    }
}
