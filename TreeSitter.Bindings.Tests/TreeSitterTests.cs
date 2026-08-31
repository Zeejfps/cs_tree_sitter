using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static TreeSitter.Bindings.Tests.Syntax;

namespace TreeSitter.Bindings.Tests;

/// <summary>
/// The gate on the tree-sitter ABI.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TSNode"/> crosses the boundary by value. If our declaration
/// disagrees with the C one, nothing fails: calls return, spans come back, and
/// they point at the wrong bytes. Every span a caller reads would be subtly wrong
/// with no exception anywhere.
/// </para>
/// <para>
/// So these tests do not check that parsing "works". They pin exact byte offsets
/// and exact row/column pairs against a known snippet, and reconstruct the source
/// text from the spans — the one assertion a shifted struct field cannot survive.
/// </para>
/// </remarks>
public class TreeSitterTests
{
    /// <summary>
    /// Rows and columns below are hand-counted against this, so it is spelled with
    /// explicit newlines rather than a raw literal whose line endings depend on how
    /// the file was checked out.
    /// </summary>
    private const string Source =
        "namespace Demo;\n" +                                     // row 0
        "\n" +                                                    // row 1
        "public class Greeter\n" +                                // row 2
        "{\n" +                                                   // row 3
        "    public string Greet(string name) => \"Hello\";\n" +  // row 4
        "}\n";                                                    // row 5

    private static readonly byte[] SourceBytes = Encoding.UTF8.GetBytes(Source);

    /// <summary>
    /// A second class, for the entry points that need more than one declaration to
    /// say anything: a byte range that excludes one method, a predicate that
    /// selects one of two names, a docstring attached to one of them.
    /// </summary>
    private const string TwoMethods =
        "public class Greeter\n" +
        "{\n" +
        "    /// <summary>Greets by name.</summary>\n" +
        "    public string Greet(string name) => \"Hello\";\n" +
        "\n" +
        "    public string Farewell(string name) => \"Bye\";\n" +
        "}\n";

    private static nint CSharpLanguage => Grammars.CSharp;

    [Fact]
    public void InteropStructs_MatchTheirCLayouts()
    {
        // uint32_t row, column
        Assert.Equal(8, Unsafe.SizeOf<TSPoint>());

        // uint32_t context[4]; const void *id; const TSTree *tree
        Assert.Equal(32, Unsafe.SizeOf<TSNode>());

        // TSNode node; uint32_t index -- plus padding to pointer alignment
        Assert.Equal(40, Unsafe.SizeOf<TSQueryCapture>());

        // uint32_t id; uint16_t pattern_index; uint16_t capture_count; const TSQueryCapture *captures
        Assert.Equal(16, Unsafe.SizeOf<TSQueryMatch>());

        // TSQueryPredicateStepType type; uint32_t value_id -- the enum is an int,
        // so this is two 32-bit fields and no padding. Read as an array straight
        // out of query memory, a wrong size here silently shifts every step.
        Assert.Equal(8, Unsafe.SizeOf<TSQueryPredicateStep>());
    }

    [Fact]
    public void TheGrammarAndTheRuntimeAgreeOnTheLanguageAbi()
    {
        var parser = TS.ts_parser_new();
        try
        {
            Assert.True(
                TS.ts_parser_set_language(parser, CSharpLanguage),
                $"The runtime rejected the c_sharp grammar (language ABI version " +
                $"{TS.ts_language_abi_version(CSharpLanguage)}). Check the pins in native/build.cs.");
        }
        finally
        {
            TS.ts_parser_delete(parser);
        }
    }

    [Fact]
    public void TheShimReportsTheRangeTheRuntimeActuallyApplies()
    {
        // The managed layer rejects grammars by this range instead of by building
        // a parser, so if the shim ever stopped agreeing with the runtime it would
        // refuse grammars that parse, or admit ones that cannot — silently, since
        // the version it reports is the version the exception would carry.
        var minimum = TS.ts_shim_min_compatible_language_abi_version();
        var maximum = TS.ts_shim_language_abi_version();

        Assert.InRange(minimum, 1u, maximum);

        var abiVersion = TS.ts_language_abi_version(CSharpLanguage);
        Assert.InRange(abiVersion, minimum, maximum);

        var parser = TS.ts_parser_new();
        try
        {
            Assert.True(
                TS.ts_parser_set_language(parser, CSharpLanguage),
                $"The shim calls ABI version {abiVersion} compatible ([{minimum}, {maximum}]) but the runtime " +
                "refused it, so native/shim.c is not compiled against the header the runtime was built from.");
        }
        finally
        {
            TS.ts_parser_delete(parser);
        }
    }

    [Fact]
    public void RootNode_SpansTheWholeFile()
    {
        WithParsedSource(CSharpLanguage, SourceBytes, root =>
        {
            Assert.Equal("compilation_unit", TypeOf(root));
            Assert.Equal(0u, TS.ts_node_start_byte(root));
            Assert.Equal((uint)SourceBytes.Length, TS.ts_node_end_byte(root));
            AssertPoint(0, 0, TS.ts_node_start_point(root));
        });
    }

    [Fact]
    public void MethodDeclaration_HasTheExactSpanOfItsSourceText()
    {
        const string declaration = "public string Greet(string name) => \"Hello\";";

        WithParsedSource(CSharpLanguage, SourceBytes, root =>
        {
            var method = FindFirst(root, "method_declaration");

            // The strongest check available: a shifted struct field cannot produce
            // a span that reads back as exactly the declaration.
            Assert.Equal(declaration, TextOf(method, SourceBytes));

            Assert.Equal(
                (uint)Source.IndexOf(declaration, StringComparison.Ordinal),
                TS.ts_node_start_byte(method));
            AssertPoint(4, 4, TS.ts_node_start_point(method));
            AssertPoint(4, 4 + (uint)declaration.Length, TS.ts_node_end_point(method));
        });
    }

    [Fact]
    public void ClassDeclaration_SpansFromItsModifierToItsClosingBrace()
    {
        WithParsedSource(CSharpLanguage, SourceBytes, root =>
        {
            var declaration = FindFirst(root, "class_declaration");

            AssertPoint(2, 0, TS.ts_node_start_point(declaration));
            AssertPoint(5, 1, TS.ts_node_end_point(declaration));
        });
    }

    [Fact]
    public void ChildByFieldName_ReadsTheDeclaredIdentifier()
    {
        WithParsedSource(CSharpLanguage, SourceBytes, root =>
        {
            var name = ChildByField(FindFirst(root, "method_declaration"), "name"u8);

            Assert.False(TS.ts_node_is_null(name));
            Assert.Equal("Greet", TextOf(name, SourceBytes));
        });
    }

    [Fact]
    public void ChildByFieldName_ReturnsTheNullNodeForAFieldTheNodeDoesNotHave()
    {
        WithParsedSource(CSharpLanguage, SourceBytes, root =>
        {
            var missing = ChildByField(FindFirst(root, "method_declaration"), "condition"u8);

            Assert.True(TS.ts_node_is_null(missing));
        });
    }

    [Fact]
    public void Parent_WalksBackToTheEnclosingDeclaration()
    {
        WithParsedSource(CSharpLanguage, SourceBytes, root =>
        {
            var enclosing = TS.ts_node_parent(FindFirst(root, "method_declaration"));
            while (!TS.ts_node_is_null(enclosing) && TypeOf(enclosing) != "class_declaration")
            {
                enclosing = TS.ts_node_parent(enclosing);
            }

            Assert.False(TS.ts_node_is_null(enclosing));
            Assert.Equal("Greeter", TextOf(ChildByField(enclosing, "name"u8), SourceBytes));
        });
    }

    [Fact]
    public void Columns_AreMeasuredInBytesNotCharacters()
    {
        // The opening quote is at character 21 and byte 21, but 'e' is two bytes in
        // UTF-8, so the literal ends at byte column 25 where a character count
        // would say 24. Anything that renders a caret has to account for this.
        var source = Encoding.UTF8.GetBytes("class C { string s = \"é\"; }");

        WithParsedSource(CSharpLanguage, source, root =>
        {
            var literal = FindFirst(root, "string_literal");

            AssertPoint(0, 21, TS.ts_node_start_point(literal));
            AssertPoint(0, 25, TS.ts_node_end_point(literal));
            Assert.Equal("\"é\"", TextOf(literal, source));
        });
    }

    /// <summary>
    /// The two child enumerations have to describe one tree. If the named
    /// enumeration were anything but the full one filtered, a walk that mixes them
    /// — which extraction does, taking fields off some nodes and scanning children
    /// of others — would see different trees depending on how it got there.
    /// </summary>
    [Fact]
    public void NamedChildren_AreExactlyTheChildrenThatReportThemselvesNamed()
    {
        WithParsedSource(CSharpLanguage, SourceBytes, root =>
        {
            var method = FindFirst(root, "method_declaration");

            var named = new List<string>();
            for (uint i = 0; i < TS.ts_node_child_count(method); i++)
            {
                var child = TS.ts_node_child(method, i);
                if (TS.ts_node_is_named(child))
                {
                    named.Add(TypeOf(child));
                }
            }

            var declared = new List<string>();
            for (uint i = 0; i < TS.ts_node_named_child_count(method); i++)
            {
                declared.Add(TypeOf(TS.ts_node_named_child(method, i)));
            }

            Assert.Equal(declared, named);

            // And the trailing ';' is the reason both enumerations exist: it is a
            // child of the declaration and not a named one.
            Assert.Equal(
                TS.ts_node_named_child_count(method) + 1,
                TS.ts_node_child_count(method));
        });
    }

    [Fact]
    public void Child_ReachesTheAnonymousTokensThatNamedChildSkips()
    {
        WithParsedSource(CSharpLanguage, SourceBytes, root =>
        {
            var parameters = FindFirst(root, "parameter_list");

            var tokens = new List<string>();
            for (uint i = 0; i < TS.ts_node_child_count(parameters); i++)
            {
                var child = TS.ts_node_child(parameters, i);
                if (!TS.ts_node_is_named(child))
                {
                    tokens.Add(TextOf(child, SourceBytes));
                }
            }

            Assert.Equal(["(", ")"], tokens);
        });
    }

    /// <summary>
    /// Docstrings are not part of the declaration they document, in any grammar we
    /// bundle — they are a sibling comment in front of it. This is the whole
    /// mechanism behind the <c>docstring</c> column.
    /// </summary>
    [Fact]
    public void PrevNamedSibling_ReachesTheDocCommentInFrontOfADeclaration()
    {
        WithParsedSource(CSharpLanguage, TwoMethods, (root, source) =>
        {
            var methods = FindAll(root, "method_declaration");
            Assert.Equal(2, methods.Count);

            var documented = TS.ts_node_prev_named_sibling(methods[0]);
            Assert.False(TS.ts_node_is_null(documented));
            Assert.Equal("comment", TypeOf(documented));
            Assert.Equal("/// <summary>Greets by name.</summary>", TextOf(documented, source));

            // The second method has no comment, and what precedes it is the first
            // method — so "is there a docstring" is a type check, never a
            // null check.
            var undocumented = TS.ts_node_prev_named_sibling(methods[1]);
            Assert.Equal("method_declaration", TypeOf(undocumented));
        });
    }

    [Fact]
    public void PrevNamedSibling_ReturnsTheNullNodeForTheFirstChild()
    {
        WithParsedSource(CSharpLanguage, SourceBytes, root =>
        {
            var first = TS.ts_node_named_child(root, 0);

            Assert.True(TS.ts_node_is_null(TS.ts_node_prev_named_sibling(first)));
        });
    }

    /// <summary>
    /// Half-written files are the normal case for an editor-adjacent index, so what
    /// matters is that a parse error is reported rather than thrown, and stays
    /// local: the intact declaration beside it still has to come back correct.
    /// </summary>
    [Fact]
    public void HasError_ReportsDamageWithoutLosingTheRestOfTheFile()
    {
        WithParsedSource(CSharpLanguage, SourceBytes, root =>
            Assert.False(TS.ts_node_has_error(root)));

        const string broken =
            "public class Greeter\n" +
            "{\n" +
            "    public string Greet(string name => \"Hello\";\n" +  // no closing paren
            "    public int Count => 1;\n" +
            "}\n";

        WithParsedSource(CSharpLanguage, broken, (root, source) =>
        {
            Assert.True(TS.ts_node_has_error(root));

            var intact = FindFirst(root, "property_declaration");
            Assert.False(TS.ts_node_is_null(intact));
            Assert.False(TS.ts_node_has_error(intact));
            Assert.Equal("public int Count => 1;", TextOf(intact, source));
        });
    }

    /// <summary>
    /// Exercises the query path end to end, which is the only thing that puts
    /// <see cref="TSQueryMatch"/> and <see cref="TSQueryCapture"/> under load — a
    /// wrong layout there corrupts captures the same way a wrong
    /// <see cref="TSNode"/> corrupts spans.
    /// </summary>
    [Fact]
    public unsafe void QueryCursor_ReturnsCapturesPointingAtTheCapturedNodes()
    {
        const string pattern = "(method_declaration name: (identifier) @method.name)";

        WithQuery(CSharpLanguage, pattern, query =>
        {
            Assert.Equal(1u, TS.ts_query_capture_count(query));
            Assert.Equal(1u, TS.ts_query_pattern_count(query));

            var namePointer = TS.ts_query_capture_name_for_id(query, 0, out var nameLength);
            Assert.Equal("method.name", Marshal.PtrToStringUTF8(namePointer, (int)nameLength));

            var cursor = TS.ts_query_cursor_new();
            try
            {
                WithParsedSource(CSharpLanguage, SourceBytes, root =>
                {
                    TS.ts_query_cursor_exec(cursor, query, root);

                    var captured = new List<string>();
                    while (TS.ts_query_cursor_next_match(cursor, out var match))
                    {
                        // Read before advancing: the cursor reuses this buffer.
                        var captures = new ReadOnlySpan<TSQueryCapture>((void*)match.Captures, match.CaptureCount);
                        foreach (var capture in captures)
                        {
                            Assert.Equal(0u, capture.Index);
                            captured.Add(TextOf(capture.Node, SourceBytes));
                        }
                    }

                    Assert.Equal(["Greet"], captured);
                });
            }
            finally
            {
                TS.ts_query_cursor_delete(cursor);
            }
        });
    }

    /// <summary>
    /// Restricting the cursor to one declaration's span, which is how a
    /// within-a-symbol query runs without a second tree.
    /// </summary>
    [Fact]
    public unsafe void QueryCursor_ByteRange_LimitsMatchesToTheRequestedSpan()
    {
        var source = Encoding.UTF8.GetBytes(TwoMethods);
        const string pattern = "(method_declaration name: (identifier) @name)";

        WithQuery(CSharpLanguage, pattern, query =>
        {
            WithParsedSource(CSharpLanguage, source, root =>
            {
                var second = FindAll(root, "method_declaration")[1];

                Assert.Equal(["Greet", "Farewell"], Run(query, root, null));

                Assert.Equal(
                    ["Farewell"],
                    Run(query, root, (TS.ts_node_start_byte(second), TS.ts_node_end_byte(second))));
            });
        });

        List<string> Run(nint query, TSNode root, (uint Start, uint End)? range)
        {
            var cursor = TS.ts_query_cursor_new();
            try
            {
                // Before exec, not after: the range is read when matching starts.
                if (range is { } bounds)
                {
                    Assert.True(TS.ts_query_cursor_set_byte_range(cursor, bounds.Start, bounds.End));
                }

                TS.ts_query_cursor_exec(cursor, query, root);

                var names = new List<string>();
                while (TS.ts_query_cursor_next_match(cursor, out var match))
                {
                    var captures = new ReadOnlySpan<TSQueryCapture>((void*)match.Captures, match.CaptureCount);
                    foreach (var capture in captures)
                    {
                        names.Add(TextOf(capture.Node, source));
                    }
                }

                return names;
            }
            finally
            {
                TS.ts_query_cursor_delete(cursor);
            }
        }
    }

    /// <summary>
    /// The predicate API, which is the one place tree-sitter parses something and
    /// then declines to act on it.
    /// </summary>
    /// <remarks>
    /// Upstream tag queries lean on <c>#eq?</c> and <c>#match?</c> heavily. A host
    /// that never reads these steps gets a query that silently means something
    /// broader than it says, so this pins the decoding: step types, which id space
    /// each one indexes, and the <c>Done</c> sentinel between predicates.
    /// </remarks>
    [Fact]
    public unsafe void Predicates_AreHandedBackAsStepsForTheHostToEvaluate()
    {
        // Two predicates on one pattern, so the Done sentinel between them is load
        // bearing rather than just a terminator.
        const string pattern =
            """
            ((method_declaration name: (identifier) @name) @method
             (#eq? @name "Greet")
             (#match? @name "^G"))
            """;

        WithQuery(CSharpLanguage, pattern, query =>
        {
            Assert.Equal(1u, TS.ts_query_pattern_count(query));

            var stepsPointer = TS.ts_query_predicates_for_pattern(query, 0, out var stepCount);
            var steps = new ReadOnlySpan<TSQueryPredicateStep>((void*)stepsPointer, (int)stepCount);

            var decoded = new List<string>();
            foreach (var step in steps)
            {
                decoded.Add(step.Type switch
                {
                    TSQueryPredicateStepType.Done => "|",
                    TSQueryPredicateStepType.Capture => "@" + Read(TS.ts_query_capture_name_for_id, step.ValueId),
                    TSQueryPredicateStepType.String => Read(TS.ts_query_string_value_for_id, step.ValueId),
                    var other => throw new InvalidOperationException($"Unknown step type {other}."),
                });
            }

            // The predicate's own name arrives as an ordinary string step, so
            // tree-sitter is not distinguishing #eq? from anything a query author
            // invents — that decision is entirely ours.
            Assert.Equal(["eq?", "@name", "Greet", "|", "match?", "@name", "^G", "|"], decoded);

            // Capture and string ids are separate spaces, and this pattern has both
            // @name and @method. Reading a string id out of the capture table would
            // land on a real name rather than fail.
            Assert.Equal(2u, TS.ts_query_capture_count(query));

            string Read(ReadName read, uint id)
            {
                var pointer = read(query, id, out var length);
                return Marshal.PtrToStringUTF8(pointer, (int)length)!;
            }
        });
    }

    /// <summary>A pattern with no predicates reports none rather than failing.</summary>
    [Fact]
    public void Predicates_AreEmptyForAPatternThatDeclaresNone()
    {
        WithQuery(CSharpLanguage, "(class_declaration) @class", query =>
        {
            TS.ts_query_predicates_for_pattern(query, 0, out var stepCount);

            Assert.Equal(0u, stepCount);
        });
    }

    /// <summary>
    /// The shape shared by the two id-to-text lookups, so one local can read
    /// either table.
    /// </summary>
    private delegate nint ReadName(nint query, uint id, out uint length);

    private static void AssertPoint(uint row, uint column, TSPoint actual)
    {
        Assert.Equal((row, column), (actual.Row, actual.Column));
    }
}
