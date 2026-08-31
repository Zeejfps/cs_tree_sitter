using System.Runtime.InteropServices;
using System.Text;

using TreeSitter.Bindings;

namespace TreeSitter;

/// <summary>
/// One node of a <see cref="SyntaxTree"/>: a grammar rule name, a span, and a way
/// back to the bytes underneath it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A readonly struct, carrying its tree.</b> The C type is 32 bytes passed by
/// value and a walk produces them by the hundred thousand — a class per node would
/// make extraction an allocation benchmark, and boxing every child of every
/// declaration is exactly the cost this index cannot afford. Adding the tree
/// reference costs one more field and buys the two things a bare
/// <see cref="TSNode"/> cannot do: read its own text, and refuse after its tree is
/// gone. <c>readonly</c> because a node is a view — nothing about it is mutable,
/// and defensive copies of a 40-byte struct in property getters are not free.
/// </para>
/// <para>
/// <b>Every read goes through <c>Raw</c>, and every native call is followed by
/// <c>GC.KeepAlive</c>.</b> The node is passed to tree-sitter by value, so nothing
/// in the call signature tells the GC that the tree still matters; if the last
/// managed reference to the tree dies at the argument load, its
/// <see cref="System.Runtime.InteropServices.SafeHandle"/> can be finalised
/// <em>during</em> the call and the callee walks freed subtrees. The keep-alives
/// are cheap — they emit no instructions, only liveness — and the alternative is a
/// crash that reproduces once a week under load.
/// </para>
/// <para>
/// Lookups that can find nothing return <c>Node?</c> rather than tree-sitter's null
/// node. The null node is a real <see cref="TSNode"/> with a null <c>id</c> that
/// answers every call — <c>ts_node_type</c> on one returns <c>"NULL"</c> — so a
/// caller who forgets to check gets plausible answers about a node that does not
/// exist. <see cref="Nullable{T}"/> makes forgetting a compile error and allocates
/// nothing.
/// </para>
/// </remarks>
public readonly struct Node
{
    private readonly SyntaxTree? _tree;
    private readonly TSNode _node;

    internal Node(SyntaxTree tree, TSNode node)
    {
        _tree = tree;
        _node = node;
    }

    /// <summary>The grammar rule name — <c>method_declaration</c>, <c>identifier</c>.</summary>
    /// <remarks>
    /// The pointer is into the grammar's static string table and is never freed,
    /// which is why the raw binding returns <see cref="nint"/>: letting the
    /// marshaller produce a <see cref="string"/> would have it free memory the
    /// grammar owns.
    /// </remarks>
    public string Type
    {
        get
        {
            var pointer = TS.ts_node_type(Raw);
            GC.KeepAlive(_tree);
            return Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
        }
    }

    /// <summary>
    /// The grammar rule name, undecoded.
    /// </summary>
    /// <remarks>
    /// The same string <see cref="Type"/> returns, without the allocation. For a walk
    /// that only feeds rule names to a comparison or a hash, where one string per node
    /// over a whole repository is the allocation that shows up in a profile. The bytes
    /// live in the grammar's static table and outlive everything here, so unlike
    /// <see cref="Utf8Text"/> this span does not alias a buffer that can be freed.
    /// </remarks>
    public unsafe ReadOnlySpan<byte> Utf8Type
    {
        get
        {
            var pointer = TS.ts_node_type(Raw);
            GC.KeepAlive(_tree);
            return pointer == 0
                ? default
                : MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)pointer);
        }
    }

    /// <summary>Byte offset of the node's first byte, into <see cref="SyntaxTree.Source"/>.</summary>
    public uint StartByte
    {
        get
        {
            var value = TS.ts_node_start_byte(Raw);
            GC.KeepAlive(_tree);
            return value;
        }
    }

    /// <summary>Byte offset one past the node's last byte.</summary>
    public uint EndByte
    {
        get
        {
            var value = TS.ts_node_end_byte(Raw);
            GC.KeepAlive(_tree);
            return value;
        }
    }

    /// <summary>Zero-based row and byte column of the node's first byte.</summary>
    /// <remarks>
    /// The column is bytes, not characters — see <see cref="TSPoint"/>. Anything
    /// that renders a caret, or reports a column to an editor, has to convert.
    /// </remarks>
    public TSPoint StartPoint
    {
        get
        {
            var value = TS.ts_node_start_point(Raw);
            GC.KeepAlive(_tree);
            return value;
        }
    }

    /// <summary>Zero-based row and byte column one past the node's last byte.</summary>
    public TSPoint EndPoint
    {
        get
        {
            var value = TS.ts_node_end_point(Raw);
            GC.KeepAlive(_tree);
            return value;
        }
    }

    /// <summary>Whether this node has a grammar rule name, rather than being punctuation or a keyword.</summary>
    public bool IsNamed
    {
        get
        {
            var value = TS.ts_node_is_named(Raw);
            GC.KeepAlive(_tree);
            return value;
        }
    }

    /// <summary>
    /// Whether a syntax error lies anywhere in this subtree.
    /// </summary>
    /// <remarks>
    /// Never a reason to discard a file. tree-sitter recovers, and the declarations
    /// outside the damaged region still carry correct spans; this is worth recording
    /// so a half-written file can be reindexed rather than trusted.
    /// </remarks>
    public bool HasError
    {
        get
        {
            var value = TS.ts_node_has_error(Raw);
            GC.KeepAlive(_tree);
            return value;
        }
    }

    /// <summary>The source text this node spans.</summary>
    /// <remarks>
    /// Decoded from the tree's own copy of the file, so it is correct for the tree's
    /// whole life and impossible after that — which is the entire reason
    /// <see cref="SyntaxTree"/> owns the bytes.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The owning tree has been disposed.</exception>
    public string Text
    {
        get
        {
            var tree = Owner;
            var start = StartByte;
            var end = EndByte;
            return tree.TextOf(start, end);
        }
    }

    /// <summary>
    /// The node's bytes, undecoded.
    /// </summary>
    /// <remarks>
    /// For comparisons that do not need a string: matching a node against a keyword,
    /// or the ordinal comparison <c>#eq?</c> and <c>#any-of?</c> do per match.
    /// Valid only while the owning tree is alive, and only as long as the returned
    /// span is not stored — it aliases the tree's buffer rather than copying it.
    /// </remarks>
    public ReadOnlySpan<byte> Utf8Text
    {
        get
        {
            var tree = Owner;
            var start = StartByte;
            var end = EndByte;
            return tree.Utf8TextOf(start, end);
        }
    }

    /// <summary>Every child, punctuation and keywords included.</summary>
    public NodeChildren Children => new(this, named: false);

    /// <summary>
    /// Only the children with a grammar rule name.
    /// </summary>
    /// <remarks>
    /// Exactly <see cref="Children"/> filtered by <see cref="IsNamed"/> — the two
    /// describe one tree, which is what lets a walk take a field off one node and
    /// scan the children of the next without seeing different shapes depending on
    /// how it arrived.
    /// </remarks>
    public NodeChildren NamedChildren => new(this, named: true);

    /// <summary>The enclosing node, or <see langword="null"/> at the root.</summary>
    public Node? Parent => Wrap(TS.ts_node_parent(Raw));

    /// <summary>
    /// The preceding named sibling, or <see langword="null"/> if this is the first.
    /// </summary>
    /// <remarks>
    /// How docstrings are found: a <c>///</c> or <c>/** */</c> block is not part of
    /// the declaration it documents in any grammar bundled here, but a separate
    /// <c>comment</c> node in front of it. Note that "is there a docstring" is a
    /// type check on the result, not a null check — what precedes a second method is
    /// usually the first method.
    /// </remarks>
    public Node? PreviousNamedSibling => Wrap(TS.ts_node_prev_named_sibling(Raw));

    /// <summary>
    /// The child under a grammar field — <c>name</c>, <c>body</c>,
    /// <c>parameters</c> — or <see langword="null"/> if this node has no such field.
    /// </summary>
    /// <remarks>
    /// The way to read a declaration's identifier without hardcoding child
    /// positions, which differ between grammars and change between grammar versions.
    /// </remarks>
    /// <param name="utf8FieldName">
    /// UTF-8 bytes, written as a literal: <c>"name"u8</c>. The overload taking a
    /// <see cref="string"/> encodes on every call; this one does not.
    /// </param>
    public unsafe Node? ChildByFieldName(ReadOnlySpan<byte> utf8FieldName)
    {
        var raw = Raw;
        fixed (byte* name = utf8FieldName)
        {
            // A zero-length field name is meaningless, and pins to null; there is no
            // field it could match, so answer without crossing the boundary.
            if (name is null)
            {
                return null;
            }

            var child = TS.ts_node_child_by_field_name(raw, name, (uint)utf8FieldName.Length);
            GC.KeepAlive(_tree);
            return Wrap(child);
        }
    }

    /// <inheritdoc cref="ChildByFieldName(ReadOnlySpan{byte})"/>
    public Node? ChildByFieldName(string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        // Field names are grammar identifiers -- short by construction -- so the
        // common case stays off the heap.
        var length = Encoding.UTF8.GetByteCount(fieldName);
        Span<byte> buffer = length <= 64 ? stackalloc byte[64] : new byte[length];
        Encoding.UTF8.GetBytes(fieldName, buffer);
        return ChildByFieldName(buffer[..length]);
    }

    /// <summary>
    /// The raw node, for the few places that still need it: executing a query
    /// cursor, and building a <see cref="Node"/> for a capture.
    /// </summary>
    /// <remarks>
    /// Refuses a node whose tree has been disposed, and a default-constructed one,
    /// so every path into tree-sitter is guarded in one place rather than fourteen.
    /// </remarks>
    internal TSNode Raw
    {
        get
        {
            Owner.EnsureAlive();
            return _node;
        }
    }

    /// <summary>The tree this node is a view into.</summary>
    internal SyntaxTree Owner =>
        _tree ?? throw new InvalidOperationException(
            "This Node is default-constructed and belongs to no tree. Nodes come from " +
            "SyntaxTree.RootNode and the lookups on an existing node.");

    /// <summary>
    /// Turns tree-sitter's null node into <see langword="null"/>, and everything
    /// else into a node of this tree.
    /// </summary>
    private Node? Wrap(TSNode node)
    {
        var isNull = TS.ts_node_is_null(node);
        GC.KeepAlive(_tree);
        return isNull ? null : new Node(Owner, node);
    }

    /// <summary>
    /// A node's children as an indexable, allocation-free sequence — either all of
    /// them or only the named ones.
    /// </summary>
    /// <remarks>
    /// A struct with a struct enumerator, and no <see cref="System.Collections.Generic.IEnumerable{T}"/>
    /// in sight: <c>foreach</c> binds to <see cref="GetEnumerator"/> by shape, so
    /// iterating costs nothing, while implementing the interface would box the
    /// enumerator on every loop. Nothing here reflects or generates code, so it
    /// survives NativeAOT.
    /// </remarks>
    public readonly struct NodeChildren
    {
        private readonly Node _parent;
        private readonly bool _named;

        internal NodeChildren(Node parent, bool named)
        {
            _parent = parent;
            _named = named;
        }

        public int Count
        {
            get
            {
                var raw = _parent.Raw;
                var count = _named ? TS.ts_node_named_child_count(raw) : TS.ts_node_child_count(raw);
                GC.KeepAlive(_parent.Owner);
                return (int)count;
            }
        }

        /// <exception cref="ArgumentOutOfRangeException">
        /// Past the end. tree-sitter would hand back its null node instead, which
        /// answers every subsequent call and reports nothing wrong.
        /// </exception>
        public Node this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
                return At(index);
            }
        }

        /// <summary>
        /// The unchecked read behind the indexer, so the enumerator does not pay for
        /// a second <see cref="Count"/> — a native call — on every step.
        /// </summary>
        private Node At(int index)
        {
            var raw = _parent.Raw;
            var child = _named
                ? TS.ts_node_named_child(raw, (uint)index)
                : TS.ts_node_child(raw, (uint)index);
            GC.KeepAlive(_parent.Owner);
            return new Node(_parent.Owner, child);
        }

        public Enumerator GetEnumerator() => new(this);

        public struct Enumerator
        {
            private readonly NodeChildren _children;
            private readonly int _count;
            private int _index;

            internal Enumerator(NodeChildren children)
            {
                _children = children;
                _count = children.Count;
                _index = -1;
            }

            public readonly Node Current => _children[_index];

            public bool MoveNext() => ++_index < _count;
        }
    }
}
