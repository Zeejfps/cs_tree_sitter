using System.Runtime.InteropServices;

namespace TreeSitter.Bindings;

/// <summary>
/// Mirrors <c>TSPoint</c>: a zero-based row, and a column measured in bytes
/// rather than characters.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TSPoint
{
    public uint Row;
    public uint Column;
}
