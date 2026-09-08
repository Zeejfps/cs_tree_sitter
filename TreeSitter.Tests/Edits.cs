using System.Text;

using TreeSitter.Bindings;

namespace TreeSitter.Tests;

/// <summary>
/// Splices UTF-8 text and describes the splice as a <see cref="TSInputEdit"/> — the
/// bookkeeping a caller maintaining a tree over a buffer has to do for itself.
/// </summary>
/// <remarks>
/// Offsets are byte offsets throughout, and points are derived by walking the
/// bytes, because that is the arithmetic the real caller has to get right: a column
/// is bytes, so anything counting characters is wrong on the first accented letter.
/// Anchors are given as text rather than as numbers so a test says which edit it
/// makes instead of asserting against a hand-counted offset.
/// </remarks>
internal static class Edits
{
    /// <summary>
    /// Replaces the <paramref name="deletedBytes"/> bytes at
    /// <paramref name="startByte"/> with <paramref name="inserted"/>.
    /// </summary>
    internal static (byte[] NewSource, TSInputEdit Edit) Splice(
        string source, int startByte, int deletedBytes, string inserted)
    {
        var oldBytes = Encoding.UTF8.GetBytes(source);
        var insertedBytes = Encoding.UTF8.GetBytes(inserted);
        var oldEndByte = startByte + deletedBytes;

        var newBytes = new byte[oldBytes.Length - deletedBytes + insertedBytes.Length];
        oldBytes.AsSpan(0, startByte).CopyTo(newBytes);
        insertedBytes.CopyTo(newBytes.AsSpan(startByte));
        oldBytes.AsSpan(oldEndByte).CopyTo(newBytes.AsSpan(startByte + insertedBytes.Length));

        var edit = new TSInputEdit
        {
            StartByte = (uint)startByte,
            OldEndByte = (uint)oldEndByte,
            NewEndByte = (uint)(startByte + insertedBytes.Length),
            StartPoint = PointAt(oldBytes, startByte),
            OldEndPoint = PointAt(oldBytes, oldEndByte),
            NewEndPoint = PointAt(newBytes, startByte + insertedBytes.Length),
        };

        return (newBytes, edit);
    }

    internal static (byte[] NewSource, TSInputEdit Edit) InsertBefore(
        string source, string anchor, string inserted) =>
        Splice(source, ByteOffsetOf(source, anchor), 0, inserted);

    internal static (byte[] NewSource, TSInputEdit Edit) InsertAfter(
        string source, string anchor, string inserted) =>
        Splice(source, ByteOffsetOf(source, anchor) + Encoding.UTF8.GetByteCount(anchor), 0, inserted);

    internal static (byte[] NewSource, TSInputEdit Edit) Remove(string source, string snippet) =>
        ReplaceOne(source, snippet, string.Empty);

    internal static (byte[] NewSource, TSInputEdit Edit) ReplaceOne(
        string source, string snippet, string replacement) =>
        Splice(source, ByteOffsetOf(source, snippet), Encoding.UTF8.GetByteCount(snippet), replacement);

    /// <summary>Byte offset of the first occurrence of <paramref name="snippet"/>.</summary>
    internal static int ByteOffsetOf(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{snippet}' is not in this source.");
        return Encoding.UTF8.GetByteCount(source.AsSpan(0, index));
    }

    private static TSPoint PointAt(ReadOnlySpan<byte> utf8, int offset)
    {
        uint row = 0;
        var lineStart = 0;

        for (var i = 0; i < offset; i++)
        {
            if (utf8[i] == (byte)'\n')
            {
                row++;
                lineStart = i + 1;
            }
        }

        return new TSPoint { Row = row, Column = (uint)(offset - lineStart) };
    }
}
