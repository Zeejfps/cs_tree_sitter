namespace TreeSitter.Tests;

/// <summary>
/// <c>#set!</c> directives: the metadata half of what a pattern can carry beside its nodes.
/// </summary>
/// <remarks>
/// Injection queries are written almost entirely in directives — <c>(#set! injection.language
/// "css")</c> is how a pattern says which grammar its content belongs to — so a host that refuses
/// them can compile no upstream injections query at all.
/// </remarks>
public class QueryDirectiveTests
{
    private const string Pattern = "(method_declaration name: (identifier) @name)";

    [Fact]
    public void ASetDirective_IsReadBackByKey()
    {
        using var query = Query.Compile(Grammars.CSharp, $"""
            ({Pattern}
             (#set! injection.language "css"))
            """);

        Assert.True(query.TryGetProperty(0, "injection.language", out var value));
        Assert.Equal("css", value);
    }

    [Fact]
    public void AQuotedKeyIsTheSameKey()
    {
        using var query = Query.Compile(Grammars.CSharp, $"""
            ({Pattern}
             (#set! "injection.language" "css"))
            """);

        Assert.True(query.TryGetProperty(0, "injection.language", out var value));
        Assert.Equal("css", value);
    }

    [Fact]
    public void AValuelessFlagIsSetWithANullValue()
    {
        using var query = Query.Compile(Grammars.CSharp, $"""
            ({Pattern}
             (#set! injection.combined))
            """);

        Assert.True(query.TryGetProperty(0, "injection.combined", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void ADirectiveCanBeScopedToACapture()
    {
        using var query = Query.Compile(Grammars.CSharp, $"""
            ({Pattern}
             (#set! @name "priority" "105"))
            """);

        var property = Assert.Single(query.PropertiesFor(0));
        Assert.Equal("priority", property.Key);
        Assert.Equal("105", property.Value);
        Assert.Equal(query.CaptureId("name"), property.CaptureId);
    }

    [Fact]
    public void DirectivesAndPredicatesCoexistOnOnePattern()
    {
        using var query = Query.Compile(Grammars.CSharp, $"""
            ({Pattern}
             (#eq? @name "Greet")
             (#set! injection.language "css"))
            """);

        Assert.True(query.PatternHasPredicates(0));
        Assert.True(query.TryGetProperty(0, "injection.language", out _));
    }

    [Fact]
    public void APatternWithNoDirectivesHasNoProperties()
    {
        using var query = Query.Compile(Grammars.CSharp, Pattern);

        Assert.Empty(query.PropertiesFor(0));
        Assert.False(query.TryGetProperty(0, "injection.language", out _));
    }

    /// <summary>
    /// Same argument as the predicate refusals: a directive nobody reads leaves the pattern
    /// meaning something the file does not say.
    /// </summary>
    [Fact]
    public void AnUnrecognizedDirective_IsRefusedAtCompileTime()
    {
        var error = Assert.Throws<QueryPredicateException>(() => Query.Compile(
            Grammars.CSharp,
            $"""
            ({Pattern}
             (#offset! @name 0 1 0 -1))
            """));

        Assert.Equal("#offset!", error.PredicateName);
    }

    [Theory]
    [InlineData("""(#set!)""")]
    [InlineData("""(#set! @name)""")]
    [InlineData("""(#set! "a" "b" "c")""")]
    [InlineData("""(#set! "a" @name)""")]
    public void ASetDirectiveWithTheWrongArgumentsIsRefused(string directive)
    {
        Assert.Throws<QueryPredicateException>(() => Query.Compile(
            Grammars.CSharp,
            $"({Pattern}\n {directive})"));
    }
}
