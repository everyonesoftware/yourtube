namespace YourTube.Core;

public sealed class DeviceAuthorization(string userCode, Uri verificationUri, DateTimeOffset expiresAt, int intervalSeconds, string deviceCode)
{
    public string UserCode { get; } = userCode;
    public Uri VerificationUri { get; } = verificationUri;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public int IntervalSeconds { get; } = intervalSeconds;
    public string DeviceCode { get; } = deviceCode;
    public override string ToString() => "DeviceAuthorization [redacted]";
}

public sealed class YouTubeSession(string accessToken, string refreshToken, DateTimeOffset expiresAt)
{
    public string AccessToken { get; } = accessToken;
    public string RefreshToken { get; } = refreshToken;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public override string ToString() => "YouTubeSession [redacted]";
}

public sealed record YouTubeAccount(string Name, string? Handle, string? PageId)
{
    public Uri? AvatarUri { get; init; }

    public override string ToString() => $"YouTubeAccount {{ Name = {Name}, Handle = {Handle}, PageId = {PageId} }}";
}
public sealed record HistoryVideo(string VideoId, string Title, string Channel, string Duration, int? PercentWatched);
public sealed record HistoryPage(IReadOnlyList<HistoryVideo> Videos, string? Continuation, string? Message);
public sealed class YouTubeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
