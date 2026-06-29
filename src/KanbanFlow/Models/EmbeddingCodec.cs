using System.Buffers.Binary;

namespace KanbanFlow.Models;

/// <summary>
/// The single source of truth for how a semantic embedding vector is packed into the
/// <see cref="MemoryNode.EmbeddingBlob"/> column (#221) and read back out (#222/#223).
/// Vectors are stored as a tight little-endian array of IEEE-754 <c>float</c> (4 bytes
/// each), endianness-pinned with <see cref="BinaryPrimitives"/> so a blob written on any
/// host architecture round-trips identically everywhere. Keeping the encoding here means
/// the embedding writer (#222) and the vector search/retrieval (#223) never disagree on layout.
/// </summary>
public static class EmbeddingCodec
{
    /// <summary>Bytes per packed component (float32).</summary>
    public const int BytesPerFloat = sizeof(float);

    /// <summary>
    /// Pack a vector into a little-endian float32 byte blob suitable for
    /// <see cref="MemoryNode.EmbeddingBlob"/>. An empty vector packs to an empty (length-0) blob.
    /// </summary>
    /// <param name="vector">The embedding components.</param>
    /// <returns>A blob of exactly <c>vector.Length * 4</c> bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="vector"/> is <c>null</c>.</exception>
    public static byte[] Pack(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var blob = new byte[vector.Length * BytesPerFloat];
        var span = blob.AsSpan();
        for (var i = 0; i < vector.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * BytesPerFloat, BytesPerFloat), vector[i]);
        return blob;
    }

    /// <summary>
    /// Unpack a little-endian float32 blob (as produced by <see cref="Pack"/>) back into a vector.
    /// </summary>
    /// <param name="blob">The packed bytes; length must be a multiple of 4.</param>
    /// <returns>The decoded vector (empty when <paramref name="blob"/> is empty).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="blob"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="blob"/>'s length is not a multiple of 4.</exception>
    public static float[] Unpack(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length % BytesPerFloat != 0)
            throw new ArgumentException(
                $"Embedding blob length {blob.Length} is not a multiple of {BytesPerFloat}.", nameof(blob));

        var vector = new float[blob.Length / BytesPerFloat];
        var span = blob.AsSpan();
        for (var i = 0; i < vector.Length; i++)
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * BytesPerFloat, BytesPerFloat));
        return vector;
    }
}
