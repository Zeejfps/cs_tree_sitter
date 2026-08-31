namespace TreeSitter.Tests;

/// <summary>
/// Predicate evaluation, and the regex dialect it commits to.
/// </summary>
/// <remarks>
/// <para>
/// Half of this is ordinary: each predicate in both its capture-vs-literal and
/// capture-vs-capture forms, the negative spellings, and the refusals that keep an
/// unrecognized predicate from being silently skipped.
/// </para>
/// <para>
/// The other half pins behaviour that neither engine can be trusted to agree on.
/// <c>#match?</c> patterns in the wild are written against Rust's <c>regex</c> crate
/// and run here through .NET's <see cref="System.Text.RegularExpressions.Regex"/>,
/// which is a different language. Where the two disagree the answer is written down
/// as a test rather than assumed, because the failure mode is a query that matches
/// something else and says nothing.
/// </para>
/// </remarks>
public class QueryPredicateTests
{
    private const string Source =
        "public class Greeter\n" +
        "{\n" +
        "    public string Greet(string name) => \"Hello\";\n" +
        "\n" +
        "    public string Farewell(string name) => \"Bye\";\n" +
        "\n" +
        "    private const int Retries = 3;\n" +
        "\n" +
        "    private int _count;\n" +
        "}\n";

    /// <summary>A class with a method of its own name, for the capture-vs-capture forms.</summary>
    private const string Echoing =
        """
        public class Echo
        {
            public void Echo() { }
            public void Other() { }
        }
        """;

    // -- #eq? and #not-eq? --------------------------------------------------------

    [Fact]
    public void Eq_AgainstALiteral_KeepsOnlyTheCaptureThatEqualsIt()
    {
        Assert.Equal(
            ["Greet"],
            Run(
                """
                ((method_declaration name: (identifier) @name)
                 (#eq? @name "Greet"))
                """,
                "name"));
    }

    [Fact]
    public void NotEq_AgainstALiteral_DropsIt()
    {
        Assert.Equal(
            ["Farewell"],
            Run(
                """
                ((method_declaration name: (identifier) @name)
                 (#not-eq? @name "Greet"))
                """,
                "name"));
    }

    /// <summary>
    /// The second form, and the one a literal cannot express: two captures compared
    /// against each other.
    /// </summary>
    [Fact]
    public void Eq_BetweenTwoCaptures_ComparesTheirTexts()
    {
        const string pattern =
            """
            ((class_declaration
               name: (identifier) @class
               body: (declaration_list
                 (method_declaration name: (identifier) @method)))
             (#eq? @class @method))
            """;

        Assert.Equal(["Echo"], Run(pattern, "method", Echoing));
    }

    [Fact]
    public void NotEq_BetweenTwoCaptures_KeepsTheOnesThatDiffer()
    {
        const string pattern =
            """
            ((class_declaration
               name: (identifier) @class
               body: (declaration_list
                 (method_declaration name: (identifier) @method)))
             (#not-eq? @class @method))
            """;

        Assert.Equal(["Other"], Run(pattern, "method", Echoing));
    }

    // -- #any-of? and #not-any-of? -----------------------------------------------

    [Fact]
    public void AnyOf_KeepsEveryCaptureInTheList()
    {
        Assert.Equal(
            ["Greet", "Farewell"],
            Run(
                """
                ((method_declaration name: (identifier) @name)
                 (#any-of? @name "Greet" "Farewell" "Absent"))
                """,
                "name"));
    }

    [Fact]
    public void NotAnyOf_DropsThem()
    {
        Assert.Equal(
            ["Farewell"],
            Run(
                """
                ((method_declaration name: (identifier) @name)
                 (#not-any-of? @name "Greet" "Absent"))
                """,
                "name"));
    }

    // -- #match? and #not-match? --------------------------------------------------

    /// <summary>
    /// <b>A search, not a full match</b> — which is what tree-sitter's own bindings
    /// do, and the single decision most likely to be assumed the other way.
    /// </summary>
    [Fact]
    public void Match_SearchesRatherThanAnchoring()
    {
        Assert.Equal(["Greet", "Farewell"], Run(MatchingNames("re"), "name"));
        Assert.Equal(["Greet"], Run(MatchingNames("^Gr"), "name"));
        Assert.Empty(Run(MatchingNames("^re"), "name"));
    }

    [Fact]
    public void NotMatch_IsTheComplement()
    {
        Assert.Equal(
            ["Farewell"],
            Run(
                """
                ((method_declaration name: (identifier) @name)
                 (#not-match? @name "^G"))
                """,
                "name"));
    }

    [Fact]
    public void SeveralPredicatesOnOnePattern_AllHaveToHold()
    {
        Assert.Empty(
            Run(
                """
                ((method_declaration name: (identifier) @name)
                 (#match? @name "e")
                 (#eq? @name "Absent"))
                """,
                "name"));
    }

    /// <summary>
    /// A capture the match did not bind passes, rather than failing the pattern. The
    /// alternative would make an optional capture and a predicate on it mutually
    /// exclusive, and it is what tree-sitter's own bindings do.
    /// </summary>
    [Fact]
    public void APredicateOnACaptureTheMatchDidNotBind_Holds()
    {
        const string pattern =
            """
            ((method_declaration
               name: (identifier) @name
               (parameter_list (parameter type: (predefined_type) @_type))?)
             (#eq? @_type "int"))
            """;

        // Neither method takes a parameter, so @_type binds nothing and there is
        // nothing for the predicate to be false about.
        Assert.Equal(["Echo", "Other"], Run(pattern, "name", Echoing));
    }

    // -- Refusals -----------------------------------------------------------------

    /// <summary>
    /// The reason the evaluator exists. Skipping <c>#is-not?</c> would reproduce
    /// exactly the over-matching that refusing predicates outright was protecting
    /// against, and it would do it one predicate at a time.
    /// </summary>
    [Fact]
    public void AnUnrecognizedPredicate_IsRefusedAtCompileTime()
    {
        var error = Assert.Throws<QueryPredicateException>(() => Query.Compile(
            Grammars.CSharp,
            """
            ((method_declaration name: (identifier) @name)
             (#is-not? @name local))
            """));

        Assert.Equal("#is-not?", error.PredicateName);
        Assert.Equal(0u, error.PatternIndex);

        // And it says which pattern, because a pattern number is not enough to find
        // one in a file of thirty.
        Assert.Contains("method_declaration", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""(#eq? @name)""")]
    [InlineData("""(#eq? @name "a" "b")""")]
    [InlineData("""(#match? @name "a" "b")""")]
    [InlineData("""(#match? @name @name)""")]
    [InlineData("""(#any-of? @name)""")]
    [InlineData("""(#any-of? @name @name)""")]
    [InlineData("""(#eq? "a" "b")""")]
    public void APredicateWithTheWrongArgumentsIsAQueryBug_AndIsRefused(string predicate)
    {
        Assert.Throws<QueryPredicateException>(() => Query.Compile(
            Grammars.CSharp,
            $"((method_declaration name: (identifier) @name)\n {predicate})"));
    }

    // -- The regex dialect --------------------------------------------------------

    /// <summary>
    /// <b>POSIX bracket expressions are the one construct that had to be banned by
    /// hand.</b> Rust reads <c>[[:alpha:]]</c> as a character class; .NET reads it as
    /// the class <c>[[:alph]</c> followed by a literal <c>]</c> — so it matches
    /// <c>"a]"</c> and <c>":]"</c> and not <c>"a"</c>, and complains about none of it.
    /// Every other Rust-only spelling (<c>\p{Greek}</c>, <c>\pL</c>,
    /// <c>(?P&lt;n&gt;)</c>) is a .NET parse error, which needs no help from us.
    /// </summary>
    [Fact]
    public void ARegexUsingAPosixBracketExpression_IsRefused()
    {
        var error = Assert.Throws<QueryPredicateException>(
            () => Query.Compile(Grammars.CSharp, MatchingNames("^[[:alpha:]]+$")));

        Assert.Equal("#match?", error.PredicateName);
        Assert.Contains("POSIX", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>#match?</c> compiles <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking"/>,
    /// which refuses lookaround, backreferences and atomic groups — the same
    /// constructs Rust's engine refuses, for the same reason. So the accepted dialect
    /// is close to the one these patterns are written in, and matching a whole
    /// repository cannot be made quadratic by a query file.
    /// </summary>
    [Theory]
    [InlineData("^(?=G)")]
    [InlineData("(?<=G)r")]
    [InlineData(@"(.)\1")]
    [InlineData("(?>G*)r")]
    public void ARegexUsingAConstructRustsEngineAlsoRefuses_IsRefusedHere(string regex)
    {
        Assert.Throws<QueryPredicateException>(() => Query.Compile(Grammars.CSharp, MatchingNames(regex)));
    }

    [Fact]
    public void ARegexThatDoesNotParse_IsRefusedWhenTheQueryIsCompiled_NotWhenItRuns()
    {
        var error = Assert.Throws<QueryPredicateException>(
            () => Query.Compile(Grammars.CSharp, MatchingNames("^(unclosed")));

        Assert.Equal("#match?", error.PredicateName);
    }

    /// <summary>
    /// <b>A known divergence, left as .NET's.</b> Rust's <c>$</c> is the end of the
    /// haystack; .NET's also matches immediately before a final newline. The root node
    /// is the one capture whose text routinely ends in one, and rewriting <c>$</c> to
    /// <c>\z</c> would mean parsing the pattern well enough to know whether
    /// <c>(?m)</c> is in effect — more machinery, and more ways to be wrong, than the
    /// divergence costs. Write <c>\z</c> when the difference matters.
    /// </summary>
    [Fact]
    public void DollarMatchesBeforeAFinalNewline_WhereRustsWouldNot()
    {
        const string source = "class A { }\n";

        Assert.Single(Run("""((compilation_unit) @unit (#match? @unit "}$"))""", "unit", source));
        Assert.Empty(Run("""((compilation_unit) @unit (#match? @unit "}\\z"))""", "unit", source));
    }

    /// <summary>
    /// The other known divergence: <c>.</c> is one UTF-16 code unit here and one
    /// Unicode scalar in Rust, so anything outside the basic multilingual plane counts
    /// as two. It costs nothing for the identifiers predicates are usually written
    /// about, and it is not fixable without a different regex engine.
    /// </summary>
    [Fact]
    public void DotIsAUtf16CodeUnit_NotAUnicodeScalar()
    {
        const string source = "class A { string s = \"\U0001F600\"; }";
        const string pattern = """((string_literal) @s (#match? @s "^\"{0}\"$"))""";

        Assert.Empty(Run(pattern.Replace("{0}", "."), "s", source));
        Assert.Single(Run(pattern.Replace("{0}", ".."), "s", source));
    }

    private static string MatchingNames(string regex) =>
        $$"""
          ((method_declaration name: (identifier) @name)
           (#match? @name "{{regex.Replace("\\", "\\\\")}}"))
          """;

    private static List<string> Run(string pattern, string capture, string source = Source)
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(source);
        using var query = Query.Compile(Grammars.CSharp, pattern);
        using var cursor = new QueryCursor();

        var captureId = query.CaptureId(capture);
        var found = new List<string>();

        cursor.ForEachMatch(query, tree.RootNode, match =>
        {
            if (match.TryGetNode(captureId, out var node))
            {
                found.Add(node.Text);
            }
        });

        return found;
    }
}
