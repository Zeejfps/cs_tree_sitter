namespace TreeSitter.Tests;

/// <summary>
/// Loading a grammar by exported name, which is the mechanism that makes bundled
/// and user-supplied grammars one code path.
/// </summary>
/// <remarks>
/// The refusal path — a grammar whose ABI version this runtime rejects, reported by
/// <see cref="IncompatibleLanguageException"/> — is not tested here, because faking
/// it would mean shipping a deliberately mis-generated grammar and the assertion
/// would then be about the fake. What is tested is that the check runs at load, so
/// a grammar that will never work fails while its name is still in hand.
/// </remarks>
public class LanguageTests
{
    [Fact]
    public void ABundledGrammar_LoadsAndParses()
    {
        var language = Language.Load(Grammars.Library, "c_sharp");

        Assert.NotEqual(nint.Zero, language.Handle);

        using var parser = new Parser(language);
        using var tree = parser.Parse("class C { }");

        Assert.Equal("class_declaration", tree.RootNode.NamedChildren[0].Type);
    }

    /// <summary>
    /// The number that goes into <see cref="IncompatibleLanguageException"/>. A zero
    /// here would mean the ABI report is decoration on an error nobody can act on.
    /// </summary>
    [Fact]
    public void AGrammarReportsTheAbiVersionItWasGeneratedFor()
    {
        Assert.True(
            Grammars.CSharp.AbiVersion > 0,
            "ts_language_abi_version reported 0, so a refused grammar would carry no diagnosis.");
    }

    [Fact]
    public void TwoGrammarsFromOneLibrary_AreDistinctLanguages()
    {
        Assert.NotEqual(Grammars.CSharp.Handle, Grammars.TypeScript.Handle);
    }

    [Fact]
    public void AGrammarTheLibraryDoesNotExport_FailsAtLoad()
    {
        Assert.Throws<EntryPointNotFoundException>(() => { _ = Language.Load(Grammars.Library, "no_such_grammar"); });
    }

    [Fact]
    public void ALibraryThatIsNotThere_FailsAtLoad()
    {
        Assert.Throws<DllNotFoundException>(() => { _ = Language.Load("no-such-grammar-library", "c_sharp"); });
    }

    /// <summary>
    /// A null pointer means the export lookup failed, and saying so here keeps the
    /// grammar's name in the message — a parser refusing a null language cannot.
    /// </summary>
    [Fact]
    public void ANullLanguagePointer_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new Language(nint.Zero); });
    }
}
