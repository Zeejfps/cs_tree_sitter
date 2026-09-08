using System.Text;

using TreeSitter.Bindings;

namespace TreeSitter.Tests;

/// <summary>
/// <see cref="SyntaxTree.Reparse"/>: that an edited tree ends up identical to a
/// tree of the same text parsed from scratch, and that the old tree and its buffer
/// are gone by the time anyone could read them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Bindings.Tests"/> pins the ABI — that <c>TSInputEdit</c>'s fields
/// land where C expects them. These are about the operation this layer builds on
/// top: the parser and the tree have to be on the same grammar, the new tree has to
/// own the new bytes, and there must be no moment at which a tree whose offsets
/// describe text it does not have is reachable.
/// </para>
/// <para>
/// Equivalence is the assertion throughout, because a wrong edit does not fail. It
/// reuses subtrees at shifted offsets and returns a tree that parses, walks and
/// queries exactly like a correct one while describing a file nobody has. Every
/// edit shape below is compared against a from-scratch parse for that reason.
/// </para>
/// </remarks>
public class ReparseTests
{
    /// <summary>
    /// The accented letter is load-bearing: it sits before the edit point in one of
    /// the cases, and a point column is bytes, so an edit measured in characters is
    /// wrong from there to the end of the line.
    /// </summary>
    private const string Source =
        "namespace Demo;\n" +
        "\n" +
        "public class Greeter\n" +
        "{\n" +
        "    public string Greet(string name) => \"Héllo\";\n" +
        "\n" +
        "    public string Farewell(string name) => \"Bye\";\n" +
        "}\n";

    [Fact]
    public void Reparse_MatchesAFromScratchParse_AfterAnInsertAtTheStartOfTheFile() =>
        AssertMatchesAFromScratchParse(Source, Edits.Splice(Source, 0, 0, "using System;\n"));

    [Fact]
    public void Reparse_MatchesAFromScratchParse_AfterAnInsertInTheMiddle() =>
        AssertMatchesAFromScratchParse(Source, Edits.InsertAfter(Source, "public ", "sealed "));

    [Fact]
    public void Reparse_MatchesAFromScratchParse_AfterAnInsertAtTheEndOfTheFile() =>
        AssertMatchesAFromScratchParse(
            Source, Edits.Splice(Source, Encoding.UTF8.GetByteCount(Source), 0, "\n// nothing follows\n"));

    [Fact]
    public void Reparse_MatchesAFromScratchParse_AfterADeletion() =>
        AssertMatchesAFromScratchParse(Source, Edits.Remove(Source, "public "));

    [Fact]
    public void Reparse_MatchesAFromScratchParse_AfterAReplacement() =>
        AssertMatchesAFromScratchParse(Source, Edits.ReplaceOne(Source, "Greeter", "Salutation"));

    /// <summary>
    /// Rows below the edit move. A tree that reuses subtrees without shifting their
    /// points reports the right text at the wrong line, which is the failure a
    /// caller sees as colouring on the line above.
    /// </summary>
    [Fact]
    public void Reparse_MatchesAFromScratchParse_AfterAnEditThatAddsLines() =>
        AssertMatchesAFromScratchParse(
            Source,
            Edits.InsertBefore(Source, "    public string Farewell", "    public int Count => 1;\n\n"));

    [Fact]
    public void Reparse_MatchesAFromScratchParse_AfterAnEditThatRemovesLines() =>
        AssertMatchesAFromScratchParse(
            Source, Edits.Remove(Source, "    public string Farewell(string name) => \"Bye\";\n"));

    /// <summary>
    /// The edit point is on a line with a two-byte letter in front of it, so its
    /// byte column and its character column differ.
    /// </summary>
    [Fact]
    public void Reparse_MatchesAFromScratchParse_AfterAnEditPastAMultiByteCharacter() =>
        AssertMatchesAFromScratchParse(Source, Edits.InsertAfter(Source, "\"Héllo\"", ".Trim()"));

    [Fact]
    public void Reparse_MatchesAFromScratchParse_AfterEmptyingTheBuffer() =>
        AssertMatchesAFromScratchParse(
            Source, Edits.Splice(Source, 0, Encoding.UTF8.GetByteCount(Source), string.Empty));

    [Fact]
    public void Reparse_MatchesAFromScratchParse_AfterFillingAnEmptyBuffer() =>
        AssertMatchesAFromScratchParse(string.Empty, Edits.Splice(string.Empty, 0, 0, Source));

    /// <summary>
    /// One tree carried across several edits, which is the way this is meant to be
    /// used. Each step is checked, so an error that only shows up once a reused
    /// subtree has been reused twice has somewhere to appear.
    /// </summary>
    [Fact]
    public void Reparse_MatchesAFromScratchParse_AcrossASequenceOfEditsOnOneTree()
    {
        using var parser = new Parser(Grammars.CSharp);
        var text = Source;
        var tree = parser.Parse(text);

        try
        {
            Apply(Edits.ReplaceOne(text, "public class", "internal class"));
            Apply(Edits.InsertBefore(text, "}", "    public int Count => 1;\n"));
            Apply(Edits.Remove(text, "    public string Farewell(string name) => \"Bye\";\n"));
            Apply(Edits.ReplaceOne(text, "Héllo", "Hi"));
            Apply(Edits.InsertBefore(text, "namespace", "using System;\n"));
            Apply(Edits.Splice(text, 0, Encoding.UTF8.GetByteCount(text), "class Empty { }"));
        }
        finally
        {
            tree.Dispose();
        }

        void Apply((byte[] NewSource, TSInputEdit Edit) step)
        {
            tree = tree.Reparse(parser, step.Edit, step.NewSource);
            text = Encoding.UTF8.GetString(step.NewSource);

            using var fromScratch = parser.Parse(step.NewSource);
            Assert.Equal(TreeShape.Of(fromScratch), TreeShape.Of(tree));
        }
    }

    /// <summary>
    /// The old tree is consumed, and consumed once. Anything holding a node from it
    /// is refused rather than reading a tree the parse has already released.
    /// </summary>
    [Fact]
    public void Reparse_ConsumesTheTreeItWasCalledOn()
    {
        using var parser = new Parser(Grammars.CSharp);
        var tree = parser.Parse(Source);
        var method = Find.First(tree.RootNode, "method_declaration");

        var (newSource, edit) = Edits.ReplaceOne(Source, "Greeter", "Salutation");
        using var reparsed = tree.Reparse(parser, edit, newSource);

        Assert.Throws<ObjectDisposedException>(() => { _ = method.Text; });
        Assert.Throws<ObjectDisposedException>(() => { _ = method.StartByte; });
        Assert.Throws<ObjectDisposedException>(() => { _ = tree.RootNode; });
        Assert.Throws<ObjectDisposedException>(() => { _ = tree.Source.Length; });

        // Releasing it again is the caller's `using`, and must stay harmless.
        tree.Dispose();
    }

    /// <summary>
    /// The new tree owns the new bytes. Reused subtrees keep their identity but not
    /// their old buffer, so text read through them is text of the file that exists.
    /// </summary>
    [Fact]
    public void Reparse_ReadsNodeTextFromTheNewSource()
    {
        using var parser = new Parser(Grammars.CSharp);
        var tree = parser.Parse(Source);

        var (newSource, edit) = Edits.ReplaceOne(Source, "\"Héllo\"", "\"Goodbye\"");
        using var reparsed = tree.Reparse(parser, edit, newSource);

        var expected = Encoding.UTF8.GetString(newSource);
        Assert.Equal(expected, reparsed.RootNode.Text);
        Assert.Equal(newSource, reparsed.Source.ToArray());

        var literals = Find.All(reparsed.RootNode, "string_literal");
        Assert.Equal("\"Goodbye\"", literals[0].Text);

        // Untouched by the edit, and reused by the parse -- so it is the node most
        // likely to still be reading the old buffer.
        Assert.Equal("\"Bye\"", literals[1].Text);
    }

    /// <summary>
    /// Node type ids are per-grammar, so a re-parse by the wrong parser does not
    /// fail — it produces another language's tree of the same bytes.
    /// </summary>
    [Fact]
    public void Reparse_RefusesAParserForAnotherGrammar()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var typescript = new Parser(Grammars.TypeScript);
        var tree = parser.Parse(Source);

        var (newSource, edit) = Edits.ReplaceOne(Source, "Greeter", "Salutation");

        Assert.Throws<ArgumentException>(() => { _ = tree.Reparse(typescript, edit, newSource); });

        // Refused before anything was edited, so the tree is still a tree of Source.
        using var untouched = parser.Parse(Source);
        Assert.Equal(TreeShape.Of(untouched), TreeShape.Of(tree));

        tree.Dispose();
    }

    [Fact]
    public void Reparse_RefusesADisposedTree()
    {
        using var parser = new Parser(Grammars.CSharp);
        var tree = parser.Parse(Source);
        tree.Dispose();

        var (newSource, edit) = Edits.ReplaceOne(Source, "Greeter", "Salutation");

        Assert.Throws<ObjectDisposedException>(() => { _ = tree.Reparse(parser, edit, newSource); });
    }

    [Fact]
    public void Reparse_RefusesADisposedParser()
    {
        var parser = new Parser(Grammars.CSharp);
        var tree = parser.Parse(Source);
        parser.Dispose();

        var (newSource, edit) = Edits.ReplaceOne(Source, "Greeter", "Salutation");

        Assert.Throws<ObjectDisposedException>(() => { _ = tree.Reparse(parser, edit, newSource); });

        // The parser is checked before the tree is edited, so this tree is still
        // usable -- and still the caller's to dispose.
        Assert.Equal("compilation_unit", tree.RootNode.Type);
        tree.Dispose();
    }

    private static void AssertMatchesAFromScratchParse(
        string before, (byte[] NewSource, TSInputEdit Edit) step)
    {
        using var parser = new Parser(Grammars.CSharp);
        var tree = parser.Parse(before);

        using var reparsed = tree.Reparse(parser, step.Edit, step.NewSource);
        using var fromScratch = parser.Parse(step.NewSource);

        Assert.Equal(TreeShape.Of(fromScratch), TreeShape.Of(reparsed));
        Assert.Equal(step.NewSource, reparsed.Source.ToArray());
    }
}
