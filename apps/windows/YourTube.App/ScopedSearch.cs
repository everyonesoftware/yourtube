using YourTube.Core;

namespace YourTube.App;

// UI-thread state. Home is remote; private views only project their loaded snapshot.
internal sealed class ScopedSearch(Func<string, CancellationToken, Task<IReadOnlyList<HistoryVideo>>> findPublic) : IDisposable
{
    private object? owner;
    private BrowseView view;
    private IReadOnlyList<HistoryVideo> source = [];
    private CancellationTokenSource? pending;
    public string Query { get; private set; } = "";
    public bool Active => Query.Length != 0;
    public bool Busy => pending is not null;
    public bool Failed { get; private set; }
    public IReadOnlyList<HistoryVideo> Videos { get; private set; } = [];

    public bool Bind(object? identity, BrowseView scope, IReadOnlyList<HistoryVideo> snapshot)
    {
        bool changed = !ReferenceEquals(owner, identity) || view != scope;
        if (changed) Clear();
        owner = identity;
        view = scope;
        if (!ReferenceEquals(source, snapshot))
        {
            source = snapshot;
            if (Active && view != BrowseView.Home) Project();
        }
        return changed;
    }

    public async Task SubmitAsync(string text)
    {
        if (owner is null || view == BrowseView.Settings) return;
        Clear();
        Query = text.Trim();
        if (!Active) return;
        if (view != BrowseView.Home) { Project(); return; }
        using var request = new CancellationTokenSource();
        pending = request;
        try
        {
            var results = await findPublic(Query, request.Token);
            if (ReferenceEquals(request, pending)) Videos = results;
        }
        catch (Exception)
        {
            if (ReferenceEquals(request, pending)) Failed = true;
        }
        finally
        {
            if (ReferenceEquals(request, pending)) pending = null;
        }
    }

    private void Project()
    {
        var words = Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = source.Where(video => words.All(word => video.Title.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            video.Channel.Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
        // A feed continuation with no new matches must not replace the playback queue snapshot.
        if (!Videos.SequenceEqual(matches, ReferenceEqualityComparer.Instance)) Videos = matches;
    }

    public void Clear()
    {
        var obsolete = pending;
        pending = null;
        Query = "";
        Videos = [];
        Failed = false;
        obsolete?.Cancel();
    }

    public void Dispose() { Clear(); owner = null; source = []; }
}
