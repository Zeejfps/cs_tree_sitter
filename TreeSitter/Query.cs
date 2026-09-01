using System.Runtime.InteropServices;
using System.Text;
using TreeSitter.Bindings;

namespace TreeSitter;

/// <summary>
/// What to do about a pattern carrying <c>#eq?</c>-style predicates, which
/// tree-sitter parses and leaves to the host.
/// </summary>
/// <remarks>
/// There is no option here that ignores a predicate. That was the old default's
/// whole argument — a host that skips them gets a query meaning something strictly
/// <em>broader</em> than it reads, with no error anywhere — and it survives the
/// evaluator: a predicate is either run or the query is refused.
/// </remarks>
public enum PredicateHandling
{
    /// <summary>
    /// Evaluate them, and refuse at compile time any predicate this host does not
    /// implement. The default.
    /// </summary>
    Evaluate = 0,

    /// <summary>
    /// Refuse to compile a pattern with predicates at all, naming them. For a
    /// caller whose query sources are meant to stay structural.
    /// </summary>
    Reject,
}

/// <summary>
/// A compiled query, with its capture names already resolved to ids.
/// </summary>
/// <remarks>
/// <para>
/// Compiled once per language and reused across every file: a query is read-only
/// after <c>ts_query_new</c> and holds no per-run state, so the only per-file cost
/// is a <see cref="QueryCursor"/>. Recompiling per file would put a parse of the
/// query source in the middle of the indexing loop.
/// </para>
/// <para>
/// <b>Names are resolved up front.</b> tree-sitter reports a capture as an integer
/// id and offers <c>ts_query_capture_name_for_id</c> to turn it back into a string;
/// doing that per match would put a native call and a UTF-8 decode inside the hot
/// loop, then compare strings. Reading the whole table once at compile time — there
/// are only ever a handful of captures — leaves the per-match path comparing
/// <see cref="uint"/>s.
/// </para>
/// </remarks>
public sealed class Query : IDisposable
{
    private readonly QueryHandle _handle;
    private readonly string[] _captureNames;
    private readonly Dictionary<string, uint> _captureIds;
    private readonly QueryPredicate[]?[] _predicates;
    private readonly QueryProperty[]?[] _properties;

    private Query(
        QueryHandle handle,
        Language language,
        string[] captureNames,
        Dictionary<string, uint> captureIds,
        DecodedPatterns patterns)
    {
        _handle = handle;
        _captureNames = captureNames;
        _captureIds = captureIds;
        _predicates = patterns.Predicates;
        _properties = patterns.Properties;
        Language = language;
    }

    /// <summary>The grammar this query's node types and fields were checked against.</summary>
    public Language Language { get; }

    /// <summary>How many patterns the source declared.</summary>
    /// <remarks>
    /// The index space <see cref="QueryMatch.PatternIndex"/> reports in, and the one
    /// predicates are keyed on.
    /// </remarks>
    public int PatternCount => _predicates.Length;

    /// <summary>How many distinct <c>@captures</c> the source declared.</summary>
    public int CaptureCount => _captureNames.Length;

    /// <summary>Whether any pattern carries predicates.</summary>
    public bool HasPredicates => Array.Exists(_predicates, p => p is not null);

    /// <summary>
    /// Compiles <paramref name="source"/> against <paramref name="language"/>.
    /// </summary>
    /// <exception cref="QueryCompilationException">
    /// tree-sitter rejected a pattern. Carries the byte offset and the reason, which
    /// together are the difference between a fixable error and "the query is wrong".
    /// </exception>
    /// <exception cref="QueryPredicateException">
    /// A pattern writes a predicate this host does not implement, or writes a
    /// supported one wrongly.
    /// </exception>
    /// <exception cref="PredicateRefusedException">
    /// The source uses predicates and <paramref name="predicates"/> is
    /// <see cref="PredicateHandling.Reject"/>.
    /// </exception>
    public static unsafe Query Compile(
        Language language,
        string source,
        PredicateHandling predicates = PredicateHandling.Evaluate)
    {
        ArgumentNullException.ThrowIfNull(source);

        var sourceBytes = Encoding.UTF8.GetBytes(source);

        nint query;
        uint errorOffset;
        TSQueryError errorType;
        fixed (byte* pinned = sourceBytes)
        {
            byte empty = 0;
            query = TS.ts_query_new(
                language.Handle,
                pinned is null ? &empty : pinned,
                (uint)sourceBytes.Length,
                out errorOffset,
                out errorType);
        }

        if (query == nint.Zero)
        {
            throw new QueryCompilationException(errorType, errorOffset, Describe(sourceBytes, errorOffset));
        }

        var handle = QueryHandle.Adopt(query);
        try
        {
            var captureCount = TS.ts_query_capture_count(query);
            var names = new string[captureCount];
            var ids = new Dictionary<string, uint>((int)captureCount, StringComparer.Ordinal);
            for (uint id = 0; id < captureCount; id++)
            {
                var pointer = TS.ts_query_capture_name_for_id(query, id, out var length);
                var name = Marshal.PtrToStringUTF8(pointer, (int)length);
                names[id] = name;

                // A query may write the same @capture on several patterns, which is
                // one id, not two -- so the first mapping wins and there is nothing
                // to reconcile.
                ids.TryAdd(name, id);
            }

            var patternCount = TS.ts_query_pattern_count(query);

            if (predicates == PredicateHandling.Reject)
            {
                for (uint pattern = 0; pattern < patternCount; pattern++)
                {
                    TS.ts_query_predicates_for_pattern(query, pattern, out var stepCount);
                    if (stepCount > 0)
                    {
                        throw new PredicateRefusedException(pattern, PredicateNames(query, pattern));
                    }
                }
            }

            var patterns = QueryPredicates.Decode(
                query,
                patternCount,
                pattern => Describe(sourceBytes, TS.ts_query_start_byte_for_pattern(query, pattern)));

            GC.KeepAlive(handle);
            return new Query(handle, language, names, ids, patterns);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The id <paramref name="captureName"/> compiled to, written without the
    /// leading <c>@</c>.
    /// </summary>
    /// <remarks>
    /// Resolve once, outside the loop, and compare the result against
    /// <see cref="QueryMatch"/> capture ids. Throwing on an unknown name is the
    /// point: a capture renamed in the query source but not in the code that reads
    /// it would otherwise return no matches and look like a grammar problem.
    /// </remarks>
    public uint CaptureId(string captureName)
    {
        ArgumentNullException.ThrowIfNull(captureName);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        return _captureIds.TryGetValue(captureName, out var id)
            ? id
            : throw new ArgumentException(
                $"This query declares no capture named '{captureName}'. It declares: " +
                $"{string.Join(", ", _captureNames)}.",
                nameof(captureName));
    }

    /// <summary>The non-throwing form, for callers running one query over several grammars.</summary>
    public bool TryGetCaptureId(string captureName, out uint captureId)
    {
        ArgumentNullException.ThrowIfNull(captureName);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        return _captureIds.TryGetValue(captureName, out captureId);
    }

    /// <summary>The name behind an id, for diagnostics rather than the hot path.</summary>
    public string CaptureName(uint captureId)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(captureId, (uint)_captureNames.Length);

        return _captureNames[captureId];
    }

    /// <summary>The <c>#set!</c> properties one pattern declared, empty where it declared none.</summary>
    public IReadOnlyList<QueryProperty> PropertiesFor(int patternIndex)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(patternIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(patternIndex, _properties.Length);

        return _properties[patternIndex] ?? [];
    }

    /// <summary>The value one pattern set for <paramref name="key"/>, null for a valueless flag.</summary>
    public bool TryGetProperty(int patternIndex, string key, out string? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        foreach (var property in PropertiesFor(patternIndex))
        {
            if (!string.Equals(property.Key, key, StringComparison.Ordinal)) continue;

            value = property.Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Whether one pattern carries predicates.</summary>
    public bool PatternHasPredicates(int patternIndex)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(patternIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(patternIndex, _predicates.Length);

        return _predicates[patternIndex] is not null;
    }

    public void Dispose() => _handle.Dispose();

    internal nint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.DangerousGetHandle();
        }
    }

    /// <summary>Keeps the handle rooted across a native call that only got its pointer.</summary>
    internal void KeepAlive() => GC.KeepAlive(_handle);

    /// <summary>
    /// Whether a match's captures satisfy its pattern's predicates.
    /// </summary>
    /// <remarks>
    /// An array probe and a null check for the overwhelmingly common pattern that
    /// has none — the reason the predicates are stored per pattern rather than
    /// scanned per match.
    /// </remarks>
    internal bool PredicatesHold(int patternIndex, SyntaxTree tree, ReadOnlySpan<TSQueryCapture> captures) =>
        _predicates[patternIndex] is not { } predicates || QueryPredicates.Hold(predicates, tree, captures);

    /// <summary>
    /// The predicate names on one pattern, for the rejection message.
    /// </summary>
    /// <remarks>
    /// A predicate's own name arrives as the first <see cref="TSQueryPredicateStepType.String"/>
    /// step of each group, groups being separated by <see cref="TSQueryPredicateStepType.Done"/>.
    /// This decodes only that much — enough to say which predicates were refused,
    /// which is all <see cref="PredicateHandling.Reject"/> needs to say.
    /// </remarks>
    private static unsafe string[] PredicateNames(nint query, uint patternIndex)
    {
        var pointer = TS.ts_query_predicates_for_pattern(query, patternIndex, out var stepCount);
        var steps = new ReadOnlySpan<TSQueryPredicateStep>((void*)pointer, (int)stepCount);

        var names = new List<string>();
        var atGroupStart = true;
        foreach (var step in steps)
        {
            switch (step.Type)
            {
                case TSQueryPredicateStepType.Done:
                    atGroupStart = true;
                    break;

                case TSQueryPredicateStepType.String when atGroupStart:
                    var name = TS.ts_query_string_value_for_id(query, step.ValueId, out var length);
                    names.Add("#" + Marshal.PtrToStringUTF8(name, (int)length));
                    atGroupStart = false;
                    break;

                default:
                    atGroupStart = false;
                    break;
            }
        }

        return [.. names];
    }

    /// <summary>
    /// Turns a byte offset into something a person can find: the line, and the text
    /// around it.
    /// </summary>
    /// <remarks>
    /// Query sources are multi-line and the error kinds are terse. "NodeType at byte
    /// 412" names the mistake but not which of thirty patterns made it.
    /// </remarks>
    private static string Describe(byte[] sourceBytes, uint errorOffset)
    {
        if (sourceBytes.Length == 0)
        {
            return "The query source was empty.";
        }

        var offset = (int)Math.Min(errorOffset, (uint)sourceBytes.Length);
        var lineStart = Array.LastIndexOf(sourceBytes, (byte)'\n', Math.Max(offset - 1, 0)) + 1;
        var lineEnd = Array.IndexOf(sourceBytes, (byte)'\n', offset);
        if (lineEnd < 0)
        {
            lineEnd = sourceBytes.Length;
        }

        var line = Encoding.UTF8.GetString(sourceBytes, lineStart, lineEnd - lineStart);
        return $"Near: {line.Trim()}";
    }
}
