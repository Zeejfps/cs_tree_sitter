using System.Reflection;
using System.Runtime.InteropServices;
using TreeSitter.Bindings;

namespace TreeSitter;

/// <summary>
/// A grammar: a <c>TSLanguage *</c>, plus the ABI check that decides whether this
/// runtime can use it.
/// </summary>
/// <remarks>
/// <para>
/// A grammar is immutable static data in its library's data segment. Nothing
/// allocates it and nothing frees it, so this is a value wrapping a pointer rather
/// than a <see cref="SafeHandle"/>: copying it is free, and sharing one across
/// parsers and threads is safe — a parser is the mutable part, a language is not.
/// </para>
/// <para>
/// <see cref="Load"/> resolves one out of a native library <em>by exported name</em>
/// rather than through a static <c>[LibraryImport]</c> per grammar. That choice is
/// what makes user-supplied grammars free: a library dropped in beside the bundled
/// ones takes exactly this code path. Deciding <em>which</em> libraries to look in,
/// and which file extensions a grammar claims, is grammar discovery and lives above
/// this layer.
/// </para>
/// </remarks>
public readonly struct Language
{
    /// <param name="handle">
    /// The result of a grammar's nullary accessor. Zero is rejected here rather than
    /// at <see cref="Parser"/> construction: a failed export lookup should name the
    /// grammar, and by the time a parser refuses a null pointer that context is gone.
    /// </param>
    /// <exception cref="IncompatibleLanguageException">
    /// This runtime cannot read the grammar's ABI. Checked here so that a
    /// <see cref="Language"/> which exists is a <see cref="Language"/> that parses,
    /// and the failure lands where the grammar can still be named.
    /// </exception>
    public Language(nint handle)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(handle, nint.Zero, nameof(handle));
        Handle = handle;
        ThrowIfIncompatible(TS.ts_language_abi_version(handle));
    }

    /// <summary>The raw <c>TSLanguage *</c>, for callers dropping to <see cref="TS"/>.</summary>
    public nint Handle { get; }

    /// <summary>
    /// The language ABI version this grammar was generated for.
    /// </summary>
    /// <remarks>
    /// Only interesting when the runtime refuses the grammar. tree-sitter reports
    /// that as a bare <see langword="false"/> from <c>ts_parser_set_language</c>,
    /// indistinguishable from every other failure until this number is attached to
    /// it — so <see cref="IncompatibleLanguageException"/> carries it and no caller
    /// has to remember to ask.
    /// </remarks>
    public uint AbiVersion => TS.ts_language_abi_version(Handle);

    /// <summary>
    /// Loads <paramref name="grammarName"/> out of <paramref name="libraryName"/> and
    /// proves the runtime accepts it.
    /// </summary>
    /// <param name="libraryName">
    /// Passed to <see cref="NativeLibrary.Load(string, Assembly, DllImportSearchPath?)"/>,
    /// so it takes the platform's decorations — <c>libtree-sitter-grammars.dylib</c>,
    /// <c>.so</c>, <c>.dll</c> — from a plain <c>tree-sitter-grammars</c>. Probing is
    /// relative to <paramref name="relativeTo"/>, which defaults to this assembly:
    /// the bundled library ships beside it.
    /// </param>
    /// <param name="grammarName">
    /// The grammar's own name — <c>c_sharp</c>, <c>typescript</c>, <c>tsx</c>. The
    /// exported symbol is <c>tree_sitter_</c> plus this.
    /// </param>
    /// <remarks>
    /// <para>
    /// The library is not unloaded and the handle is not kept. A grammar library is
    /// process-lifetime by nature — the <c>TSLanguage</c> returned points into its
    /// data segment, and every tree parsed with it holds a reference to that memory —
    /// so unloading it would invalidate live trees for no benefit. Repeat calls hit
    /// the loader's own cache.
    /// </para>
    /// <para>
    /// Compatibility is the constructor's business, so a grammar that will never work
    /// fails at load, naming itself, rather than at the first parse of the first file.
    /// </para>
    /// </remarks>
    /// <exception cref="IncompatibleLanguageException">
    /// The grammar loaded but the runtime refuses it — an ABI mismatch, reported
    /// with the version.
    /// </exception>
    public static unsafe Language Load(string libraryName, string grammarName, Assembly? relativeTo = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryName);
        ArgumentException.ThrowIfNullOrEmpty(grammarName);

        var library = NativeLibrary.Load(
            libraryName, relativeTo ?? typeof(Language).Assembly, searchPath: null);

        // Every grammar exports the same nullary accessor returning its static
        // TSLanguage. Cdecl is explicit because the C declaration carries no
        // annotation and the platform default is not the same everywhere.
        var export = NativeLibrary.GetExport(library, $"tree_sitter_{grammarName}");
        var accessor = (delegate* unmanaged[Cdecl]<nint>)export;

        return new Language(accessor());
    }

    /// <summary>
    /// Throws if <paramref name="abiVersion"/> is outside the range this runtime
    /// accepts, and does nothing if it is inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place that decides compatibility, so the constructor and
    /// <see cref="Parser"/> agree on the answer and on how it is reported. It takes
    /// the version rather than a <see cref="Language"/> because <see cref="Parser"/>
    /// reads it off a grammar the runtime has already refused, before anything can
    /// move.
    /// </para>
    /// <para>
    /// The range is read out of the runtime, not hardcoded: it lives in
    /// <c>api.h</c> as two macros, and <c>native/shim.c</c> compiles them into the
    /// exports called here. So this answers exactly what
    /// <c>ts_parser_set_language</c> would answer, without allocating a parser to
    /// ask, and a bump of the vendored runtime moves both at once.
    /// </para>
    /// </remarks>
    /// <exception cref="IncompatibleLanguageException">
    /// The grammar was generated by a tree-sitter CLI outside the accepted range.
    /// </exception>
    public static void ThrowIfIncompatible(uint abiVersion)
    {
        if (abiVersion < TS.ts_shim_min_compatible_language_abi_version()
            || abiVersion > TS.ts_shim_language_abi_version())
        {
            throw new IncompatibleLanguageException(abiVersion);
        }
    }
}
