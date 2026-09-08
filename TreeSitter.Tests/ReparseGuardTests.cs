using System.Text;

using TreeSitter.Bindings;

namespace TreeSitter.Tests;

/// <summary>
/// The edits <see cref="SyntaxTree.Reparse"/> refuses, and the tree it leaves
/// behind when it does.
/// </summary>
/// <remarks>
/// A wrong edit normally cannot be detected — it re-parses into a plausible tree of
/// a file nobody has, which is what makes the equivalence tests next door the real
/// safety net. What can be detected is an edit that describes no pair of buffers at
/// all: a start past an end, or an end past the text it indexes. Those are the
/// shapes a half-written offset mapping produces, so they are worth a message
/// rather than a corrupt tree, and refusing them costs four comparisons.
/// </remarks>
public class ReparseGuardTests
{
    private const string Source = "class A { }\n";

    private static int OldLength => Encoding.UTF8.GetByteCount(Source);

    public static TheoryData<string, TSInputEdit> ImpossibleEdits => new()
    {
        {
            "start past old end",
            Edit(startByte: 6, oldEndByte: 3, newEndByte: 6)
        },
        {
            "start past new end",
            Edit(startByte: 6, oldEndByte: 6, newEndByte: 3)
        },
        {
            "old end past the old text",
            Edit(startByte: 0, oldEndByte: (uint)OldLength + 1, newEndByte: 0)
        },
        {
            "new end past the new text",
            Edit(startByte: 0, oldEndByte: 0, newEndByte: (uint)OldLength + 1)
        },
    };

    [Theory]
    [MemberData(nameof(ImpossibleEdits))]
    public void Reparse_RefusesAnEditThatDescribesNoPairOfBuffers(string because, TSInputEdit edit)
    {
        Assert.NotEmpty(because);

        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => tree.Reparse(parser, edit, Encoding.UTF8.GetBytes(Source)));
    }

    /// <summary>
    /// A refusal has to happen before the tree is edited, or the caller is handed an
    /// exception and left holding a tree whose offsets describe text it does not
    /// have — the exact state <see cref="SyntaxTree.Reparse"/> exists to prevent.
    /// </summary>
    [Theory]
    [MemberData(nameof(ImpossibleEdits))]
    public void Reparse_LeavesTheTreeUsable_WhenItRefusesTheEdit(string because, TSInputEdit edit)
    {
        Assert.NotEmpty(because);

        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);
        var before = TreeShape.Of(tree);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => tree.Reparse(parser, edit, Encoding.UTF8.GetBytes(Source)));

        Assert.Equal(before, TreeShape.Of(tree));
        Assert.Equal(Source, Encoding.UTF8.GetString(tree.Source));
    }

    /// <summary>
    /// The bounds are inclusive at both ends: appending at the very end of the file
    /// puts the start at the old length, and the new end at the new length.
    /// </summary>
    [Fact]
    public void Reparse_AcceptsAnEditThatEndsExactlyAtTheEndOfEitherBuffer()
    {
        var step = Edits.Splice(Source, OldLength, 0, "class B { }\n");

        using var parser = new Parser(Grammars.CSharp);
        var tree = parser.Parse(Source);

        using var reparsed = tree.Reparse(parser, step.Edit, step.NewSource);
        using var fromScratch = parser.Parse(step.NewSource);

        Assert.Equal(TreeShape.Of(fromScratch), TreeShape.Of(reparsed));
    }

    private static TSInputEdit Edit(uint startByte, uint oldEndByte, uint newEndByte) => new()
    {
        StartByte = startByte,
        OldEndByte = oldEndByte,
        NewEndByte = newEndByte,
    };
}
