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
