using System.Runtime.InteropServices;

namespace TreeSitter.Bindings.Tests;

/// <summary>
/// The bundled grammars, resolved by exported name out of the single
/// <c>tree-sitter-grammars</c> library.
/// </summary>
/// <remarks>
/// A <c>TSLanguage</c> is never freed: it points into the library's data segment,
/// and for a test process the right lifetime is the process. Each is loaded once
/// and shared, because a grammar is immutable and the parsers built on it are not.
/// </remarks>
internal static class Grammars
{
    internal static nint CSharp { get; } = Load("c_sharp");

    internal static nint TypeScript { get; } = Load("typescript");

    internal static nint Tsx { get; } = Load("tsx");

    internal static unsafe nint Load(string name)
    {
        var library = NativeLibrary.Load(
            "tree-sitter-grammars", typeof(Grammars).Assembly, searchPath: null);

        // Every grammar exports the same nullary accessor returning its static
        // TSLanguage. Cdecl is explicit because the C declaration carries no
        // annotation and the default differs by platform.
        var accessor = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(library, $"tree_sitter_{name}");
        return accessor();
    }
}
