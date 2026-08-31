using System.Text;

namespace TreeSitter.Tests;

/// <summary>
/// The wrapper's half of the contract: ownership, lifetime, and the byte offsets
/// that only mean something while the buffer they index is still here.
/// </summary>
/// <remarks>
/// <see cref="Bindings.Tests"/> already pins the ABI — that a span comes back
/// correct at all. These tests are about what this layer adds on top: that the
/// bytes stay reachable for as long as the tree, that a released handle is refused
/// rather than followed, and that nothing here quietly re-derives an offset in
/// characters.
/// </remarks>
public class SyntaxTreeTests
{
    private const string Source =
        "public class Greeter\n" +
        "{\n" +
        "    /// <summary>Greets by name.</summary>\n" +
        "    public string Greet(string name) => \"Hello\";\n" +
        "\n" +
        "    public string Farewell(string name) => \"Bye\";\n" +
        "}\n";

    [Fact]
    public void RootNode_SpansTheWholeFile()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);

        var root = tree.RootNode;

        Assert.Equal("compilation_unit", root.Type);
        Assert.Equal(0u, root.StartByte);
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(Source), root.EndByte);
        Assert.False(root.HasError);
        Assert.Null(root.Parent);
    }

    [Fact]
    public void Node_ReadsItsOwnText_FromTheBufferTheTreeOwns()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);

        var method = Find.First(tree.RootNode, "method_declaration");

        Assert.Equal("public string Greet(string name) => \"Hello\";", method.Text);
        Assert.Equal("Greet", method.ChildByFieldName("name"u8)?.Text);
        Assert.Equal("Greet", method.ChildByFieldName("name")?.Text);
    }

    /// <summary>
    /// The invariant this whole layer exists for. The caller's array is not the
    /// tree's array, so nothing the caller does to it afterwards can change what a
    /// node reads.
    /// </summary>
    [Fact]
    public void Parse_CopiesTheSource_SoMutatingTheCallersBufferCannotCorruptNodeText()
    {
        var bytes = Encoding.UTF8.GetBytes("class Greeter { }");

        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(bytes);

        // Whatever the caller does next -- reuse the buffer for the next file, clear
        // it, hand it back to a pool -- the tree's spans still index its own copy.
        Array.Clear(bytes);

        Assert.Equal("Greeter", Find.First(tree.RootNode, "class_declaration").ChildByFieldName("name")?.Text);
    }

    /// <summary>
    /// A tree is independent of the parser that produced it — <c>ts_tree_delete</c>
    /// releases through a pool it creates for itself, never the parser's — so there
    /// is no ordering requirement between the two handles and no finalizer hazard.
    /// Asserted rather than assumed, because the whole ownership design rests on it.
    /// </summary>
    [Fact]
    public void Tree_OutlivesTheParserThatProducedIt()
    {
        var parser = new Parser(Grammars.CSharp);
        var tree = parser.Parse(Source);
        parser.Dispose();

        // Reading through the tree after the parser is gone must be a normal read,
        // not a use-after-free that happens to look right.
        var method = Find.First(tree.RootNode, "method_declaration");
        Assert.Equal("Greet", method.ChildByFieldName("name")?.Text);

        tree.Dispose();
    }

    /// <summary>
    /// Reconstructing text from a node's span has to agree with the node's own text,
    /// for every node in the tree. A span measured against the wrong buffer, or an
    /// offset quietly read as characters, disagrees somewhere.
    /// </summary>
    [Fact]
    public void EveryNodesSpan_RoundTripsThroughTheTreesSourceBuffer()
    {
        const string multiByte =
            "public class Café\n" +
            "{\n" +
            "    public string Naïve() => \"日本語\";\n" +
            "}\n";

        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(multiByte);

        var checkedNodes = 0;
        Walk(tree.RootNode);

        // Guards against a walk that silently visited nothing and passed.
        Assert.True(checkedNodes > 10, $"Only {checkedNodes} nodes were checked.");

        void Walk(Node node)
        {
            var fromSpan = Encoding.UTF8.GetString(tree.Source[(int)node.StartByte..(int)node.EndByte]);
            Assert.Equal(node.Text, fromSpan);
            checkedNodes++;

            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }
    }

    /// <summary>
    /// The byte-versus-character distinction, which survives a round trip through
    /// this layer only because the tree keeps bytes rather than a string.
    /// </summary>
    /// <remarks>
    /// The opening quote is at character 21 and byte 21, but <c>é</c> is two bytes in
    /// UTF-8, so the literal ends at byte column 25 where a character count would say
    /// 24. A wrapper that decoded to UTF-16 on parse and indexed the string would
    /// report 24 here and be wrong for every non-ASCII file in the repo.
    /// </remarks>
    [Fact]
    public void Points_AreMeasuredInBytes_AndTextIsStillDecodedCorrectly()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse("class C { string s = \"é\"; }");

        var literal = Find.First(tree.RootNode, "string_literal");

        Assert.Equal((0u, 21u), (literal.StartPoint.Row, literal.StartPoint.Column));
        Assert.Equal((0u, 25u), (literal.EndPoint.Row, literal.EndPoint.Column));
        Assert.Equal(21u, literal.StartByte);
        Assert.Equal(25u, literal.EndByte);

        // Four bytes, three characters. Both numbers are correct; they are just not
        // the same number.
        Assert.Equal(4u, literal.EndByte - literal.StartByte);
        Assert.Equal("\"é\"", literal.Text);
        Assert.Equal(3, literal.Text.Length);
    }

    [Fact]
    public void NamedChildren_AreExactlyTheChildrenThatReportThemselvesNamed()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);

        var method = Find.First(tree.RootNode, "method_declaration");

        var named = new List<string>();
        foreach (var child in method.Children)
        {
            if (child.IsNamed)
            {
                named.Add(child.Type);
            }
        }

        var declared = new List<string>();
        foreach (var child in method.NamedChildren)
        {
            declared.Add(child.Type);
        }

        Assert.Equal(declared, named);

        // The trailing ';' is why both enumerations exist: a child, and not a named
        // one.
        Assert.Equal(method.NamedChildren.Count + 1, method.Children.Count);
    }

    [Fact]
    public void ChildrenIndexer_RefusesAnIndexPastTheEnd_RatherThanReturningTheNullNode()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);

        var children = Find.First(tree.RootNode, "method_declaration").NamedChildren;

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = children[children.Count]; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = children[-1]; });
    }

    /// <summary>
    /// Lookups that find nothing come back as <see langword="null"/>, not as
    /// tree-sitter's null node — which would answer <c>Type</c> with <c>"NULL"</c>
    /// and every other call with something plausible.
    /// </summary>
    [Fact]
    public void MissingLookups_AreNull()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);

        var methods = Find.All(tree.RootNode, "method_declaration");
        Assert.Equal(2, methods.Count);

        Assert.Null(Find.First(tree.RootNode, "method_declaration").ChildByFieldName("condition"));
        Assert.Null(tree.RootNode.Parent);
        Assert.Null(tree.RootNode.NamedChildren[0].PreviousNamedSibling);
    }

    /// <summary>
    /// How docstrings are reached: the comment is a sibling in front of the
    /// declaration, so what precedes an undocumented method is the previous method
    /// rather than nothing.
    /// </summary>
    [Fact]
    public void PreviousNamedSibling_ReachesTheDocCommentInFrontOfADeclaration()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(Source);

        var methods = Find.All(tree.RootNode, "method_declaration");

        var documented = methods[0].PreviousNamedSibling;
        Assert.Equal("comment", documented?.Type);
        Assert.Equal("/// <summary>Greets by name.</summary>", documented?.Text);

        Assert.Equal("method_declaration", methods[1].PreviousNamedSibling?.Type);
    }

    [Fact]
    public void HasError_ReportsDamageWithoutLosingTheRestOfTheFile()
    {
        const string broken =
            "public class Greeter\n" +
            "{\n" +
            "    public string Greet(string name => \"Hello\";\n" +  // no closing paren
            "    public int Count => 1;\n" +
            "}\n";

        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(broken);

        Assert.True(tree.RootNode.HasError);

        var intact = Find.First(tree.RootNode, "property_declaration");
        Assert.False(intact.HasError);
        Assert.Equal("public int Count => 1;", intact.Text);
    }

    [Fact]
    public void Parse_HandlesAnEmptyFile()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var tree = parser.Parse(string.Empty);

        Assert.Equal(0u, tree.RootNode.EndByte);
        Assert.Equal(string.Empty, tree.RootNode.Text);
    }

    [Fact]
    public void Parse_OfAStringAndOfItsBytes_AgreeOnSpans()
    {
        using var parser = new Parser(Grammars.CSharp);
        using var fromText = parser.Parse(Source);
        using var fromBytes = parser.Parse(Encoding.UTF8.GetBytes(Source));

        Assert.Equal(fromText.RootNode.EndByte, fromBytes.RootNode.EndByte);
        Assert.Equal(
            Find.First(fromText.RootNode, "method_declaration").Text,
            Find.First(fromBytes.RootNode, "method_declaration").Text);
    }

    /// <summary>
    /// A default-constructed <see cref="Node"/> belongs to no tree, and says so
    /// instead of calling into tree-sitter with a zeroed struct.
    /// </summary>
    [Fact]
    public void DefaultNode_RefusesEveryRead()
    {
        Node node = default;

        Assert.Throws<InvalidOperationException>(() => { _ = node.Type; });
    }

    [Fact]
    public void ParserAndTree_AreSafeToDisposeTwice()
    {
        var parser = new Parser(Grammars.CSharp);
        var tree = parser.Parse(Source);

        tree.Dispose();
        tree.Dispose();
        parser.Dispose();
        parser.Dispose();
    }

    /// <summary>
    /// The failure this replaces is the dangerous one: without the check, a node read
    /// after its tree is released walks memory the allocator has taken back, and
    /// returns plausible garbage rather than faulting.
    /// </summary>
    [Fact]
    public void NodeReadsAfterTheTreeIsDisposed_Throw()
    {
        var parser = new Parser(Grammars.CSharp);
        var tree = parser.Parse(Source);
        var method = Find.First(tree.RootNode, "method_declaration");

        tree.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = method.Type; });
        Assert.Throws<ObjectDisposedException>(() => { _ = method.StartByte; });
        Assert.Throws<ObjectDisposedException>(() => { _ = method.Text; });
        Assert.Throws<ObjectDisposedException>(() => { _ = method.Parent; });
        Assert.Throws<ObjectDisposedException>(() => { _ = method.ChildByFieldName("name"); });
        Assert.Throws<ObjectDisposedException>(() => { _ = method.NamedChildren.Count; });
        Assert.Throws<ObjectDisposedException>(() => { _ = tree.RootNode; });
        Assert.Throws<ObjectDisposedException>(() => { _ = tree.Source.Length; });

        parser.Dispose();
    }

    [Fact]
    public void ParsingWithADisposedParser_Throws()
    {
        var parser = new Parser(Grammars.CSharp);
        parser.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = parser.Parse(Source); });
    }
}
