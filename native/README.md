# native/

Produces the native libraries `TreeSitter.Bindings` loads:

| File | What it is |
| --- | --- |
| `libtree-sitter.{dylib,so}`, `tree-sitter.dll` | The tree-sitter runtime — the C ABI the P/Invoke layer is written against. |
| `libtree-sitter-grammars.{dylib,so}`, `tree-sitter-grammars.dll` | Every bundled grammar in one library, each exporting `tree_sitter_<lang>()`. |

Windows drops the `lib` prefix, per platform convention. Both names resolve the
same way from managed code — `LibraryImport("tree-sitter")` and
`NativeLibrary.Load("tree-sitter-grammars", …)` are written once and work everywhere.

Beside them sits a static archive, which nothing loads — a consumer doing a
native AOT publish links it into the executable, so the published binary carries
the runtime instead of reading it from a file alongside. See
[the seam](#the-seam-with-the-projects) below.

| File | What it is |
| --- | --- |
| `libtree-sitter.a`, `tree-sitter.lib` | The runtime above, archived rather than linked. |

Windows names and formats it the way `lib.exe` does, not the way GNU `ar`
does — see [Toolchains](#toolchains).

The grammars stay a shared library regardless, because they are resolved by name
at runtime and there is no name to resolve once a library is linked in.

One grammar library per RID rather than one per language × RID: the alternative
multiplies artifacts by the language count for no gain, since a consumer that
wants one language still pays a RID's worth of build.

## Building locally

```
dotnet run native/build.cs                       # vendor, compile, write artifacts/<rid>/
dotnet run native/build.cs -- --clean            # discard obj/ and artifacts/ first
dotnet run native/build.cs -- --target win-x64   # cross-compile for another RID
```

Builds the host RID unless `--target` says otherwise. `git` and a C compiler need
to be on `PATH`; the compiler is `$CC`, falling back to `cc` (`gcc` on Windows),
and the archiver is `$AR`, falling back to the compiler's own target prefix. The
first run needs the network — it clones tree-sitter and the grammars at the tags
pinned in `build.cs`. Later runs reuse `vendor/`.

### Toolchains

The **shared libraries** want a **GNU toolchain — gcc or clang** — on every
platform, including Windows, where that means MSYS2 or mingw-w64 rather than
MSVC. That is not a stylistic preference. tree-sitter's core annotates nothing
`__declspec(dllexport)` (`api.h` uses `#pragma GCC visibility` and nothing else)
and leans on CMake to export its symbols, so a `cl.exe` build links a DLL with an
empty export table and every `LibraryImport` in `TS.cs` fails at first call. `ld`
exports everything by default when no symbol is annotated, which is exactly the
behaviour the core expects. The grammars are the mirror image: a generated
`parser.c` does mark its one entry point `__declspec(dllexport)`, which switches
that default off and leaves precisely `tree_sitter_<lang>()` exported.

Because MinGW targets Windows from anywhere, `--target win-x64` with
`CC=x86_64-w64-mingw32-gcc` produces the Windows *shared libraries* from a Linux
or macOS host.

Nothing in `TS.cs` frees native memory from the managed side, so linking against
`msvcrt` rather than the UCRT crosses no allocator boundary.

The **static archive** inverts this on Windows: it comes from `cl.exe` and
`lib.exe`. ILC links with MSVC's `link.exe`
(`Microsoft.NETCore.Native.Windows.targets` sets `CppLinker` to `link` and finds
it through `findvcvarsall.bat`; the `LinkerFlavor` escape hatch is Unix-only),
and it does not read what GNU `ar` writes. Nothing is lost by switching: the
export-table problem above is a DLL problem, and an archive has no export table.
`DirectPInvoke` resolves the symbols out of it at link time.

`cl.exe` takes its include and library paths from the environment rather than from
arguments, so `build.cs` locates a Visual Studio install with `vswhere` and reads
`vcvarsall.bat`'s environment back out of `cmd.exe` before running it — the same
route ILC takes to `link.exe`. `dotnet run native/build.cs` therefore works from an
ordinary shell; a Developer Command Prompt still works too, and short-circuits that
lookup.

The cost is that the Windows archive **cannot be cross-built** — `cl.exe` runs
only on Windows, so CI needs a Windows runner for it even though the DLLs still
cross-compile. Running `build.cs` for a `win` RID from macOS or Linux writes the
DLLs, prints what it skipped, and exits cleanly; a consumer's AOT publish then
fails naming the missing archive rather than in the linker.

The objects are compiled `/MT`, matching the static CRT in the runtime's own
objects — ILC swaps the dynamic UCRT in over the top of that
(`/NODEFAULTLIB:libucrt.lib /DEFAULTLIB:ucrt.lib`). If a Windows AOT publish ever
reports `LNK4098` or duplicate CRT symbols, that flag is the first thing to look
at.

## Layout

```
build.cs        the whole build; upstream versions are pinned at the top
vendor/         sources at those versions           (gitignored)
obj/<rid>/      intermediate object files           (gitignored)
artifacts/<rid>/  the libraries and the archive     (gitignored)
Native.props    where consumers find that directory
```

Bumping a pin in `build.cs` is deliberate: the tree-sitter version fixes the ABI
the bindings are written against, and a grammar version fixes the node type names
the queries match on.

## The seam with the projects

This directory is the **producer**; `TreeSitter.Bindings` and any consumer doing a
native AOT publish are the **consumers**, and the whole contract between them is
one directory path. `Native.props` holds it and both import it.

`TreeSitter.Bindings` copies `artifacts/$(NativeArtifactsRid)/*` into its build
output — where the RID defaults to the SDK's own, so a local build picks up
whatever `build.cs` last produced, and is overridable for cross-targeting. If the
directory is missing, the build fails with a pointer back to the command above
rather than at load time.

An AOT consumer reads the same directory for the archive, on every RID.
`DirectPInvoke` is what makes linking it possible at all: it turns the P/Invokes
naming `tree-sitter` into symbol references the linker resolves, and without it
the AOT runtime would still go looking for a shared library at startup. The names
differ per platform, which is what `StaticLibraryPrefix` and
`StaticLibraryExtension` in `Native.props` are for.

Because that is the entire coupling, `native/` stays a sibling of the projects
instead of living inside `TreeSitter.Bindings/`:

- The .NET SDK globs `**/*.cs` under a project directory. `build.cs` would be
  compiled into the library — top-level statements, a second entry point — and so
  would any C# a vendored grammar repository happens to ship (`tree-sitter-c-sharp`
  ships a `Generator.csproj` today). Suppressing that means `Compile`, `None`, and
  `Content` removes that stay correct forever.
- Different toolchain. This is a C compiler and `git` driven by a file-based app,
  run *before* the .NET build graph, and the one part of the repo with a
  dependency outside the .NET SDK.
- Different lifecycle. Vendoring and pinning upstream sources is not the bindings'
  concern, and CI runs this across five RIDs while the bindings consume exactly one.
- `vendor/` and `obj/` are large gitignored trees — tens of megabytes of generated
  `parser.c`. Keeping them out of a project directory keeps IDE file trees, search,
  and analyzers away from them.
