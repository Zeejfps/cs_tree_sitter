using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using TreeSitter.Bindings;

namespace TreeSitter;

/// <summary>
/// Which predicate a step group spelled, once its name has been recognized.
/// </summary>
/// <remarks>
/// Closed on purpose. tree-sitter hands back whatever name the author wrote, so the
/// only thing separating a supported predicate from a typo — or from <c>#is-not?</c>,
/// which means something this host does not implement — is this list and the refusal
/// that follows it.
/// </remarks>
internal enum PredicateKind
{
    Eq,
    NotEq,
    AnyOf,
    NotAnyOf,
    Match,
    NotMatch,
}

/// <summary>
/// One predicate, resolved down to capture ids, UTF-8 literals and a compiled
/// <see cref="Regex"/> at query-compile time.
/// </summary>
/// <remarks>
/// <para>
/// Everything expensive happens once. A predicate is checked per match, per file,
/// across a whole repository — and <c>@ref</c> patterns made matches an order of
/// magnitude more numerous — so nothing here looks a capture name up, encodes a
/// literal, or parses a pattern while the cursor is running.
/// </para>
/// <para>
/// Literals are kept as UTF-8 because that is what a captured node already is: a
/// slice of the tree's source. <c>#eq?</c> and <c>#any-of?</c> therefore compare
/// bytes and never allocate. <c>#match?</c> cannot — .NET regexes run over UTF-16 —
/// so it pays one decode per check, which is a reason to prefer <c>#eq?</c> where
/// either would do.
/// </para>
/// </remarks>
internal sealed class QueryPredicate
{
    public required PredicateKind Kind { get; init; }

    /// <summary>The capture whose text is being tested.</summary>
    public required uint CaptureId { get; init; }

    /// <summary>The capture it is tested against, for the <c>(#eq? @a @b)</c> form.</summary>
    public uint SecondCaptureId { get; init; }

    public bool ComparesTwoCaptures { get; init; }

    /// <summary>The literals to compare against, UTF-8, empty for the capture-vs-capture form.</summary>
    public byte[][] Literals { get; init; } = [];

    public Regex? Pattern { get; init; }
}

/// <summary>
/// Decoding and evaluation for <c>#eq?</c>-style predicates.
/// </summary>
/// <remarks>
/// <para>
/// <b>The regex dialect is .NET's, minus the parts .NET and Rust disagree about
/// silently.</b> Predicates in the wild are written against the Rust <c>regex</c>
/// crate, which is not the language <see cref="Regex"/> speaks. Two choices close
/// most of the gap. <c>#match?</c> is compiled
/// <see cref="RegexOptions.NonBacktracking"/>, which refuses lookaround,
/// backreferences and atomic groups — precisely the constructs Rust's engine also
/// refuses — so a pattern outside the shared dialect fails when the query is
/// compiled instead of matching differently at runtime, and matching stays linear in
/// the input no matter what a query file asks for. And POSIX bracket expressions
/// (<c>[[:alpha:]]</c>), which Rust supports and .NET reads as an ordinary character
/// class followed by a literal <c>]</c>, are rejected outright — that one is silent
/// in both directions and is the only construct here that had to be banned by hand.
/// </para>
/// <para>
/// Two differences remain, pinned by tests rather than papered over: <c>$</c> matches
/// before a final newline in .NET and only at the very end in Rust (write <c>\z</c>
/// when it matters), and <c>.</c> consumes one UTF-16 code unit rather than one
/// Unicode scalar, so a character outside the basic plane counts as two.
/// </para>
/// <para>
/// Semantics otherwise follow tree-sitter's own bindings: a predicate holds for
/// <em>every</em> node bound to its capture, so a capture the match did not bind
/// passes vacuously, and <c>#match?</c> searches rather than anchoring.
/// </para>
/// </remarks>
internal static partial class QueryPredicates
{
    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    /// <summary>
    /// Decodes every pattern's predicates, or throws naming the one that is wrong.
    /// </summary>
    /// <returns>
    /// One entry per pattern, <see langword="null"/> where the pattern carries none —
    /// which is the common case and the one worth keeping out of the match loop.
    /// </returns>
    public static unsafe QueryPredicate[]?[] Decode(nint query, uint patternCount, Func<uint, string> describePattern)
    {
        var byPattern = new QueryPredicate[]?[patternCount];

        for (var pattern = 0u; pattern < patternCount; pattern++)
        {
            var start = TS.ts_query_predicates_for_pattern(query, pattern, out var stepCount);
            if (stepCount == 0)
            {
                continue;
            }

            var steps = new ReadOnlySpan<TSQueryPredicateStep>((void*)start, (int)stepCount);
            var decoded = new List<QueryPredicate>();

            var groupStart = 0;
            for (var i = 0; i < steps.Length; i++)
            {
                if (steps[i].Type != TSQueryPredicateStepType.Done)
                {
                    continue;
                }

                decoded.Add(DecodeOne(query, pattern, steps[groupStart..i], describePattern));
                groupStart = i + 1;
            }

            byPattern[pattern] = [.. decoded];
        }

        return byPattern;
    }

    /// <summary>
    /// Whether every predicate on a pattern holds for one match's captures.
    /// </summary>
    public static bool Hold(
        QueryPredicate[] predicates,
        SyntaxTree tree,
        ReadOnlySpan<TSQueryCapture> captures)
    {
        foreach (var predicate in predicates)
        {
            if (!Holds(predicate, tree, captures))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Holds(QueryPredicate predicate, SyntaxTree tree, ReadOnlySpan<TSQueryCapture> captures)
    {
        if (predicate.ComparesTwoCaptures)
        {
            return CapturesAgree(predicate, tree, captures);
        }

        var wanted = predicate.Kind is PredicateKind.Eq or PredicateKind.AnyOf or PredicateKind.Match;

        foreach (var capture in captures)
        {
            if (capture.Index != predicate.CaptureId)
            {
                continue;
            }

            var text = new Node(tree, capture.Node).Utf8Text;
            var hit = predicate.Pattern is { } regex
                ? regex.IsMatch(Encoding.UTF8.GetString(text))
                : ContainsText(predicate.Literals, text);

            if (hit != wanted)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The <c>(#eq? @a @b)</c> form: the captures are walked in step, and a match
    /// binding different numbers of each compares only as far as both go — which is
    /// what tree-sitter's own bindings do.
    /// </summary>
    private static bool CapturesAgree(QueryPredicate predicate, SyntaxTree tree, ReadOnlySpan<TSQueryCapture> captures)
    {
        var wanted = predicate.Kind is PredicateKind.Eq;
        var right = 0;

        foreach (var capture in captures)
        {
            if (capture.Index != predicate.CaptureId)
            {
                continue;
            }

            while (right < captures.Length && captures[right].Index != predicate.SecondCaptureId)
            {
                right++;
            }

            if (right == captures.Length)
            {
                return true;
            }

            var left = new Node(tree, capture.Node).Utf8Text;
            var other = new Node(tree, captures[right].Node).Utf8Text;
            right++;

            if (left.SequenceEqual(other) != wanted)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsText(byte[][] literals, ReadOnlySpan<byte> text)
    {
        foreach (var literal in literals)
        {
            if (text.SequenceEqual(literal))
            {
                return true;
            }
        }

        return false;
    }

    private static QueryPredicate DecodeOne(
        nint query,
        uint pattern,
        ReadOnlySpan<TSQueryPredicateStep> group,
        Func<uint, string> describePattern)
    {
        if (group.Length == 0 || group[0].Type != TSQueryPredicateStepType.String)
        {
            throw new QueryPredicateException(pattern, "?", "a predicate has to start with its name.", describePattern);
        }

        var name = StringValue(query, group[0].ValueId);
        var arguments = group[1..];

        var kind = name switch
        {
            "eq?" => PredicateKind.Eq,
            "not-eq?" => PredicateKind.NotEq,
            "any-of?" => PredicateKind.AnyOf,
            "not-any-of?" => PredicateKind.NotAnyOf,
            "match?" => PredicateKind.Match,
            "not-match?" => PredicateKind.NotMatch,
            _ => throw new QueryPredicateException(
                pattern,
                name,
                "no such predicate. This host implements #eq?, #not-eq?, #any-of?, #not-any-of?, #match? and " +
                "#not-match?, and refuses everything else rather than skipping it — a predicate nobody evaluates " +
                "makes the pattern match more than it reads.",
                describePattern),
        };

        if (arguments.Length == 0 || arguments[0].Type != TSQueryPredicateStepType.Capture)
        {
            throw new QueryPredicateException(
                pattern,
                name,
                "its first argument has to be a @capture; there is nothing else to test.",
                describePattern);
        }

        var subject = arguments[0].ValueId;
        var rest = arguments[1..];

        return kind switch
        {
            PredicateKind.Eq or PredicateKind.NotEq =>
                DecodeComparison(query, pattern, name, kind, subject, rest, describePattern),
            PredicateKind.AnyOf or PredicateKind.NotAnyOf =>
                DecodeAnyOf(query, pattern, name, kind, subject, rest, describePattern),
            _ => DecodeMatch(query, pattern, name, kind, subject, rest, describePattern),
        };
    }

    private static QueryPredicate DecodeComparison(
        nint query,
        uint pattern,
        string name,
        PredicateKind kind,
        uint subject,
        ReadOnlySpan<TSQueryPredicateStep> rest,
        Func<uint, string> describePattern)
    {
        if (rest.Length != 1)
        {
            throw new QueryPredicateException(
                pattern,
                name,
                $"takes exactly two arguments and was given {rest.Length + 1}.",
                describePattern);
        }

        return rest[0].Type == TSQueryPredicateStepType.Capture
            ? new QueryPredicate
            {
                Kind = kind,
                CaptureId = subject,
                SecondCaptureId = rest[0].ValueId,
                ComparesTwoCaptures = true,
            }
            : new QueryPredicate
            {
                Kind = kind,
                CaptureId = subject,
                Literals = [Utf8Value(query, rest[0].ValueId)],
            };
    }

    private static QueryPredicate DecodeAnyOf(
        nint query,
        uint pattern,
        string name,
        PredicateKind kind,
        uint subject,
        ReadOnlySpan<TSQueryPredicateStep> rest,
        Func<uint, string> describePattern)
    {
        if (rest.Length == 0)
        {
            throw new QueryPredicateException(
                pattern,
                name,
                "needs a @capture and at least one string to compare it against.",
                describePattern);
        }

        var literals = new byte[rest.Length][];
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i].Type != TSQueryPredicateStepType.Capture)
            {
                literals[i] = Utf8Value(query, rest[i].ValueId);
                continue;
            }

            throw new QueryPredicateException(
                pattern,
                name,
                "compares one @capture against string literals, and one of its arguments is a capture.",
                describePattern);
        }

        return new QueryPredicate { Kind = kind, CaptureId = subject, Literals = literals };
    }

    private static QueryPredicate DecodeMatch(
        nint query,
        uint pattern,
        string name,
        PredicateKind kind,
        uint subject,
        ReadOnlySpan<TSQueryPredicateStep> rest,
        Func<uint, string> describePattern)
    {
        if (rest.Length != 1 || rest[0].Type == TSQueryPredicateStepType.Capture)
        {
            throw new QueryPredicateException(
                pattern,
                name,
                "takes a @capture and exactly one regular expression, written as a string.",
                describePattern);
        }

        var source = StringValue(query, rest[0].ValueId);

        if (PosixClass().IsMatch(source))
        {
            throw new QueryPredicateException(
                pattern,
                name,
                $"'{source}' uses a POSIX bracket expression. Rust's regex crate, which query predicates are " +
                "usually written against, reads [[:alpha:]] as a character class; .NET reads it as the class " +
                "[[:alph] followed by a literal ']' and matches something else entirely without complaining. " +
                "Write the class out — [a-zA-Z] — or use \\p{L}.",
                describePattern);
        }

        try
        {
            return new QueryPredicate
            {
                Kind = kind,
                CaptureId = subject,
                Pattern = new Regex(source, Options),
            };
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new QueryPredicateException(
                pattern,
                name,
                $"'{source}' is not a regular expression this host accepts: {error.Message}",
                describePattern,
                error);
        }
    }

    private static string StringValue(nint query, uint valueId)
    {
        var pointer = TS.ts_query_string_value_for_id(query, valueId, out var length);
        return Marshal.PtrToStringUTF8(pointer, (int)length);
    }

    private static unsafe byte[] Utf8Value(nint query, uint valueId)
    {
        var pointer = TS.ts_query_string_value_for_id(query, valueId, out var length);
        return new ReadOnlySpan<byte>((void*)pointer, (int)length).ToArray();
    }

    /// <summary>
    /// The one Rust construct .NET misreads instead of refusing, so it has to be
    /// spotted before <see cref="Regex"/> accepts it.
    /// </summary>
    [GeneratedRegex(@"\[:\^?(alnum|alpha|ascii|blank|cntrl|digit|graph|lower|print|punct|space|upper|word|xdigit):\]")]
    private static partial Regex PosixClass();
}
