using System.Buffers.Binary;
using System.Text;
namespace YourTube.Playback;

internal static class Mp4Header
{
    // An 8-byte box header, 24-byte version-0 payload, and one 12-byte reference.
    private const ulong MinimumIndexSize = 44;
    private static PlaybackException Invalid() => new("unsupported_media", "The media index is unsupported or invalid.");
    internal static Mp4Index Parse(byte[] bytes, ulong resourceSize, string? expectedSampleType = null) =>
        ParsePrefix(bytes, resourceSize, (ulong)bytes.Length, expectedSampleType) ?? throw Invalid();

    // Null means the fetched prefix ended before a potentially valid index. A known
    // malformed box or an index that cannot fit in the read budget is not retryable.
    internal static Mp4Index? ParsePrefix(byte[] bytes, ulong resourceSize, ulong prefixLimit, string? expectedSampleType)
    {
        prefixLimit = Math.Min(prefixLimit, resourceSize);
        bool ftyp = false, movie = false; ulong initEnd = 0;
        for (int p = 0; ;)
        {
            int available = bytes.Length - p;
            Mp4Index? Incomplete(ulong requiredEnd) => requiredEnd <= prefixLimit ? null : throw Invalid();
            if (available < 8) return Incomplete((ulong)p + MinimumIndexSize + (movie ? 0u : 8u));
            var type = Encoding.ASCII.GetString(bytes, p + 4, 4);
            if (type is "mdat" or "pssh" or "moof" or "sinf" or "encv" or "enca" ||
                type == "ftyp" && (ftyp || p != 0) || type == "moov" && (!ftyp || movie) ||
                type == "sidx" && (!ftyp || !movie) || p == 0 && type != "ftyp") throw Invalid();
            ulong requiredAfter = type == "sidx" ? 0 : MinimumIndexSize + (movie || type == "moov" ? 0u : 8u);
            if (available < 16 && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p)) == 1)
                return Incomplete((ulong)p + (type == "sidx" ? MinimumIndexSize + 8 : 16) + requiredAfter);
            var (size, header, _) = Box(bytes, p, bytes.Length);
            if (type == "ftyp" && size < header + 8 ||
                type == "sidx" && (ulong)size < MinimumIndexSize + (ulong)(header - 8) ||
                (ulong)p + (ulong)size + requiredAfter > prefixLimit) throw Invalid();
            if (size > available) return null;
            if (type == "ftyp") ftyp = true;
            if (type == "moov") { if (Inspect(bytes, p + header, p + size, 0, expectedSampleType) != 1) throw Invalid(); movie = true; initEnd = (ulong)(p + size - 1); }
            if (type == "sidx")
            {
                var s = bytes.AsSpan(p + header, size - header);
                if (s.Length < 24 || s[0] > 1 || s[1] != 0 || s[2] != 0 || s[3] != 0 || BinaryPrimitives.ReadUInt32BigEndian(s[8..]) == 0) throw Invalid();
                int prefix = s[0] == 0 ? 24 : 32;
                if (s.Length < prefix) throw Invalid();
                ulong firstOffset = s[0] == 0 ? BinaryPrimitives.ReadUInt32BigEndian(s[16..]) : BinaryPrimitives.ReadUInt64BigEndian(s[20..]);
                int count = BinaryPrimitives.ReadUInt16BigEndian(s[(prefix - 2)..]);
                if (count == 0 || count > 10000 || s.Length != prefix + count * 12) throw Invalid();
                ulong end = (ulong)(p + size);
                if (firstOffset > resourceSize || end > resourceSize - firstOffset) throw Invalid();
                end += firstOffset;
                for (int i = 0; i < count; i++)
                {
                    var r = s[(prefix + i * 12)..]; var n = BinaryPrimitives.ReadUInt32BigEndian(r);
                    if ((n & 0x80000000) != 0 || n == 0 || BinaryPrimitives.ReadUInt32BigEndian(r[4..]) == 0 || n > resourceSize || end > resourceSize - n) throw Invalid();
                    end += n;
                }
                return new(initEnd, (ulong)p, (ulong)(p + size - 1), BinaryPrimitives.ReadUInt32BigEndian(s[8..]), s[0] == 0 ? BinaryPrimitives.ReadUInt32BigEndian(s[12..]) : BinaryPrimitives.ReadUInt64BigEndian(s[12..]));
            }
            p += size;
        }
    }
    private static (int Size, int Header, string Type) Box(byte[] b, int p, int end)
    {
        if (end - p < 8) throw Invalid();
        ulong size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); int header = 8;
        if (size == 1) { if (end - p < 16) throw Invalid(); size = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p + 8)); header = 16; }
        if (size < (ulong)header || size > int.MaxValue) throw Invalid();
        return ((int)size, header, Encoding.ASCII.GetString(b, p + 4, 4));
    }
    private static int Inspect(byte[] b, int p, int end, int depth, string? expectedSampleType = null)
    {
        if (depth > 10) throw Invalid();
        int samples = 0;
        while (p < end)
        {
            var (size, header, type) = Box(b, p, end);
            if (size > end - p || type is "pssh" or "sinf" or "encv" or "enca") throw Invalid();
            int start = p + header;
            if (type is "trak" or "mdia" or "minf" or "stbl") samples += Inspect(b, start, p + size, depth + 1, expectedSampleType);
            if (type == "stsd")
            {
                if (size < header + 16 || BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(start + 4)) != 1) throw Invalid();
                var (sampleSize, sampleHeader, sampleType) = Box(b, start + 8, p + size);
                if (sampleType is not ("avc1" or "mp4a") || expectedSampleType is not null && sampleType != expectedSampleType || sampleSize != size - header - 8) throw Invalid();
                int fields = sampleType == "avc1" ? 78 : 28;
                if (sampleSize < sampleHeader + fields) throw Invalid();
                Inspect(b, start + 8 + sampleHeader + fields, p + size, depth + 1);
                samples++;
            }
            p += size;
        }
        return samples;
    }
}
