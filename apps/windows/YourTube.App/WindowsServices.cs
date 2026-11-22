using System.Runtime.InteropServices;
using Windows.Security.Credentials;
using YourTube.Core;

namespace YourTube.App;

internal sealed class CoreApi(YouTubeClient client) : IYouTubeApi
{
    public Task RecordWatchStartAsync(YouTubeSession session, YouTubeAccount account, string videoId, TimeSpan duration, TimeSpan position, CancellationToken cancellationToken) => client.RecordWatchStartAsync(session, account, videoId, duration, position, cancellationToken);
    public Task<DeviceAuthorization> BeginSignInAsync(string deviceId, CancellationToken cancellationToken) => client.BeginSignInAsync(deviceId, cancellationToken);
    public Task<YouTubeSession> CompleteSignInAsync(DeviceAuthorization authorization, CancellationToken cancellationToken) => client.CompleteSignInAsync(authorization, cancellationToken);
    public Task<YouTubeSession> RefreshSessionAsync(string refreshToken, CancellationToken cancellationToken) => client.RefreshSessionAsync(refreshToken, cancellationToken);
    public Task<IReadOnlyList<YouTubeAccount>> GetAccountsAsync(YouTubeSession session, CancellationToken cancellationToken) => client.GetAccountsAsync(session, cancellationToken);
    public Task<SubscriptionsPage> GetSubscriptionsAsync(YouTubeSession session, YouTubeAccount account, string? continuation, CancellationToken cancellationToken) => client.GetSubscriptionsAsync(session, account, continuation, cancellationToken);
    public Task<HistoryPage> GetHistoryAsync(YouTubeSession session, YouTubeAccount account, string? continuation, CancellationToken cancellationToken) => client.GetHistoryAsync(session, account, continuation, cancellationToken);
}

internal sealed class VaultTokenStore : ITokenStore
{
    private const string Resource = "EveryoneSoftware.YourTube.RefreshToken";
    private const string UserName = "device-session";
    private readonly PasswordVault vault = new();
    private PasswordCredential? Find()
    {
        try { return vault.Retrieve(Resource, UserName); }
        catch (COMException error) when (error.HResult == unchecked((int)0x80070490)) { return null; }
    }
    public string? Load()
    {
        var credential = Find();
        if (credential is null) return null;
        credential.RetrievePassword();
        return credential.Password;
    }
    public void Save(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new InvalidOperationException("Missing refresh token.");
        // PasswordVault replaces the exact resource/user pair. Never remove first:
        // an interrupted replacement must not intentionally discard a good login.
        vault.Add(new PasswordCredential(Resource, UserName, refreshToken));
    }
    public void Clear()
    {
        var credential = Find();
        if (credential is not null) vault.Remove(credential);
    }
}

internal static class DeviceIdentity
{
    public static string LoadOrCreate()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveryoneSoftware", "YourTube");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "device-id.txt");
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path), out var existing)) return existing.ToString("D");
        var id = Guid.NewGuid().ToString("D");
        File.WriteAllText(path, id);
        return id;
    }
}
