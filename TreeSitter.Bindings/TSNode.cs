using System.Runtime.InteropServices;

namespace TreeSitter.Bindings;

/// <summary>
/// Mirrors <c>TSNode</c>, which tree-sitter passes and returns <em>by value</em>.
/// </summary>
/// <remarks>
/// <para>
/// This is the one type here where a mistake is silent. Nothing validates the
/// layout at the ABI boundary: get it wrong and calls still succeed, returning
/// spans that point at the wrong bytes. The upstream declaration is
/// </para>
/// <code>
/// typedef struct TSNode {
///   uint32_t context[4];
///   const void *id;
///   const TSTree *tree;
/// } TSNode;
/// </code>
/// <para>
/// The context array is four discrete fields rather than a fixed buffer: the
/// layout is identical, callers never index it, and it keeps the type blittable
/// without an unsafe context.
/// </para>
/// <para>
/// A node is a view into its tree and is only valid while that tree is alive.
/// <c>Id</c> is null for the null node that lookups return when they find nothing.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct TSNode
{
    public uint Context0;
    public uint Context1;
    public uint Context2;
    public uint Context3;
    public nint Id;
    public nint Tree;
}
