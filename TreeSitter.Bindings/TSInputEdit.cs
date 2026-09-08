using System.Runtime.InteropServices;

namespace TreeSitter.Bindings;

/// <summary>
/// Mirrors <c>TSInputEdit</c>: one change to a document, given in byte offsets
/// <em>and</em> in row/column points.
/// </summary>
/// <remarks>
/// <para>
/// Both coordinate systems are asked for, but only the bytes decide anything on the
/// parse path here: <c>ts_parser_parse_string</c> is handed the whole new text and
/// derives every row and column from it, so the points reach only the heuristic
/// that decides how much of the old tree to re-lex. Fill them in correctly — the
/// header asks for them, and a reader that trusts them is entitled to — but a wrong
/// one does not show up as a wrong tree.
/// </para>
/// <para>
/// Start must not be past old end, in either system. Nothing here checks that: an
/// edit which lies about the change still parses, and hands back a plausible tree of
/// a file nobody has. <c>SyntaxTree.Reparse</c> is where the byte half of it gets
/// checked, because that is the layer that knows the buffers.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct TSInputEdit
{
    public uint StartByte;
    public uint OldEndByte;
    public uint NewEndByte;
    public TSPoint StartPoint;
    public TSPoint OldEndPoint;
    public TSPoint NewEndPoint;
}
