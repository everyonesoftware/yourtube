namespace YourTube.App;
internal static class BrowserPolicy
{
    public static bool IsActivationUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        return ((host is "google.com" or "www.google.com") && path == "/device") ||
               ((host is "youtube.com" or "www.youtube.com") && path == "/activate");
    }

    public static Uri? VideoUri(string id) => id.Length == 11 &&
        id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')
            ? new Uri("https://www.youtube.com/watch?v=" + id) : null;
}
