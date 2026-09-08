using System.Runtime.InteropServices;
using System.Text;
using static TreeSitter.Bindings.Tests.Syntax;

namespace TreeSitter.Bindings.Tests;

/// <summary>
/// The gate on <see cref="TSInputEdit"/> and on the incremental parse path.
/// </summary>
/// <remarks>
/// <para>
/// The struct is six 32-bit fields in a row and nothing at the boundary checks
/// them. Declare them in the wrong order and no call fails: the size is still 36,
/// <c>ts_tree_edit</c> still accepts it, and the parse still returns a tree.
/// </para>
/// <para>
/// So nothing here asserts that a re-parse "works". One test pins what the edit
/// does to the tree on its own — the extent moves by exactly the bytes that were
/// inserted — and one pins that the re-parsed tree is <em>identical</em> to a
/// from-scratch parse of the same bytes, node for node and offset for offset.
/// </para>
/// </remarks>
public class TreeEditTests
{
    private const string Before =
        "class Greeter\n" +
        "{\n" +
        "    int Count => 1;\n" +
        "}\n";

    /// <summary>The same file with <c>public </c> — seven bytes — inserted at byte 0.</summary>
    private const string After = "public " + Before;

    private static TSInputEdit InsertedAtTheStart => new()
    {
        StartByte = 0,
        OldEndByte = 0,
        NewEndByte = 7,
        StartPoint = new TSPoint { Row = 0, Column = 0 },
        OldEndPoint = new TSPoint { Row = 0, Column = 0 },
        NewEndPoint = new TSPoint { Row = 0, Column = 7 },
    };

    /// <summary>
    /// The offsets, against <c>api.h</c>, because the three points cannot be
    /// checked any other way.
    /// </summary>
    /// <remarks>
    /// Parsing a contiguous buffer recomputes every position from the text, so a
    /// tree comes out identical whatever points the edit carried — verified by
    /// zeroing all three, at which point nothing below fails. The byte fields are
    /// pinned by behaviour in the tests that follow; the points are pinned only by
    /// this, and by the declaration order in the header.
    /// </remarks>
    [Fact]
    public void TSInputEdit_LaysOutItsFieldsInTheHeadersOrder()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<TSInputEdit>(nameof(TSInputEdit.StartByte)));
        Assert.Equal(4, (int)Marshal.OffsetOf<TSInputEdit>(nameof(TSInputEdit.OldEndByte)));
        Assert.Equal(8, (int)Marshal.OffsetOf<TSInputEdit>(nameof(TSInputEdit.NewEndByte)));
        Assert.Equal(12, (int)Marshal.OffsetOf<TSInputEdit>(nameof(TSInputEdit.StartPoint)));
        Assert.Equal(20, (int)Marshal.OffsetOf<TSInputEdit>(nameof(TSInputEdit.OldEndPoint)));
        Assert.Equal(28, (int)Marshal.OffsetOf<TSInputEdit>(nameof(TSInputEdit.NewEndPoint)));
    }

    /// <summary>
    /// Before any re-parse, the edit alone has to move the tree's extent by exactly
    /// the seven bytes that were inserted — which it cannot do if
    /// <c>new_end_byte</c> is being read out of another field's slot.
    /// </summary>
    [Fact]
    public void TreeEdit_ShiftsTheTreesExtentByTheSizeOfTheEdit()
    {
        WithParser(parser =>
        {
            var tree = Parse(parser, Encoding.UTF8.GetBytes(Before), nint.Zero);
            try
            {
                var beforeLength = TS.ts_node_end_byte(TS.ts_tree_root_node(tree));

                var edit = InsertedAtTheStart;
                TS.ts_tree_edit(tree, in edit);

                var root = TS.ts_tree_root_node(tree);
                Assert.Equal(beforeLength + 7, TS.ts_node_end_byte(root));
                Assert.Equal((uint)Encoding.UTF8.GetByteCount(After), TS.ts_node_end_byte(root));
            }
            finally
            {
                TS.ts_tree_delete(tree);
            }
        });
    }

    [Fact]
    public void ParsingWithAnEditedOldTree_ProducesTheTreeAFreshParseWould()
    {
        WithParser(parser =>
        {
            var newBytes = Encoding.UTF8.GetBytes(After);

            var old = Parse(parser, Encoding.UTF8.GetBytes(Before), nint.Zero);
            var edit = InsertedAtTheStart;
            TS.ts_tree_edit(old, in edit);

            var reparsed = Parse(parser, newBytes, old);
            var fromScratch = Parse(parser, newBytes, nint.Zero);

            try
            {
                Assert.Equal(
                    Describe(TS.ts_tree_root_node(fromScratch)),
                    Describe(TS.ts_tree_root_node(reparsed)));
            }
            finally
            {
                TS.ts_tree_delete(fromScratch);
                TS.ts_tree_delete(reparsed);
                TS.ts_tree_delete(old);
            }
        });
    }

    /// <summary>
    /// The old tree survives the parse that reused it. tree-sitter takes no
    /// ownership of it, so the caller both may still read it and must still delete
    /// it.
    /// </summary>
    [Fact]
    public void ParsingWithAnEditedOldTree_LeavesTheOldTreeTheCallersToDelete()
    {
        WithParser(parser =>
        {
            var old = Parse(parser, Encoding.UTF8.GetBytes(Before), nint.Zero);
            var edit = InsertedAtTheStart;
            TS.ts_tree_edit(old, in edit);

            var reparsed = Parse(parser, Encoding.UTF8.GetBytes(After), old);

            try
            {
                Assert.NotEqual(old, reparsed);
                Assert.Equal("compilation_unit", TypeOf(TS.ts_tree_root_node(old)));
            }
            finally
            {
                TS.ts_tree_delete(reparsed);
                TS.ts_tree_delete(old);
            }
        });
    }

    private static unsafe nint Parse(nint parser, byte[] source, nint oldTree)
    {
        nint tree;
        fixed (byte* pinned = source)
        {
            tree = TS.ts_parser_parse_string(parser, oldTree, pinned, (uint)source.Length);
        }

        Assert.NotEqual(nint.Zero, tree);
        return tree;
    }

    private static void WithParser(Action<nint> assertions)
    {
        var parser = TS.ts_parser_new();
        Assert.NotEqual(nint.Zero, parser);

        try
        {
            Assert.True(TS.ts_parser_set_language(parser, Grammars.CSharp));
            assertions(parser);
        }
        finally
        {
            TS.ts_parser_delete(parser);
        }
    }
}
