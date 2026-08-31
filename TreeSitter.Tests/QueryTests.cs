using TreeSitter.Bindings;

namespace TreeSitter.Tests;

/// <summary>
/// The query half of the wrapper: ids resolved once, matches read safely, and
/// failures that say where.
/// </summary>
public class QueryTests
{
    private const string Source =
        "public class Greeter\n" +
        "{\n" +
        "    public string Greet(string name) => \"Hello\";\n" +
        "\n" +
        "    public string Farewell(string name) => \"Bye\";\n" +
        "}\n";

    private const string Methods = "(method_declaration name: (identifier) @method.name)";

    private const string ClassesAndMethods =
        """
        (class_declaration name: (identifier) @class.name)
        (method_declaration name: (identifier) @method.name)
        """;

    [Fact]
    public void CaptureNames_ResolveToIdsAtCompileTime()
    {
        using var query = Query.Compile(Grammars.CSharp, ClassesAndMethods);

        Assert.Equal(2, query.PatternCount);
        Assert.Equal(2, query.CaptureCount);

        // The ids are whatever tree-sitter assigned; what matters is that the two
        // directions agree, since the per-match path only ever sees the integer.
        var classId = query.CaptureId("class.name");
        var methodId = query.CaptureId("method.name");

        Assert.NotEqual(classId, methodId);
        Assert.Equal("class.name", query.CaptureName(classId));
        Assert.Equal("method.name", query.CaptureName(methodId));

        Assert.True(query.TryGetCaptureId("method.name", out var again));
        Assert.Equal(methodId, again);
        Assert.False(query.TryGetCaptureId("method.body", out _));
    }

    /// <summary>
    /// A capture renamed in the query but not in the code reading it would otherwise
    /// look like a query that simply matches nothing.
    /// </summary>
    [Fact]
    public void CaptureId_ForANameTheQueryDoesNotDeclare_Throws()
    {
        using var query = Query.Compile(Grammars.CSharp, Methods);

        var error = Assert.Throws<ArgumentException>(() => { _ = query.CaptureId("method.body"); });
        Assert.Contains("method.name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_ReturnTheCapturedNodes()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);
        using var query = Query.Compile(Grammars.CSharp, ClassesAndMethods);
        using var cursor = new QueryCursor();

        var classId = query.CaptureId("class.name");
        var methodId = query.CaptureId("method.name");

        var classes = new List<string>();
        var methods = new List<string>();

        cursor.ForEachMatch(query, tree.RootNode, match =>
        {
            if (match.TryGetNode(classId, out var declared))
            {
                classes.Add(declared.Text);
            }

            if (match.TryGetNode(methodId, out var method))
            {
                methods.Add(method.Text);
            }
        });

        Assert.Equal(["Greeter"], classes);
        Assert.Equal(["Greet", "Farewell"], methods);
    }

    /// <summary>
    /// The hazard the API is shaped around.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TSQueryMatch.captures</c> points into a buffer the cursor overwrites on
    /// every <c>ts_query_cursor_next_match</c>. A binding that handed out a match
    /// object callers could keep would return, for every match collected, whatever
    /// the <em>last</em> match left behind — three captures of "Farewell", say, with
    /// nothing anywhere reporting a problem.
    /// </para>
    /// <para>
    /// Here the captures are read inside the callback and the nodes are copied out
    /// by value, so reading them after the cursor has advanced past every match is
    /// still correct. If the buffer-reuse bug were present this test would collapse
    /// the two names into one repeated name. Keeping the <see cref="QueryMatch"/>
    /// itself is not testable, because <c>ref struct</c> makes it a compile error.
    /// </para>
    /// </remarks>
    [Fact]
    public void CapturedNodes_StayCorrectAfterTheCursorHasAdvancedPastTheirMatch()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);
        using var query = Query.Compile(Grammars.CSharp, Methods);
        using var cursor = new QueryCursor();

        var methodId = query.CaptureId("method.name");

        var captured = new List<Node>();
        var textsDuringIteration = new List<string>();

        cursor.ForEachMatch(query, tree.RootNode, match =>
        {
            Assert.Equal(1, match.CaptureCount);
            Assert.Equal(methodId, match.CaptureIdAt(0));

            textsDuringIteration.Add(match.NodeAt(0).Text);
            captured.Add(match.NodeAt(0));
        });

        Assert.Equal(["Greet", "Farewell"], textsDuringIteration);

        // Read only now, with the cursor exhausted and its buffer holding at most
        // the final match.
        Assert.Equal(["Greet", "Farewell"], captured.Select(node => node.Text));

        // And the spans still index the tree's buffer, not a copy that moved.
        Assert.Equal((uint)Source.IndexOf("Greet(", StringComparison.Ordinal), captured[0].StartByte);
    }

    /// <summary>
    /// Running one query over one declaration rather than the whole file, which is
    /// how a within-a-symbol lookup avoids a second tree.
    /// </summary>
    [Fact]
    public void ByteRange_LimitsMatchesToTheRequestedSpan()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);
        using var query = Query.Compile(Grammars.CSharp, Methods);
        using var cursor = new QueryCursor();

        var methodId = query.CaptureId("method.name");
        var second = Find.All(tree.RootNode, "method_declaration")[1];

        Assert.Equal(["Greet", "Farewell"], Run(null));
        Assert.Equal(["Farewell"], Run((second.StartByte, second.EndByte)));

        // A cursor carries its range between executions, so the unranged run after a
        // ranged one has to see the whole file again -- otherwise reusing a cursor
        // across files silently returns a subset.
        Assert.Equal(["Greet", "Farewell"], Run(null));

        List<string> Run((uint Start, uint End)? range)
        {
            var names = new List<string>();
            void Collect(QueryMatch match)
            {
                if (match.TryGetNode(methodId, out var node))
                {
                    names.Add(node.Text);
                }
            }

            if (range is { } bounds)
            {
                cursor.ForEachMatch(query, tree.RootNode, bounds.Start, bounds.End, Collect);
            }
            else
            {
                cursor.ForEachMatch(query, tree.RootNode, Collect);
            }

            return names;
        }
    }

    /// <summary>
    /// A rejected query has to say where. "NodeType" alone names the mistake but not
    /// which of thirty patterns made it.
    /// </summary>
    [Fact]
    public void AQueryNamingANodeTypeTheGrammarDoesNotHave_ReportsOffsetAndErrorType()
    {
        const string pattern = "(no_such_node) @x";

        var error = Assert.Throws<QueryCompilationException>(() => Query.Compile(Grammars.CSharp, pattern));

        Assert.Equal(TSQueryError.NodeType, error.Error);
        Assert.Equal((uint)pattern.IndexOf("no_such_node", StringComparison.Ordinal), error.ByteOffset);
    }

    [Fact]
    public void AQueryNamingAFieldTheGrammarDoesNotHave_ReportsOffsetAndErrorType()
    {
        const string pattern = "(method_declaration nosuchfield: (identifier) @n)";

        var error = Assert.Throws<QueryCompilationException>(() => Query.Compile(Grammars.CSharp, pattern));

        Assert.Equal(TSQueryError.Field, error.Error);
        Assert.Equal((uint)pattern.IndexOf("nosuchfield", StringComparison.Ordinal), error.ByteOffset);
    }

    [Fact]
    public void AQueryThatDoesNotParse_ReportsASyntaxError()
    {
        var error = Assert.Throws<QueryCompilationException>(
            () => Query.Compile(Grammars.CSharp, "(method_declaration name: (identifier) @n"));

        Assert.Equal(TSQueryError.Syntax, error.Error);
    }

    /// <summary>
    /// Predicates are evaluated by default now, so "the method called Greet" means
    /// what it reads. <see cref="QueryPredicateTests"/> covers the forms; this covers
    /// the default.
    /// </summary>
    [Fact]
    public void AQueryWithPredicates_IsEvaluatedByDefault()
    {
        const string pattern =
            """
            ((method_declaration name: (identifier) @name)
             (#eq? @name "Greet"))
            """;

        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);
        using var query = Query.Compile(Grammars.CSharp, pattern);
        using var cursor = new QueryCursor();

        Assert.True(query.HasPredicates);
        Assert.True(query.PatternHasPredicates(0));

        var nameId = query.CaptureId("name");
        var names = new List<string>();
        cursor.ForEachMatch(query, tree.RootNode, match =>
        {
            if (match.TryGetNode(nameId, out var node))
            {
                names.Add(node.Text);
            }
        });

        Assert.Equal(["Greet"], names);
    }

    /// <summary>
    /// The strictness a caller opts into when a query source is meant to stay
    /// structural — a predicate appearing in one should then be a change somebody
    /// notices rather than a change in what the query means.
    /// </summary>
    [Fact]
    public void AQueryWithPredicates_IsRefusedWhenTheCallerAsks_NamingThePredicates()
    {
        const string pattern =
            """
            ((method_declaration name: (identifier) @name)
             (#eq? @name "Greet"))
            """;

        var error = Assert.Throws<PredicateRefusedException>(
            () => Query.Compile(Grammars.CSharp, pattern, PredicateHandling.Reject));

        Assert.Equal(0u, error.PatternIndex);
        Assert.Equal(["#eq?"], error.PredicateNames);
    }

    [Fact]
    public void AQueryWithoutPredicates_ReportsNone()
    {
        using var query = Query.Compile(Grammars.CSharp, Methods);

        Assert.False(query.HasPredicates);
        Assert.False(query.PatternHasPredicates(0));
    }

    /// <summary>
    /// Node type ids are per-grammar, so a query run over the wrong language does not
    /// fail — it matches whatever rules happen to share those ids.
    /// </summary>
    [Fact]
    public void AQueryCompiledForAnotherGrammar_IsRefused()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);
        using var query = Query.Compile(Grammars.TypeScript, "(function_declaration) @function");
        using var cursor = new QueryCursor();

        Assert.Throws<ArgumentException>(() => cursor.ForEachMatch(query, tree.RootNode, _ => { }));
    }

    [Fact]
    public void QueryAndCursor_AreSafeToDisposeTwice()
    {
        var query = Query.Compile(Grammars.CSharp, Methods);
        var cursor = new QueryCursor();

        query.Dispose();
        query.Dispose();
        cursor.Dispose();
        cursor.Dispose();
    }

    [Fact]
    public void UsingADisposedQueryOrCursor_Throws()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);

        var query = Query.Compile(Grammars.CSharp, Methods);
        var cursor = new QueryCursor();

        query.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = query.CaptureId("method.name"); });
        Assert.Throws<ObjectDisposedException>(() => { _ = query.CaptureName(0); });
        Assert.Throws<ObjectDisposedException>(() => cursor.ForEachMatch(query, tree.RootNode, _ => { }));

        using var live = Query.Compile(Grammars.CSharp, Methods);
        cursor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cursor.ForEachMatch(live, tree.RootNode, _ => { }));
    }
}
