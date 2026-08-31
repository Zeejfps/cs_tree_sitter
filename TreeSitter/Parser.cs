using System.Text;

using TreeSitter.Bindings;

namespace TreeSitter;

/// <summary>
/// A parser bound to one <see cref="Language"/>, producing a
/// <see cref="SyntaxTree"/> per file.
/// </summary>
/// <remarks>
/// <para>
/// A parser is the mutable, single-threaded half of tree-sitter and a language is
/// the immutable half, so the useful shape is one parser per worker rather than one
/// per file: <c>ts_parser_new</c> allocates a stack and a subtree pool that get
/// reused across parses, and re-creating it per file throws that away. Never share
/// one across threads — <c>Parallel.ForEachAsync</c> over files wants a parser per
/// partition.
/// </para>
/// <para>
/// The language is fixed at construction. tree-sitter allows re-setting it, but a
/// parser that changes language mid-life makes the trees it produced ambiguous
/// about which grammar's node types they carry, and nothing here needs it.
/// </para>
/// </remarks>
public sealed class Parser : IDisposable
{
    private readonly ParserHandle _handle;

    /// <exception cref="IncompatibleLanguageException">
    /// The runtime refused the grammar. Reported with its ABI version, because that
    /// is the only number that says which side to re-pin.
    /// </exception>
    public Parser(Language language)
    {
        _handle = ParserHandle.Create();
        if (_handle.IsInvalid)
        {
            throw new TreeSitterException("ts_parser_new returned NULL, which means the allocator failed.");
        }

        if (!TS.ts_parser_set_language(_handle.DangerousGetHandle(), language.Handle))
        {
            // The parser keeps its previous language — none — so it is unusable.
            // Release it here rather than leaving a half-built object for a
            // finalizer, and read the ABI version before anything else can move.
            var abiVersion = language.AbiVersion;
            _handle.Dispose();

            // An ABI mismatch is the documented reason for a refusal, and
            // Language rejects those at construction, so reaching here means the
            // runtime refused a grammar whose version it reports as acceptable.
            Language.ThrowIfIncompatible(abiVersion);
            throw new TreeSitterException(
                $"ts_parser_set_language refused a grammar whose ABI version ({abiVersion}) is inside the range " +
                $"[{TS.ts_shim_min_compatible_language_abi_version()}, {TS.ts_shim_language_abi_version()}] " +
                "the runtime reports as compatible.");
        }

        GC.KeepAlive(_handle);
        Language = language;
    }

    /// <summary>The grammar every tree from this parser was produced by.</summary>
    public Language Language { get; }

    /// <summary>
    /// Parses UTF-8 bytes into a tree that owns its own copy of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The copy is the point.</b> tree-sitter does not retain the buffer it is
    /// handed, and every span it later reports is a byte offset into it, so text
    /// for a node can only be recovered from bytes the caller kept. Aliasing the
    /// caller's array instead would leave the tree correct only until that array
    /// was reused or mutated — a failure that shows up as wrong symbol text much
    /// later, on some other file. One array copy per file is nothing beside the
    /// parse.
    /// </para>
    /// <para>
    /// Takes bytes rather than a <see cref="string"/> because that is what a file
    /// read produces and what the spans index. Decoding to UTF-16 up front would
    /// mean re-encoding to answer any question about offsets.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The parser has been disposed.</exception>
    public unsafe SyntaxTree Parse(ReadOnlySpan<byte> utf8Source)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        var owned = utf8Source.ToArray();

        nint tree;
        fixed (byte* pinned = owned)
        {
            // An empty file pins to null. tree-sitter's string reader would never
            // dereference it at length zero, but relying on that is relying on an
            // implementation detail of a library we do not control.
            byte empty = 0;
            tree = TS.ts_parser_parse_string(
                _handle.DangerousGetHandle(),
                nint.Zero,
                pinned is null ? &empty : pinned,
                (uint)owned.Length);
        }

        GC.KeepAlive(_handle);

        if (tree == nint.Zero)
        {
            // Not the syntax-error path: a file that does not parse comes back as a
            // tree full of ERROR nodes. NULL means the parser had no language or
            // was cancelled, neither of which this type allows.
            throw new TreeSitterException("ts_parser_parse_string returned NULL.");
        }

        return new SyntaxTree(TreeHandle.Adopt(tree), owned, Language);
    }

    /// <summary>
    /// Convenience for sources that are already text — tests, snippets, and
    /// anything not read from disk.
    /// </summary>
    /// <remarks>
    /// Encodes to UTF-8 first, so the spans on the resulting nodes are offsets into
    /// those bytes and not into the string's UTF-16 code units. The two disagree on
    /// any non-ASCII input, which is why the indexing path should stay on the byte
    /// overload rather than round-tripping through a string.
    /// </remarks>
    public SyntaxTree Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Parse(Encoding.UTF8.GetBytes(source));
    }

    public void Dispose() => _handle.Dispose();
}
