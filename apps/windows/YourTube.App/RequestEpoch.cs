namespace YourTube.App;

// Cross-platform state boundary, shared by the native controller and tests.
internal sealed class RequestEpoch
{
    private readonly object sync = new();
    private long generation;
    private CancellationTokenSource source = new();

    public (long Id, CancellationToken Token) Snapshot()
    {
        lock (sync) return (generation, source.Token);
    }

    public (long Id, CancellationToken Token) Begin()
    {
        lock (sync)
        {
            generation++;
            source.Cancel();
            source.Dispose();
            source = new CancellationTokenSource();
            return (generation, source.Token);
        }
    }

    // No awaits inside a commit. Vault writes and state updates share this lock
    // with invalidation, so a late token refresh cannot re-create a signed-out login.
    public bool Commit(long id, Action action)
    {
        lock (sync)
        {
            if (id != generation) return false;
            action();
            return true;
        }
    }
}
