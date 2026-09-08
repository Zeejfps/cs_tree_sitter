using System.Text;

namespace TreeSitter.Tests;

/// <summary>
/// A whole tree rendered as text, so two trees can be compared in one assertion.
/// </summary>
/// <remarks>
/// Everything a wrong edit could shift goes into the string — type, byte span and
/// both points, for every node including the anonymous ones. That breadth is the
/// point: an incrementally re-parsed tree that is wrong is still a tree, and
/// answers every question plausibly, so the only assertion that catches it is
/// "identical to a from-scratch parse of the same bytes".
/// </remarks>
internal static class TreeShape
{
    internal static string Of(SyntaxTree tree) => Of(tree.RootNode);

    internal static string Of(Node root)
    {
        var shape = new StringBuilder();
        Walk(root, 0);
        return shape.ToString();

        void Walk(Node node, int depth)
        {
            var start = node.StartPoint;
            var end = node.EndPoint;

            shape.Append(' ', depth * 2)
                .Append(node.Type)
                .Append(' ')
                .Append(node.StartByte)
                .Append("..")
                .Append(node.EndByte)
                .Append(" (")
                .Append(start.Row)
                .Append(',')
                .Append(start.Column)
                .Append(")..(")
                .Append(end.Row)
                .Append(',')
                .Append(end.Column)
                .Append(")\n");

            foreach (var child in node.Children)
            {
                Walk(child, depth + 1);
            }
        }
    }
}
