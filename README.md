# cs_tree_sitter

C# bindings for [tree-sitter](https://tree-sitter.github.io/), plus the native
build that produces the libraries they load. Built to be consumed as a git
submodule.

Two layers, deliberately separate:

| Project | What it is |
| --- | --- |
| `TreeSitter.Bindings` | The raw C ABI — `[LibraryImport]` declarations and the structs that cross the boundary by value. No abstractions, no lifetimes. |
| `TreeSitter` | The wrapper callers actually use: `Parser`, `SyntaxTree`, `Node`, `Query`, `QueryCursor`, with `SafeHandle`-backed lifetimes, predicate evaluation and `#set!` directives. |

`TreeSitter.Bindings.Tests` pins the ABI — exact byte offsets and row/column
pairs against known snippets, which is the one assertion a shifted struct field
cannot survive. `TreeSitter.Tests` covers the wrapper.

## Quick start

```csharp
using TreeSitter;

var language = Language.Load("tree-sitter-grammars", "c_sharp");

using var parser = new Parser(language);
using var tree = parser.Parse("class Foo { void Bar() {} }");

Console.WriteLine(tree.RootNode.Type);   // compilation_unit
```

Bundled grammars: `c_sharp`, `typescript`, `tsx`. They all live in one native
library, `tree-sitter-grammars`, which `TreeSitter.Bindings` copies into the
output of anything referencing it.

## Building

The native libraries are not checked in — build them first, then the solution:

```
dotnet run native/build.cs      # writes native/artifacts/<rid>/
dotnet build TreeSitter.slnx
dotnet test TreeSitter.slnx
```

The first native run clones tree-sitter and the grammars at the tags pinned in
`build.cs`; later runs reuse `vendor/`. It needs `git` and a C compiler on
`PATH`. If you skip it, the build fails at `CheckNativeArtifacts` naming the
command rather than at load time. See [native/README.md](native/README.md) for
toolchain requirements, cross-compiling, and the Windows story.

## Using it as a submodule

```
git submodule add <url> vendor/cs_tree_sitter
```

Then reference the project from your own solution:

```xml
<ProjectReference Include="vendor/cs_tree_sitter/TreeSitter/TreeSitter.csproj" />
```

That is enough for a JIT build — the shared libraries flow into your output
through the project reference chain. Your CI has to run `native/build.cs` before
`dotnet build`, on each RID you ship.

A **native AOT publish** needs one more step, because a linked-in library has no
name left to resolve at runtime. Import the props and link the archive:

```xml
<Import Project="vendor/cs_tree_sitter/native/Native.props" />

<ItemGroup>
  <DirectPInvoke Include="tree-sitter" />
  <NativeLibrary Include="$(NativeArtifactsDir)$(StaticLibraryPrefix)tree-sitter$(StaticLibraryExtension)" />
</ItemGroup>
```

The grammars stay a shared library either way: `Language.Load` resolves them by
exported name, and there is no name to resolve once a library is linked in.

`Directory.Build.props` here sets `net10.0`, nullable, and warnings-as-errors for
this repo's own projects. A consumer's own `Directory.Build.props` does not reach
into the submodule, so the two do not fight.

## Adding a grammar

Add an entry to the `grammars` array at the top of `native/build.cs` — the repo
name, the tag to pin, and the subdirectory holding `src/` (`.` for a
single-language grammar). Re-run the build; it compiles into the same
`tree-sitter-grammars` library and is reachable as
`Language.Load("tree-sitter-grammars", "<name>")`.

Grammars from outside this repo work too, with no changes here: `Language.Load`
takes any library name, so a library dropped in beside the bundled one takes
exactly the same code path.
