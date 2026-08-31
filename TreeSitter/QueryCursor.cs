using TreeSitter.Bindings;

namespace TreeSitter;

/// <summary>
/// One match, valid only for the duration of the callback it is handed to.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a <c>ref struct</c> for one reason: it cannot be stored.</b>
/// <c>TSQueryMatch.captures</c> points into a buffer the cursor reuses, and
/// <c>ts_query_cursor_next_match</c> overwrites it — so captures have to be read
/// before the loop advances. Every binding that hands out a match object documents
/// this and hopes. A ref struct cannot be assigned to a field, captured by a
/// lambda, boxed, or put in a list, so the compiler refuses the code that would
/// read a stale buffer instead of the runtime returning wrong nodes for it.
/// </para>
/// <para>
/// The cost is that iteration is a callback rather than an
/// <see cref="System.Collections.Generic.IEnumerable{T}"/>. That is the trade:
/// <c>foreach</c> over matches would mean either copying every capture array or
/// handing back exactly the object this type refuses to be.
/// </para>
/// </remarks>
public readonly ref struct QueryMatch
{
    private readonly SyntaxTree _tree;
    private readonly ReadOnlySpan<TSQueryCapture> _captures;

    internal QueryMatch(SyntaxTree tree, int patternIndex, ReadOnlySpan<TSQueryCapture> captures)
    {
        _tree = tree;
        _captures = captures;
        PatternIndex = patternIndex;
    }

    /// <summary>Which pattern of the query matched.</summary>
    /// <remarks>
    /// What a caller uses when one query holds patterns for several symbol kinds.
    /// Matches whose predicates failed never arrive here, so this indexes only
    /// patterns that actually held.
    /// </remarks>
    public int PatternIndex { get; }

    /// <summary>How many captures this match carries.</summary>
    /// <remarks>
    /// Not the query's capture count: a pattern's optional captures are absent from
    /// matches that did not bind them, so this varies match to match.
    /// </remarks>
    public int CaptureCount => _captures.Length;

    /// <summary>The capture id at a position, to compare against <see cref="Query.CaptureId"/>.</summary>
    public uint CaptureIdAt(int index) => _captures[index].Index;

    /// <summary>The captured node at a position, attached to the tree it came from.</summary>
    public Node NodeAt(int index) => new(_tree, _captures[index].Node);

    /// <summary>
    /// The node bound to <paramref name="captureId"/>, if this match bound it.
    /// </summary>
    /// <remarks>
    /// The intended per-match path: an integer scan over a handful of captures, with
    /// the name lookup already paid for at compile time. Returns
    /// <see langword="false"/> rather than throwing because an optional capture that
    /// did not bind is ordinary, not an error.
    /// </remarks>
    public bool TryGetNode(uint captureId, out Node node)
    {
        foreach (var capture in _captures)
        {
            if (capture.Index == captureId)
            {
                node = new Node(_tree, capture.Node);
                return true;
            }
        }

        node = default;
        return false;
    }
}

/// <summary>
/// Receives one match. Must not try to keep it — <see cref="QueryMatch"/> is a ref
/// struct, so it cannot.
/// </summary>
public delegate void QueryMatchHandler(QueryMatch match);

/// <summary>
/// The mutable per-run half of the query API: executes a <see cref="Query"/> over a
/// subtree and walks the matches.
/// </summary>
/// <remarks>
/// <para>
/// Reusable and worth reusing — a cursor owns the match buffer and the state stack
/// that matching needs, so one per worker beats one per file. Never share one
/// across threads; the query it runs is immutable and shareable, the cursor is not.
/// </para>
/// <para>
/// Note that a cursor carries settings between executions, which is why
/// <see cref="ForEachMatch(Query, Node, QueryMatchHandler)"/> resets the byte range
/// rather than leaving whatever the previous call set. A cursor silently still
/// scoped to the last declaration it was pointed at would return a plausible subset
/// of the matches for the next file.
/// </para>
/// </remarks>
public sealed class QueryCursor : IDisposable
{
    private readonly QueryCursorHandle _handle;

    public QueryCursor()
    {
        _handle = QueryCursorHandle.Create();
        if (_handle.IsInvalid)
        {
            throw new TreeSitterException("ts_query_cursor_new returned NULL, which means the allocator failed.");
        }
    }

    /// <summary>
    /// Runs <paramref name="query"/> over the subtree rooted at <paramref name="node"/>,
    /// calling <paramref name="handler"/> once per match.
    /// </summary>
    public void ForEachMatch(Query query, Node node, QueryMatchHandler handler) =>
        // uint.MaxValue rather than the file length: it means "no restriction", and
        // reaching for the length here would need the tree's size for no gain.
        Execute(query, node, 0, uint.MaxValue, handler);

    /// <summary>
    /// The same, restricted to a byte range — how a query runs over one declaration
    /// rather than a whole file, without reparsing or building a second tree.
    /// </summary>
    /// <remarks>
    /// The range is applied before execution, since it is read when matching starts
    /// and ignored afterwards. Offsets are byte offsets into the tree's source, so
    /// they compose directly with <see cref="Node.StartByte"/> and
    /// <see cref="Node.EndByte"/>.
    /// </remarks>
    public void ForEachMatch(Query query, Node node, uint startByte, uint endByte, QueryMatchHandler handler)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startByte, endByte);
        Execute(query, node, startByte, endByte, handler);
    }

    public void Dispose() => _handle.Dispose();

    private unsafe void Execute(Query query, Node node, uint startByte, uint endByte, QueryMatchHandler handler)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        var tree = node.Owner;
        if (tree.Language.Handle != query.Language.Handle)
        {
            // Node type ids are per-grammar. Running a query compiled for one
            // language over another language's tree does not fail -- it matches
            // whatever rules happen to share those ids, which is nothing anyone
            // wrote.
            throw new ArgumentException(
                "This query was compiled for a different grammar than the tree it is being run over.",
                nameof(query));
        }

        var raw = node.Raw;
        var cursor = _handle.DangerousGetHandle();

        if (!TS.ts_query_cursor_set_byte_range(cursor, startByte, endByte))
        {
            throw new ArgumentException($"tree-sitter rejected the byte range [{startByte}, {endByte}).");
        }

        TS.ts_query_cursor_exec(cursor, query.Handle, raw);

        while (TS.ts_query_cursor_next_match(cursor, out var match))
        {
            var captures = new ReadOnlySpan<TSQueryCapture>((void*)match.Captures, match.CaptureCount);

            // Predicates are evaluated here rather than by the handler, which is what
            // makes a failing one invisible: a match the query excluded never reaches
            // the callback at all. Both things an evaluation needs -- the captured
            // nodes and the source bytes behind them -- are in hand at this point and
            // nowhere else.
            if (!query.PredicatesHold(match.PatternIndex, tree, captures))
            {
                continue;
            }

            handler(new QueryMatch(tree, match.PatternIndex, captures));
        }

        // The cursor and query were passed as bare pointers, so nothing in those
        // calls told the GC their SafeHandles were still in use.
        GC.KeepAlive(_handle);
        query.KeepAlive();
        GC.KeepAlive(tree);
    }
}
