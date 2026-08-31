using System.Runtime.InteropServices;

namespace TreeSitter.Bindings;

/// <summary>Mirrors <c>TSQueryCapture</c>: a captured node and its capture id.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TSQueryCapture
{
    public TSNode Node;
    public uint Index;
}

/// <summary>
/// Mirrors <c>TSQueryMatch</c>. <see cref="Captures"/> points at
/// <see cref="CaptureCount"/> consecutive <see cref="TSQueryCapture"/> values in
/// memory owned by the query cursor, which the next call to
/// <c>ts_query_cursor_next_match</c> overwrites — read them before advancing.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TSQueryMatch
{
    public uint Id;
    public ushort PatternIndex;
    public ushort CaptureCount;
    public nint Captures;
}

/// <summary>
/// Mirrors <c>TSQueryPredicateStepType</c>: what a predicate step's
/// <see cref="TSQueryPredicateStep.ValueId"/> refers to.
/// </summary>
public enum TSQueryPredicateStepType
{
    /// <summary>
    /// A sentinel ending one predicate. Its <see cref="TSQueryPredicateStep.ValueId"/>
    /// means nothing; a pattern's steps are several predicates run together, split
    /// on this.
    /// </summary>
    Done = 0,

    /// <summary>The id is a capture id, for <c>ts_query_capture_name_for_id</c>.</summary>
    Capture,

    /// <summary>The id is a string id, for <c>ts_query_string_value_for_id</c>.</summary>
    String,
}

/// <summary>
/// Mirrors <c>TSQueryPredicateStep</c>: one token of a <c>#eq?</c>-style predicate,
/// in memory owned by the query.
/// </summary>
/// <remarks>
/// The predicate's own name — <c>eq?</c>, <c>match?</c>, <c>not-eq?</c> — arrives as
/// the first <see cref="TSQueryPredicateStepType.String"/> step of each group, so
/// tree-sitter is not distinguishing between predicates it defines and ones a query
/// author invented. That is the host's job, including rejecting the unrecognized.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct TSQueryPredicateStep
{
    public TSQueryPredicateStepType Type;
    public uint ValueId;
}

/// <summary>Mirrors <c>TSQueryError</c>: why <c>ts_query_new</c> rejected a pattern.</summary>
public enum TSQueryError
{
    None = 0,
    Syntax,
    NodeType,
    Field,
    Capture,
    Structure,
    Language,
}
