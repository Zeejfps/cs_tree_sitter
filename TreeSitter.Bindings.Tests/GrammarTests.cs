using static TreeSitter.Bindings.Tests.Syntax;

namespace TreeSitter.Bindings.Tests;

/// <summary>
/// The gate on the bundled grammar library, as distinct from the ABI it is reached
/// through.
/// </summary>
/// <remarks>
/// <para>
/// <c>tree-sitter-grammars</c> is one artifact holding every language, so the failures
/// worth catching are packaging failures: a grammar dropped from the build, two
/// grammars from one checkout where only one got compiled, a version bump that
/// moves the ABI out of the runtime's range. Each of those shows up as a missing
/// export or a refused language, not as a bad parse.
/// </para>
/// <para>
/// The parses here are correspondingly shallow — enough to prove the right grammar
/// answered.
/// </para>
/// </remarks>
public class GrammarTests
{
    public static TheoryData<string> BundledGrammars => new("c_sharp", "typescript", "tsx");

    /// <summary>
    /// Load and ABI in one assertion, because a grammar that loads but is refused
    /// is the same outage as one that is missing.
    /// </summary>
    [Theory]
    [MemberData(nameof(BundledGrammars))]
    public void EveryBundledGrammar_IsExportedAndAcceptedByTheRuntime(string name)
    {
        var language = Grammars.Load(name);
        Assert.NotEqual(nint.Zero, language);

        var parser = TS.ts_parser_new();
        try
        {
            Assert.True(
                TS.ts_parser_set_language(parser, language),
                $"The runtime rejected the {name} grammar (language ABI version " +
                $"{TS.ts_language_abi_version(language)}). Check the pins in native/build.cs.");
        }
        finally
        {
            TS.ts_parser_delete(parser);
        }
    }

    [Fact]
    public void TypeScript_ParsesADeclarationWithItsTypeAnnotations()
    {
        const string source =
            """
            export function greet(name: string): string {
              return `Hello ${name}`;
            }
            """;

        WithParsedSource(Grammars.TypeScript, source, (root, bytes) =>
        {
            Assert.False(TS.ts_node_has_error(root));

            var function = FindFirst(root, "function_declaration");
            Assert.Equal("greet", TextOf(ChildByField(function, "name"u8), bytes));

            // The type annotation is what separates this grammar from the
            // JavaScript one it extends.
            Assert.Equal("(name: string)", TextOf(ChildByField(function, "parameters"u8), bytes));
            Assert.Equal(": string", TextOf(ChildByField(function, "return_type"u8), bytes));
        });
    }

    /// <summary>
    /// Why TSX is a separate grammar and not an extension mapping.
    /// </summary>
    /// <remarks>
    /// <c>&lt;T&gt;(x)</c> is a type assertion in TypeScript and an element in TSX;
    /// the two readings cannot coexist in one parser, so the same bytes have to go
    /// to different grammars. If this ever passes under
    /// <see cref="Grammars.TypeScript"/>, the file-extension routing above the
    /// parser is free to collapse — until then it is load bearing.
    /// </remarks>
    [Fact]
    public void Tsx_ParsesJsxThatTheTypeScriptGrammarCannot()
    {
        const string source = "const view = <div className=\"row\">{label}</div>;\n";

        WithParsedSource(Grammars.Tsx, source, (root, bytes) =>
        {
            Assert.False(TS.ts_node_has_error(root));

            var element = FindFirst(root, "jsx_element");
            Assert.False(TS.ts_node_is_null(element));
            Assert.Equal("<div className=\"row\">{label}</div>", TextOf(element, bytes));
        });

        WithParsedSource(Grammars.TypeScript, source, (root, _) =>
            Assert.True(
                TS.ts_node_has_error(root),
                "The typescript grammar parsed JSX cleanly, so the separate tsx grammar has stopped earning its place."));
    }

    /// <summary>
    /// The two TypeScript grammars come out of one checkout and one link step, and
    /// a build that compiled the same subdirectory twice would still export both
    /// names. Distinct <c>TSLanguage</c> pointers are the cheap proof it did not.
    /// </summary>
    [Fact]
    public void TypeScriptAndTsx_AreDistinctLanguagesFromOneCheckout()
    {
        Assert.NotEqual(Grammars.TypeScript, Grammars.Tsx);
    }
}
