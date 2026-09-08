namespace TreeSitter.Tests;

/// <summary>
/// The bundled grammars, loaded through the wrapper's own
/// <see cref="Language.Load"/>.
/// </summary>
/// <remarks>
/// Deliberately not a copy of the loader in <c>TreeSitter.Bindings.Tests</c>: if
/// this layer is going to hand callers a <see cref="Language"/>, it should be able
/// to produce one, and using it here is what tests that it can. A grammar is
/// immutable and process-lifetime, so one load each, shared.
/// </remarks>
internal static class Grammars
{
    /// <summary>
    /// The single artifact holding every bundled grammar, copied next to the test
    /// assembly by the project reference chain.
    /// </summary>
    internal const string Library = "tree-sitter-grammars";

    internal static Language CSharp { get; } = Language.Load(Library, "c_sharp");

    internal static Language TypeScript { get; } = Language.Load(Library, "typescript");

    /// <summary>
    /// An indentation-sensitive grammar, which C# and TypeScript are not: shifting a
    /// line sideways moves it out of one block and into another.
    /// </summary>
    internal static Language Python { get; } = Language.Load(Library, "python");

    /// <summary>
    /// The one bundled grammar whose scanner asks the lexer for a column, and a
    /// grammar whose heredocs carry lexer state across lines.
    /// </summary>
    internal static Language Bash { get; } = Language.Load(Library, "bash");
}
