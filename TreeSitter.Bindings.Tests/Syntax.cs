using System.Runtime.InteropServices;
using System.Text;

namespace TreeSitter.Bindings.Tests;

/// <summary>
/// The handful of operations every test here needs on top of the raw entry
/// points: parse something, find a node, read its text.
/// </summary>
/// <remarks>
/// Deliberately not the managed wrapper layer — no <c>SafeHandle</c>s, no
/// ownership model, nothing worth reusing outside tests. It exists so the
/// assertions stay about tree-sitter's behaviour rather than about
/// <c>try</c>/<c>finally</c>.
/// </remarks>
internal static class Syntax
{
    /// <summary>
    /// Parses <paramref name="source"/> with <paramref name="language"/> and hands
    /// the root node to <paramref name="assertions"/>, releasing the parser and
    /// tree afterwards. Nodes must not outlive the callback, because the tree they
    /// point into does not.
    /// </summary>
    internal static unsafe void WithParsedSource(nint language, byte[] source, Action<TSNode> assertions)
    {
        var parser = TS.ts_parser_new();
        Assert.NotEqual(nint.Zero, parser);

        try
        {
            Assert.True(TS.ts_parser_set_language(parser, language));

            nint tree;
            fixed (byte* pinned = source)
            {
                tree = TS.ts_parser_parse_string(parser, nint.Zero, pinned, (uint)source.Length);
            }

            Assert.NotEqual(nint.Zero, tree);
            try
            {
                assertions(TS.ts_tree_root_node(tree));
            }
            finally
            {
                TS.ts_tree_delete(tree);
            }
        }
        finally
        {
            TS.ts_parser_delete(parser);
        }
    }

    /// <summary>Overload for source given as text, which is most of it.</summary>
    internal static void WithParsedSource(nint language, string source, Action<TSNode, byte[]> assertions)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        WithParsedSource(language, bytes, root => assertions(root, bytes));
    }

    /// <summary>
    /// Compiles <paramref name="pattern"/> against <paramref name="language"/> and
    /// hands the query to <paramref name="assertions"/>, failing with the byte
    /// offset and reason if the pattern is rejected.
    /// </summary>
    internal static unsafe void WithQuery(nint language, string pattern, Action<nint> assertions)
    {
        var patternBytes = Encoding.UTF8.GetBytes(pattern);
        nint query;
        fixed (byte* source = patternBytes)
        {
            query = TS.ts_query_new(
                language, source, (uint)patternBytes.Length, out var errorOffset, out var errorType);
            Assert.True(query != nint.Zero, $"Query rejected: {errorType} at byte {errorOffset}.");
        }

        try
        {
            assertions(query);
        }
        finally
        {
            TS.ts_query_delete(query);
        }
    }

    internal static string TypeOf(TSNode node) =>
        Marshal.PtrToStringUTF8(TS.ts_node_type(node))!;

    internal static string TextOf(TSNode node, byte[] source) =>
        Encoding.UTF8.GetString(
            source,
            (int)TS.ts_node_start_byte(node),
            (int)(TS.ts_node_end_byte(node) - TS.ts_node_start_byte(node)));

    internal static unsafe TSNode ChildByField(TSNode node, ReadOnlySpan<byte> field)
    {
        fixed (byte* name = field)
        {
            return TS.ts_node_child_by_field_name(node, name, (uint)field.Length);
        }
    }

    /// <summary>Depth-first search for the first node of a given grammar type.</summary>
    internal static TSNode FindFirst(TSNode node, string type)
    {
        if (TypeOf(node) == type)
        {
            return node;
        }

        var count = TS.ts_node_named_child_count(node);
        for (uint i = 0; i < count; i++)
        {
            var found = FindFirst(TS.ts_node_named_child(node, i), type);
            if (!TS.ts_node_is_null(found))
            {
                return found;
            }
        }

        return default;
    }

    /// <summary>Every node of a given grammar type, in document order.</summary>
    internal static List<TSNode> FindAll(TSNode node, string type)
    {
        var found = new List<TSNode>();
        Walk(node);
        return found;

        void Walk(TSNode current)
        {
            if (TypeOf(current) == type)
            {
                found.Add(current);
            }

            var count = TS.ts_node_named_child_count(current);
            for (uint i = 0; i < count; i++)
            {
                Walk(TS.ts_node_named_child(current, i));
            }
        }
    }
}
