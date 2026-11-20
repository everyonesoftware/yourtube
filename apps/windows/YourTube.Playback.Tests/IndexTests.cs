using System.Buffers.Binary;
using System.Text;
using YourTube.Playback;
using Xunit;

namespace YourTube.Playback.Tests;
public sealed class IndexTests
{
    internal static byte[] Box(string type, byte[] payload)
    {
        byte[] box = new byte[payload.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4); payload.CopyTo(box, 8); return box;
    }
    internal static byte[] Header(bool audio = false)
    {
        byte[] index = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(8), 1000);
        BinaryPrimitives.WriteUInt16BigEndian(index.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(24), 100);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(28), 1000);
        var sample = Box("stsd", [0,0,0,0,0,0,0,1,..Box(audio ? "mp4a" : "avc1", new byte[audio ? 28 : 78])]);
        var movie = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", sample)))));
        return [..Box("ftyp", new byte[8]), ..movie, ..Box("sidx", index)];
    }
    [Theory]
    [InlineData("pssh")][InlineData("moof")]
    public void RejectsEncryptedOrFragmentedInitialization(string type)
    {
        byte[] bytes = [..Box(type, []), ..Header()];
        Assert.Throws<PlaybackException>(() => Mp4Header.Parse(bytes, (ulong)bytes.Length + 100));
    }
    [Fact] public void RejectsMovieWithoutSampleDescription()
    {
        var valid = Header(); var index = valid[^44..]; byte[] bytes = [..Box("ftyp", new byte[8]), ..Box("moov", []), ..index];
        Assert.Throws<PlaybackException>(() => Mp4Header.Parse(bytes, (ulong)bytes.Length + 100));
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(7)][InlineData(16)][InlineData(24)][InlineData(100)]
    public void RejectsTruncatedHeaders(int length) => Assert.Throws<PlaybackException>(() => Mp4Header.Parse(Header()[..length], 1000));
    [Fact] public void RejectsOutOfResourceReferences() => Assert.Throws<PlaybackException>(() => Mp4Header.Parse(Header(), (ulong)Header().Length + 99));
    [Fact] public void RejectsMislabeledCodec()
    {
        var bytes = Header(); Assert.Throws<PlaybackException>(() => Mp4Header.Parse(bytes, (ulong)bytes.Length + 100, "mp4a"));
    }
    [Fact] public void ParsesVersionOneTimingAndOffsets()
    {
        var original = Header(); byte[] body = new byte[44]; body[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8), 48000);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(12), 4294967297);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(30), 1);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(32), 100);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(36), 48000);
        byte[] bytes = [..original[..^44], ..Box("sidx", body)];
        var index = Mp4Header.Parse(bytes, (ulong)bytes.Length + 100);
        Assert.Equal(48000u, index.Timescale); Assert.Equal(4294967297ul, index.PresentationTimeOffset);
    }
    [Fact] public void ParsesActualBoxBoundaries()
    {
        var bytes = Header(); var index = Mp4Header.Parse(bytes, (ulong)bytes.Length + 100);
        Assert.Equal((ulong)bytes.Length - 45, index.InitEnd);
        Assert.Equal((ulong)bytes.Length - 44, index.IndexStart);
        Assert.Equal((ulong)bytes.Length - 1, index.IndexEnd);
        Assert.Equal(1000u, index.Timescale);
    }
}
