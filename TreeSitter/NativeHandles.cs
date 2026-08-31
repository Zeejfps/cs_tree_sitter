using Microsoft.Win32.SafeHandles;

using TreeSitter.Bindings;

namespace TreeSitter;

// The four native lifetimes, one SafeHandle each: parser, tree, query, query
// cursor.
//
// They are internal on purpose. A public SafeHandle is an invitation to
// DangerousGetHandle from outside the assembly, which reintroduces exactly the raw
// ownership this layer exists to remove; callers get Parser, SyntaxTree, Query and
// QueryCursor instead, and TS stays public for anyone who genuinely wants the raw
// API.
//
// There is no finalizer-order hazard between these, which is a property of
// tree-sitter rather than of anything arranged here, and was checked rather than
// assumed: ts_tree_new takes a reference on the root subtree and on the language,
// and ts_tree_delete releases both through a SubtreePool it creates for itself --
// the parser's pool is never touched. A tree is therefore independent of the
// parser that produced it the moment ts_parser_parse_string returns, and the
// finalizer queue may run the two in either order. The same holds for a cursor
// against the query and tree it last ran over: between calls it references
// neither.
//
// The one ordering rule that does exist is not about handles at all. A TSNode
// points into its tree's memory and into the UTF-8 buffer the tree was parsed
// from, so reading one after either is gone is a use-after-free. That is why Node
// carries its SyntaxTree and checks it, instead of being a bare struct.

/// <summary>Owns a <c>TSParser *</c>.</summary>
internal sealed class ParserHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    internal static ParserHandle Create()
    {
        var handle = new ParserHandle();
        handle.SetHandle(TS.ts_parser_new());
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        TS.ts_parser_delete(handle);
        return true;
    }
}

/// <summary>Owns a <c>TSTree *</c>.</summary>
internal sealed class TreeHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    internal static TreeHandle Adopt(nint tree)
    {
        var handle = new TreeHandle();
        handle.SetHandle(tree);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        TS.ts_tree_delete(handle);
        return true;
    }
}

/// <summary>Owns a <c>TSQuery *</c>.</summary>
/// <remarks>
/// A query outlives the trees it is run against and is read-only once compiled, so
/// one instance is shared across every file of a language. The mutable per-run
/// state is entirely in <see cref="QueryCursorHandle"/>.
/// </remarks>
internal sealed class QueryHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    internal static QueryHandle Adopt(nint query)
    {
        var handle = new QueryHandle();
        handle.SetHandle(query);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        TS.ts_query_delete(handle);
        return true;
    }
}

/// <summary>Owns a <c>TSQueryCursor *</c>.</summary>
/// <remarks>
/// Holds the match buffer that <c>ts_query_cursor_next_match</c> overwrites, and is
/// the reason a cursor cannot be shared between threads even though a query can.
/// </remarks>
internal sealed class QueryCursorHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    internal static QueryCursorHandle Create()
    {
        var handle = new QueryCursorHandle();
        handle.SetHandle(TS.ts_query_cursor_new());
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        TS.ts_query_cursor_delete(handle);
        return true;
    }
}
