using System.Text;

using TreeSitter.Bindings;

namespace TreeSitter;

/// <summary>
/// A parsed file: the native tree, <em>and the UTF-8 bytes it was parsed from</em>.
/// </summary>
/// <remarks>
/// <para>
/// The two are one object because they are one lifetime. A <see cref="TSNode"/>
/// carries byte offsets and nothing else — tree-sitter never sees the text again
/// after <c>ts_parser_parse_string</c> returns — so a tree without its buffer can
/// report where a symbol is but not what it is called. Keeping the buffer beside
/// the tree is what makes <see cref="Node.Text"/> a property rather than something
/// every caller reimplements with a byte array it hopes is the right one.
/// </para>
/// <para>
/// The buffer is private and copied at parse time, so nothing outside can mutate
/// or shorten it underneath a node. The only way to read source text is through a
/// <see cref="Node"/>, which holds its tree and refuses once that tree is disposed
/// — the alternative, handing out spans over a shared array, puts the invariant
/// back in the caller's hands, and that invariant is the one this layer exists to
/// enforce.
/// </para>
/// <para>
/// Not thread-safe against its own disposal: concurrent reads of a live tree are
/// fine (tree-sitter's node API is const), disposing one while another thread walks
/// it is not, and no amount of checking here can close that window.
/// </para>
/// </remarks>
public sealed class SyntaxTree : IDisposable
{
    private readonly TreeHandle _handle;
    private readonly byte[] _source;

    internal SyntaxTree(TreeHandle handle, byte[] source, Language language)
    {
        _handle = handle;
        _source = source;
        Language = language;
    }

    /// <summary>The grammar this tree was parsed with.</summary>
    /// <remarks>
    /// Carried so a <see cref="Query"/> compiled for one language can refuse to run
    /// over a tree from another. Node type ids are per-grammar, so that mistake does
    /// not fail — it silently matches the wrong rules.
    /// </remarks>
    public Language Language { get; }

    /// <summary>The whole file, as the parser saw it.</summary>
    /// <exception cref="ObjectDisposedException">The tree has been disposed.</exception>
    public ReadOnlySpan<byte> Source
    {
        get
        {
            EnsureAlive();
            return _source;
        }
    }

    /// <summary>The node covering the whole file.</summary>
    /// <exception cref="ObjectDisposedException">The tree has been disposed.</exception>
    public Node RootNode
    {
        get
        {
            EnsureAlive();
            var root = TS.ts_tree_root_node(_handle.DangerousGetHandle());
            GC.KeepAlive(_handle);
            return new Node(this, root);
        }
    }

    /// <summary>
    /// Applies one edit and re-parses, reusing this tree for the parts the edit did
    /// not touch. Returns the tree of the new text; this one is consumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One call, because the two halves are not separately safe.</b> Editing a
    /// tree moves its spans onto text this object does not have — its buffer is
    /// still the old file — so an <c>Edit</c> a caller could reach would leave
    /// <see cref="Node.Text"/> reading the wrong substring, or refusing outright.
    /// Taking the edit and the bytes together means the tree and the buffer it
    /// indexes are never apart for longer than this method, and there is no way to
    /// hand a tree a buffer it was not parsed from.
    /// </para>
    /// <para>
    /// <paramref name="edit"/> has to describe the difference between this tree's
    /// source and <paramref name="newUtf8Source"/> exactly. The offsets are checked
    /// against the two buffers for the contradictions that are cheap to spot — a
    /// start past an end, an end past the buffer it indexes — but an edit can be
    /// well formed and still describe the wrong change, and that one does not throw:
    /// it returns a tree that is quietly wrong about a file nobody has. Callers
    /// maintaining a tree over a buffer they do not fully trust should compare the
    /// new root's extent against the buffer length and fall back to a fresh parse,
    /// which is cheap.
    /// </para>
    /// <para>
    /// Only the byte offsets are checked, because only the byte offsets are load
    /// bearing here: the parser walks <paramref name="newUtf8Source"/> and derives
    /// every row and column from the text, so the points on <paramref name="edit"/>
    /// do not change the tree it returns. Supply them correctly anyway — they are
    /// what the API asks for — but do not expect a wrong one to be caught.
    /// </para>
    /// <para>
    /// The old tree is released whatever happens, including when the re-parse
    /// throws, so there is no path that leaves an edited tree observable. Refusals
    /// that happen before the edit — a disposed object, the wrong grammar — leave
    /// this tree untouched and still usable.
    /// </para>
    /// </remarks>
    /// <param name="parser">
    /// Must be on the same grammar as this tree. Node type ids are per-grammar, so
    /// the wrong parser does not fail, it reparses into another language's rules.
    /// </param>
    /// <exception cref="ObjectDisposedException">The tree or the parser has been disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="parser"/> is on a different grammar.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="edit"/> contradicts itself or the buffers it describes.
    /// </exception>
    public SyntaxTree Reparse(Parser parser, in TSInputEdit edit, ReadOnlySpan<byte> newUtf8Source)
    {
        ArgumentNullException.ThrowIfNull(parser);
        EnsureAlive();
        parser.EnsureAlive();

        if (parser.Language.Handle != Language.Handle)
        {
            throw new ArgumentException(
                "This parser is on a different grammar than the tree it is being asked to re-parse.",
                nameof(parser));
        }

        ThrowIfEditIsImpossible(in edit, _source.Length, newUtf8Source.Length);

        try
        {
            TS.ts_tree_edit(_handle.DangerousGetHandle(), in edit);
            GC.KeepAlive(_handle);

            return parser.ParseWith(_handle, newUtf8Source);
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>
    /// Rejects the edits that cannot describe any pair of buffers, before one of
    /// them reaches a tree it would silently corrupt.
    /// </summary>
    /// <remarks>
    /// These are the transpositions and off-by-ones an offset mapping produces
    /// while it is being written, and they are the only errors in an edit that can
    /// be caught from here — a plausible edit that is merely wrong is
    /// indistinguishable from a right one without re-parsing to compare, which is
    /// the thing the caller came here to avoid.
    /// </remarks>
    private static void ThrowIfEditIsImpossible(in TSInputEdit edit, int oldLength, int newLength)
    {
        if (edit.StartByte > edit.OldEndByte)
        {
            throw new ArgumentOutOfRangeException(
                nameof(edit),
                $"The edit starts at byte {edit.StartByte}, past the byte {edit.OldEndByte} it says it ends at in the old text.");
        }

        if (edit.StartByte > edit.NewEndByte)
        {
            throw new ArgumentOutOfRangeException(
                nameof(edit),
                $"The edit starts at byte {edit.StartByte}, past the byte {edit.NewEndByte} it says it ends at in the new text.");
        }

        if (edit.OldEndByte > (uint)oldLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(edit),
                $"The edit ends at byte {edit.OldEndByte} of the old text, which is {oldLength} bytes long.");
        }

        if (edit.NewEndByte > (uint)newLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(edit),
                $"The edit ends at byte {edit.NewEndByte} of the new text, which is {newLength} bytes long.");
        }
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// The disposed check every <see cref="Node"/> read runs first.
    /// </summary>
    /// <remarks>
    /// Without it, a node read after <see cref="Dispose"/> is a use-after-free on
    /// memory tree-sitter has returned to the allocator: it does not fault, it
    /// returns plausible garbage. An exception is the only outcome that stays
    /// diagnosable.
    /// </remarks>
    internal void EnsureAlive() => ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

    /// <summary>Decodes one node's span. Callers reach this through <see cref="Node.Text"/>.</summary>
    internal string TextOf(uint startByte, uint endByte)
    {
        EnsureAlive();
        return Encoding.UTF8.GetString(Utf8TextOf(startByte, endByte));
    }

    /// <summary>
    /// The undecoded bytes of one node's span, for callers that want to compare
    /// rather than materialise — which is what a predicate evaluator's
    /// <c>#eq?</c> will want.
    /// </summary>
    internal ReadOnlySpan<byte> Utf8TextOf(uint startByte, uint endByte)
    {
        EnsureAlive();

        // Cannot happen for a node from this tree, since a parse never reports a
        // span past the buffer it was given. It is checked because the failure it
        // guards against -- a node paired with the wrong buffer -- would otherwise
        // read adjacent heap or silently truncate, and this is the one place with
        // enough context to say so.
        if (endByte > (uint)_source.Length || startByte > endByte)
        {
            throw new TreeSitterException(
                $"Node span [{startByte}, {endByte}) lies outside the {_source.Length}-byte source it was parsed from.");
        }

        return _source.AsSpan((int)startByte, (int)(endByte - startByte));
    }
}
