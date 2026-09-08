using System.Runtime.InteropServices;

namespace TreeSitter.Bindings;

/// <summary>
/// The tree-sitter C API, one-for-one and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the whole header. tree-sitter exports a couple of hundred
/// functions; what is bound here is the surface needed to parse a whole file,
/// re-parse it after an edit, and walk the result with queries, so tree cursors,
/// ranges and the wasm store are all absent. Add an entry point when a caller
/// needs it — an unused binding is an untested binding.
/// </para>
/// <para>
/// Incremental editing is bound at its narrowest: <see cref="ts_tree_edit"/> and
/// the <c>oldTree</c> parameter of <see cref="ts_parser_parse_string"/>, which is
/// everything a single owner of a tree needs. Handing an edited tree to a second
/// reader (<c>ts_tree_copy</c>) and shifting nodes, points and ranges outside a
/// tree (<c>ts_node_edit</c>, <c>ts_point_edit</c>, <c>ts_range_edit</c>) are not
/// here, for the same reason as everything else that is not.
/// </para>
/// <para>
/// <c>LibraryImport</c> rather than <c>DllImport</c>: marshalling is generated at
/// compile time, which is what keeps this NativeAOT-safe.
/// </para>
/// <para>
/// Raw pointers, raw ownership. Everything returned by a <c>_new</c> or
/// <c>parse</c> entry point has to be handed back to its matching <c>_delete</c>,
/// and a <see cref="TSNode"/> is only valid while its tree is alive. Wrapping any
/// of that in <c>SafeHandle</c>s is a separate layer, on top of this one.
/// </para>
/// </remarks>
public static partial class TS
{
    /// <summary>
    /// Resolved by the runtime to <c>libtree-sitter.dylib</c> / <c>.so</c> /
    /// <c>tree-sitter.dll</c> beside the assembly.
    /// </summary>
    /// <remarks>
    /// Grammars are not here. They live in a separate library and are resolved by
    /// name — <c>NativeLibrary.Load</c> plus <c>GetExport("tree_sitter_c_sharp")</c>
    /// — so bundled and user-supplied grammars take the same code path. Each
    /// exported symbol is a nullary function returning the grammar's static
    /// <c>const TSLanguage *</c>, which is what the <c>language</c> parameters
    /// below want.
    /// </remarks>
    public const string LibraryName = "tree-sitter";

    // --- Shim -----------------------------------------------------------
    //
    // Not upstream's. tree-sitter states its accepted grammar ABI range as two
    // preprocessor macros, which no P/Invoke can reach, so native/shim.c
    // compiles them into functions and is linked into the same artifact. The
    // ts_shim_ prefix keeps them from ever being mistaken for API.

    /// <summary>
    /// <c>TREE_SITTER_LANGUAGE_VERSION</c>: the newest grammar ABI this runtime reads.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ts_shim_language_abi_version();

    /// <summary>
    /// <c>TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION</c>: the oldest grammar ABI this
    /// runtime still reads.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ts_shim_min_compatible_language_abi_version();

    // --- Parser ---------------------------------------------------------

    [LibraryImport(LibraryName)]
    public static partial nint ts_parser_new();

    [LibraryImport(LibraryName)]
    public static partial void ts_parser_delete(nint self);

    /// <returns>
    /// <see langword="false"/> if the language's ABI version is incompatible with
    /// this runtime, in which case the parser keeps its previous language.
    /// </returns>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ts_parser_set_language(nint self, nint language);

    /// <param name="oldTree">
    /// <see cref="nint.Zero"/> to parse from scratch, or a tree of the previous
    /// text that has already been through <see cref="ts_tree_edit"/> — the
    /// incremental path, where the unchanged subtrees are reused instead of
    /// re-lexed. Ownership does not move: the old tree is still the caller's to
    /// delete afterwards.
    /// </param>
    /// <param name="str">
    /// UTF-8 bytes. Every span tree-sitter reports is a byte offset into this
    /// buffer, so callers keep the bytes rather than decoding up front.
    /// </param>
    /// <param name="length">Length of <paramref name="str"/> in bytes, not characters.</param>
    /// <returns>
    /// A tree that must be released with <see cref="ts_tree_delete"/>. Never null
    /// in normal use: unparseable input comes back as a tree containing error
    /// nodes, which is what makes this usable on half-written files.
    /// </returns>
    [LibraryImport(LibraryName)]
    public static unsafe partial nint ts_parser_parse_string(
        nint self,
        nint oldTree,
        byte* str,
        uint length);

    // --- Tree -----------------------------------------------------------

    [LibraryImport(LibraryName)]
    public static partial void ts_tree_delete(nint self);

    /// <summary>
    /// Shifts the tree's spans and points so they describe the text <em>after</em>
    /// <paramref name="edit"/>, which is what lets the next parse reuse it.
    /// </summary>
    /// <remarks>
    /// Edits the tree in place, so the spans it reported a moment ago are gone —
    /// between this call and the re-parse it indexes text the caller has and
    /// tree-sitter does not. The edit must match the change to the bytes exactly;
    /// nothing validates it, and a wrong one produces a tree rather than an error.
    /// </remarks>
    [LibraryImport(LibraryName)]
    public static partial void ts_tree_edit(nint self, in TSInputEdit edit);

    [LibraryImport(LibraryName)]
    public static partial TSNode ts_tree_root_node(nint self);

    // --- Node -----------------------------------------------------------

    /// <returns>
    /// A pointer to a static, NUL-terminated string owned by the grammar. Read it
    /// with <c>Marshal.PtrToStringUTF8</c> and do not free it — which is why this
    /// returns <see cref="nint"/> rather than letting the marshaller produce a
    /// <see cref="string"/>: the generated UTF-8 return marshaller frees the
    /// pointer it is handed.
    /// </returns>
    [LibraryImport(LibraryName)]
    public static partial nint ts_node_type(TSNode self);

    [LibraryImport(LibraryName)]
    public static partial uint ts_node_start_byte(TSNode self);

    [LibraryImport(LibraryName)]
    public static partial uint ts_node_end_byte(TSNode self);

    [LibraryImport(LibraryName)]
    public static partial TSPoint ts_node_start_point(TSNode self);

    [LibraryImport(LibraryName)]
    public static partial TSPoint ts_node_end_point(TSNode self);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ts_node_is_null(TSNode self);

    /// <summary>
    /// Whether this node has a grammar rule name, as opposed to being punctuation
    /// or a keyword.
    /// </summary>
    /// <remarks>
    /// Only meaningful on a node reached through <see cref="ts_node_child"/>;
    /// everything <see cref="ts_node_named_child"/> hands back is named by
    /// construction.
    /// </remarks>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ts_node_is_named(TSNode self);

    /// <summary>
    /// Whether this subtree contains a syntax error — a parse failure somewhere
    /// beneath, not necessarily at this node.
    /// </summary>
    /// <remarks>
    /// Never a reason to discard a file: tree-sitter recovers, and the symbols
    /// outside the damaged region are still correct. It is worth recording, so a
    /// half-written file can be reindexed rather than trusted.
    /// </remarks>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ts_node_has_error(TSNode self);

    /// <summary>The enclosing node, or the null node at the root.</summary>
    [LibraryImport(LibraryName)]
    public static partial TSNode ts_node_parent(TSNode self);

    /// <summary>Counts every child, punctuation and keywords included.</summary>
    [LibraryImport(LibraryName)]
    public static partial uint ts_node_child_count(TSNode self);

    /// <summary>
    /// The child at <paramref name="childIndex"/> among all children, named or not.
    /// </summary>
    /// <remarks>
    /// The anonymous tokens this exposes are what a signature is cut from — the
    /// parentheses and commas between a declaration's named children are not
    /// reachable any other way.
    /// </remarks>
    [LibraryImport(LibraryName)]
    public static partial TSNode ts_node_child(TSNode self, uint childIndex);

    /// <summary>Counts children that have a grammar rule name, skipping punctuation and keywords.</summary>
    [LibraryImport(LibraryName)]
    public static partial uint ts_node_named_child_count(TSNode self);

    [LibraryImport(LibraryName)]
    public static partial TSNode ts_node_named_child(TSNode self, uint childIndex);

    /// <summary>
    /// The preceding named sibling, or the null node if this is the first.
    /// </summary>
    /// <remarks>
    /// How docstrings are found. A <c>///</c> or <c>/** */</c> block is not part of
    /// the declaration it documents in either grammar — it is a separate
    /// <c>comment</c> node in front of it — so reading one means walking backwards
    /// from the declaration.
    /// </remarks>
    [LibraryImport(LibraryName)]
    public static partial TSNode ts_node_prev_named_sibling(TSNode self);

    /// <summary>
    /// Fetches a child by grammar field name — <c>name</c>, <c>body</c>,
    /// <c>parameters</c> — which is how a declaration's identifier is read without
    /// hardcoding child positions.
    /// </summary>
    /// <param name="name">UTF-8 bytes, written as a literal: <c>"name"u8</c>.</param>
    /// <returns>The null node if this node has no such field.</returns>
    [LibraryImport(LibraryName)]
    public static unsafe partial TSNode ts_node_child_by_field_name(
        TSNode self,
        byte* name,
        uint nameLength);

    // --- Language -------------------------------------------------------

    /// <summary>
    /// The language ABI version a grammar was generated for. Worth reporting when
    /// <see cref="ts_parser_set_language"/> refuses a grammar, so a grammar library
    /// built against a different runtime gives a diagnosable error rather than an
    /// unexplained refusal.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ts_language_abi_version(nint self);

    // --- Query ----------------------------------------------------------

    /// <param name="source">The query's S-expression patterns, as UTF-8 bytes.</param>
    /// <returns>
    /// A query to be released with <see cref="ts_query_delete"/>, or
    /// <see cref="nint.Zero"/> if a pattern is invalid — in which case the byte
    /// offset of the problem and its reason are written to the out parameters.
    /// </returns>
    [LibraryImport(LibraryName)]
    public static unsafe partial nint ts_query_new(
        nint language,
        byte* source,
        uint sourceLength,
        out uint errorOffset,
        out TSQueryError errorType);

    [LibraryImport(LibraryName)]
    public static partial void ts_query_delete(nint self);

    /// <summary>How many patterns the query source declared.</summary>
    /// <remarks>
    /// The index space for <see cref="ts_query_predicates_for_pattern"/>, and the
    /// same space <see cref="TSQueryMatch.PatternIndex"/> reports a match in.
    /// </remarks>
    [LibraryImport(LibraryName)]
    public static partial uint ts_query_pattern_count(nint self);

    /// <summary>
    /// How many distinct <c>@captures</c> the query declares. Read once so capture
    /// names can be resolved to ids up front, leaving the per-match path comparing
    /// integers.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ts_query_capture_count(nint self);

    /// <summary>
    /// Where one pattern starts in the query source.
    /// </summary>
    /// <remarks>
    /// The only thing that turns a pattern index back into something a person can
    /// find. A predicate the host refuses is reported against a pattern number, and
    /// a number is not enough to locate one of thirty patterns in a file.
    /// </remarks>
    [LibraryImport(LibraryName)]
    public static partial uint ts_query_start_byte_for_pattern(nint self, uint patternIndex);

    /// <returns>A pointer to query-owned memory; see <see cref="ts_node_type"/>.</returns>
    [LibraryImport(LibraryName)]
    public static partial nint ts_query_capture_name_for_id(
        nint self,
        uint index,
        out uint length);

    /// <summary>
    /// Resolves a string literal id from a predicate step to its text.
    /// </summary>
    /// <returns>A pointer to query-owned memory; see <see cref="ts_node_type"/>.</returns>
    [LibraryImport(LibraryName)]
    public static partial nint ts_query_string_value_for_id(
        nint self,
        uint index,
        out uint length);

    /// <summary>
    /// The predicate steps attached to one pattern, as a flat array of
    /// <paramref name="stepCount"/> <see cref="TSQueryPredicateStep"/> values in
    /// query-owned memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The API that makes upstream tag queries usable. tree-sitter parses
    /// <c>#eq?</c> and <c>#match?</c> but does not evaluate them — it hands the
    /// steps back and expects the host to decide. A caller that ignores this gets
    /// every match the structural pattern found, predicates silently discarded,
    /// which is more symbols than the query asked for rather than fewer.
    /// </para>
    /// <para>
    /// One pattern's steps are several predicates concatenated, each terminated by
    /// a step of type <see cref="TSQueryPredicateStepType.Done"/>. Within a
    /// predicate, a <see cref="TSQueryPredicateStepType.String"/> step's
    /// <c>ValueId</c> indexes <see cref="ts_query_string_value_for_id"/> and a
    /// <see cref="TSQueryPredicateStepType.Capture"/> step's indexes
    /// <see cref="ts_query_capture_name_for_id"/> — two separate spaces, so the
    /// step type decides which lookup applies.
    /// </para>
    /// </remarks>
    [LibraryImport(LibraryName)]
    public static partial nint ts_query_predicates_for_pattern(
        nint self,
        uint patternIndex,
        out uint stepCount);

    [LibraryImport(LibraryName)]
    public static partial nint ts_query_cursor_new();

    [LibraryImport(LibraryName)]
    public static partial void ts_query_cursor_delete(nint self);

    /// <summary>
    /// Restricts subsequent matching to nodes overlapping the byte range, until
    /// changed again.
    /// </summary>
    /// <remarks>
    /// Set this before <see cref="ts_query_cursor_exec"/>, not after. It is what
    /// turns a whole-file query into a within-one-symbol query — running the
    /// references query over a single declaration's span rather than the file —
    /// without reparsing or building a second tree.
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> if the range was rejected as invalid, in which case
    /// the previous range stands.
    /// </returns>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ts_query_cursor_set_byte_range(nint self, uint startByte, uint endByte);

    /// <summary>Starts matching <paramref name="query"/> against the subtree rooted at <paramref name="node"/>.</summary>
    [LibraryImport(LibraryName)]
    public static partial void ts_query_cursor_exec(nint self, nint query, TSNode node);

    /// <returns><see langword="false"/> once the matches are exhausted.</returns>
    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool ts_query_cursor_next_match(nint self, out TSQueryMatch match);
}
