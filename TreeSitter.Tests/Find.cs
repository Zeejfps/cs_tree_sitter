namespace TreeSitter.Tests;

/// <summary>
/// Tree searches the tests need and the wrapper deliberately does not provide.
/// </summary>
/// <remarks>
/// A general "find every node of type X" walk belongs above this layer, where it can
/// be a query instead of a recursion. It exists here only so assertions can name a
/// node without counting child indices.
/// </remarks>
internal static class Find
{
    /// <summary>Depth-first search for the first named node of a given grammar type.</summary>
    internal static Node First(Node node, string type)
    {
        var found = FirstOrDefault(node, type);
        Assert.True(found.HasValue, $"No '{type}' node in this tree.");
        return found!.Value;
    }

    internal static Node? FirstOrDefault(Node node, string type)
    {
        if (node.Type == type)
        {
            return node;
        }

        foreach (var child in node.NamedChildren)
        {
            if (FirstOrDefault(child, type) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Every named node of a given grammar type, in document order.</summary>
    internal static List<Node> All(Node node, string type)
    {
        var found = new List<Node>();
        Walk(node);
        return found;

        void Walk(Node current)
        {
            if (current.Type == type)
            {
                found.Add(current);
            }

            foreach (var child in current.NamedChildren)
            {
                Walk(child);
            }
        }
    }
}
