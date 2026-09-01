namespace TreeSitter;

/// <summary>
/// One <c>#set!</c> directive on a pattern: metadata the query attaches for the host to read,
/// such as <c>(#set! injection.language "css")</c>.
/// </summary>
public sealed class QueryProperty
{
    /// <summary>The key, as written: <c>injection.language</c>.</summary>
    public required string Key { get; init; }

    /// <summary>The value, or null for a valueless flag such as <c>(#set! injection.combined)</c>.</summary>
    public string? Value { get; init; }

    /// <summary>The capture the directive was scoped to, for <c>(#set! @capture key value)</c>.</summary>
    public uint? CaptureId { get; init; }
}
