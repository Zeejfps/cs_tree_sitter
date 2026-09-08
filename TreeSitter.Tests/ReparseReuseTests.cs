namespace TreeSitter.Tests;

/// <summary>
/// Re-parsing a tree with a parser other than the one that produced it, and
/// re-parsing grammars whose lexing carries state across the edit.
/// </summary>
/// <remarks>
/// <para>
/// Both are the normal case rather than the edge one. A caller that pools parsers —
/// which is the shape this library recommends, one per worker rather than one per
/// file — hands a maintained tree to whichever parser is idle, so a tree is rarely
/// re-parsed by the parser that built it. And the grammars most likely to break
/// under subtree reuse are the ones with external scanners, which is not the C# the
/// tests next door are written against.
/// </para>
/// </remarks>
public class ReparseReuseTests
{
    private const string CSharp =
        "class Greeter\n" +
        "{\n" +
        "    public string Greet() => \"Hello\";\n" +
        "}\n";

    /// <summary>
    /// Indentation decides where a block ends, so this edit moves a statement out of
    /// one and the tree has to be re-shaped rather than re-offset.
    /// </summary>
    private const string Python =
        "def outer():\n" +
        "    if flag:\n" +
        "        first()\n" +
        "        second()\n" +
        "    return 1\n";

    /// <summary>
    /// A heredoc body runs until a terminator in column zero, so the scanner is
    /// carrying state across the lines the edit sits between.
    /// </summary>
    private const string Bash =
        "cat <<EOF\n" +
        "  body line\n" +
        "EOF\n" +
        "echo done\n";

    [Fact]
    public void Reparse_AcceptsADifferentParserOnTheSameGrammar()
    {
        var step = Edits.InsertAfter(CSharp, "class ", "Polite");

        using var built = new Parser(Grammars.CSharp);
        using var reparsedBy = new Parser(Grammars.CSharp);
        var tree = built.Parse(CSharp);

        using var reparsed = tree.Reparse(reparsedBy, step.Edit, step.NewSource);
        using var fromScratch = built.Parse(step.NewSource);

        Assert.Equal(TreeShape.Of(fromScratch), TreeShape.Of(reparsed));
    }

    /// <summary>
    /// A tree does not belong to its parser: it keeps the subtrees it is made of,
    /// and releasing them does not go back through whatever parsed them. A pool that
    /// retires an idle parser must not take the open files with it.
    /// </summary>
    [Fact]
    public void Reparse_AcceptsATreeWhoseOriginalParserIsGone()
    {
        var step = Edits.InsertAfter(CSharp, "class ", "Polite");

        var built = new Parser(Grammars.CSharp);
        var tree = built.Parse(CSharp);
        built.Dispose();

        using var reparsedBy = new Parser(Grammars.CSharp);
        using var reparsed = tree.Reparse(reparsedBy, step.Edit, step.NewSource);
        using var fromScratch = reparsedBy.Parse(step.NewSource);

        Assert.Equal(TreeShape.Of(fromScratch), TreeShape.Of(reparsed));

        // The bytes are still readable too: node text comes out of the new tree's
        // own buffer, not out of anything the retired parser was holding.
        Assert.Equal(System.Text.Encoding.UTF8.GetString(step.NewSource), reparsed.RootNode.Text);
    }

    [Fact]
    public void Reparse_MatchesAFromScratchParse_WhenAnEditChangesPythonIndentation() =>
        AssertMatches(Grammars.Python, Python, Edits.ReplaceOne(Python, "        second()", "    second()"));

    [Fact]
    public void Reparse_MatchesAFromScratchParse_WhenAnEditAddsAPythonLine() =>
        AssertMatches(Grammars.Python, Python, Edits.InsertBefore(Python, "        second()", "        middle()\n"));

    [Fact]
    public void Reparse_MatchesAFromScratchParse_WhenAnEditLandsInsideAHeredoc() =>
        AssertMatches(Grammars.Bash, Bash, Edits.InsertBefore(Bash, "  body line", "  extra\n"));

    /// <summary>
    /// Removing the terminator changes where the heredoc ends, so every line after
    /// it means something different. Reuse that ignored the scanner's state would
    /// keep the old shape.
    /// </summary>
    [Fact]
    public void Reparse_MatchesAFromScratchParse_WhenAnEditMovesAHeredocTerminator() =>
        AssertMatches(Grammars.Bash, Bash, Edits.ReplaceOne(Bash, "EOF\n", "  EOF\n"));

    private static void AssertMatches(
        Language language, string before, (byte[] NewSource, Bindings.TSInputEdit Edit) step)
    {
        using var parser = new Parser(language);
        var tree = parser.Parse(before);

        using var reparsed = tree.Reparse(parser, step.Edit, step.NewSource);
        using var fromScratch = parser.Parse(step.NewSource);

        Assert.Equal(TreeShape.Of(fromScratch), TreeShape.Of(reparsed));
        Assert.Equal(step.NewSource, reparsed.Source.ToArray());
    }
}
