using KanbanFlow.Models;

namespace KanbanFlow.Tests;

/// <summary>
/// Round-trip tests for <see cref="EmbeddingCodec"/> (#221). This is the single source of truth for
/// the <see cref="MemoryNode.EmbeddingBlob"/> layout that #222/#223 depend on, so the pack/unpack
/// contract is pinned here: little-endian float32, exact byte length, exact value preservation.
/// </summary>
public sealed class EmbeddingCodecTests
{
    [Fact]
    public void PackUnpack_RoundTripsExactly()
    {
        var vector = new[] { 0f, 1f, -1f, 3.5f, 0.123456f, float.MaxValue, float.MinValue, -0.0f };

        var blob = EmbeddingCodec.Pack(vector);
        var restored = EmbeddingCodec.Unpack(blob);

        // float32 is preserved bit-for-bit through the codec (no precision loss, no reordering).
        Assert.Equal(vector, restored);
        // Byte-level verification catches -0.0f == 0.0f and NaN sign/payload differences.
        Assert.Equal(blob, EmbeddingCodec.Pack(restored));
        Assert.Equal(vector.Length * sizeof(float), blob.Length);
    }

    [Fact]
    public void Pack_IsLittleEndian()
    {
        // 1.0f == 0x3F800000; little-endian byte order is 00 00 80 3F.
        var blob = EmbeddingCodec.Pack([1.0f]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, blob);
    }

    [Fact]
    public void EmptyVector_RoundTripsToEmptyBlob()
    {
        var blob = EmbeddingCodec.Pack([]);
        Assert.Empty(blob);
        Assert.Empty(EmbeddingCodec.Unpack(blob));
    }

    [Fact]
    public void Unpack_BadLength_Throws()
    {
        // 5 bytes is not a whole number of float32 components.
        Assert.Throws<ArgumentException>(() => EmbeddingCodec.Unpack(new byte[5]));
    }

    [Fact]
    public void Pack_PreservesSpecialFloats()
    {
        var vector = new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.Epsilon };

        var restored = EmbeddingCodec.Unpack(EmbeddingCodec.Pack(vector));

        Assert.True(float.IsNaN(restored[0]));
        Assert.True(float.IsPositiveInfinity(restored[1]));
        Assert.True(float.IsNegativeInfinity(restored[2]));
        Assert.Equal(float.Epsilon, restored[3]);
    }
}
