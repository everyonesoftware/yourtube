using System.Buffers.Binary;
using System.Net;

namespace YourTube.Playback;

internal static class StoryboardSheet
{
    internal const int MaximumBytes = 2 * 1024 * 1024;

    internal static async Task<byte[]?> ReadAsync(HttpMessageInvoker transport, Storyboard storyboard, int sheet, CancellationToken token)
    {
        if (storyboard.SheetUri(sheet) is not { } uri) return null;
        token.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("image/jpeg");
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var response = await transport.SendAsync(request, token);
        var headers = response.Content.Headers;
        if (response.StatusCode != HttpStatusCode.OK || headers.ContentEncoding.Count != 0 ||
            !string.Equals(headers.ContentType?.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ||
            headers.ContentLength is < 4 or > MaximumBytes) return null;
        using var buffer = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaximumBytes + 1 - buffer.Length)), token);
            token.ThrowIfCancellationRequested();
            if (read == 0) break;
            if (buffer.Length + read > MaximumBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        if (headers.ContentLength is long declared && declared != buffer.Length) return null;
        return HasBoundedJpegFrame(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), storyboard) ? buffer.ToArray() : null;
    }

    // This is a format/dimension gate, not a replacement for the image decoder.
    private static bool HasBoundedJpegFrame(ReadOnlySpan<byte> bytes, Storyboard storyboard)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9) return false;
        for (int offset = 2; offset < bytes.Length - 2;)
        {
            if (bytes[offset++] != 0xff) return false;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset > bytes.Length - 3) return false;
            byte marker = bytes[offset++];
            if (marker is 0 or 1 or >= 0xd0 and <= 0xda) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || length > bytes.Length - offset - 2) return false;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (marker is not (0xc0 or 0xc1 or 0xc2) || length < 11 || bytes[offset + 2] != 8) return false;
                int height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]);
                return width > 0 && width <= storyboard.MaximumSheetWidth && height > 0 && height <= storyboard.MaximumSheetHeight;
            }
            offset += length;
        }
        return false;
    }
}
