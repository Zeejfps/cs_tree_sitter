#!/usr/bin/env dotnet
//
// Builds the native artifacts this repo ships:
//
//   tree-sitter.{dylib,so,dll}           the tree-sitter runtime, the ABI the
//                                        P/Invoke layer is written against
//   tree-sitter-grammars.{dylib,so,dll}  every bundled grammar in one library,
//                                        each exporting tree_sitter_<lang>()
//
// Both carry a "lib" prefix everywhere except Windows, which does not use one, and
// TreeSitter.Bindings copies them to the output of everything that references it.
//
// Then the static archive a native AOT publish links in instead of loading:
//
//   tree-sitter.{a,lib}                  the runtime above, archived rather than
//                                        linked
//
// A JIT build ignores it and loads the shared libraries at runtime instead.
//
// Sources are vendored at the versions pinned below into vendor/, which is
// gitignored. Output lands in artifacts/<rid>/.
//
// Usage: dotnet run native/build.cs [-- [--clean] [--target <rid>]]
//        native/build.cs [--clean] [--target <rid>]   (Unix, via the shebang)
//
// The compiler is $CC, or cc (gcc on Windows) if that is unset. The shared
// libraries want a GNU toolchain — MSYS2 or mingw-w64 on Windows — because
// tree-sitter's core marks nothing __declspec(dllexport) and relies on ld
// exporting everything instead. The archive is the opposite case on Windows and
// comes from cl.exe and lib.exe, which this script locates itself rather than
// expecting a Developer Command Prompt; see BuildArchive and UseDeveloperEnvironment.
//
// --target cross-compiles for another RID, which needs a $CC that can produce it
// (x86_64-w64-mingw32-gcc for win-x64 from Linux, say). Without it the host RID
// is built. Cross-compiling a win RID gets the DLLs only, because cl.exe runs
// nowhere but Windows.
//
// This is the local-development path. CI produces the same files for each shipping
// RID; see native/README.md.

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Pinned upstream sources. Bumping one is a deliberate act: the tree-sitter core
// version fixes the C ABI the bindings are written against, and a grammar version
// fixes the node type names the queries match on.
const string TreeSitterVersion = "v0.26.9";

// Grammars compiled into the single tree-sitter-grammars artifact. Subdirectory is
// the path under the checkout holding src/ — "." for a single-language repo,
// "typescript"/"tsx" for the multi-grammar TypeScript one.
// TypeScript and TSX are two grammars out of one checkout, cloned once and
// compiled twice; TSX is a separate grammar rather than a file extension because
// its JSX syntax is ambiguous with type assertions.
Grammar[] grammars =
[
    new("tree-sitter-c-sharp", "v0.23.5", "."),
    new("tree-sitter-typescript", "v0.23.2", "typescript"),
    new("tree-sitter-typescript", "v0.23.2", "tsx"),
];

const string Usage = "Usage: dotnet run native/build.cs [-- [--clean] [--target <rid>]]";

var clean = false;
string? target = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--clean":
            clean = true;
            break;

        case "--target" when i + 1 < args.Length:
            target = args[++i];
            break;

        case "--target":
            throw Fail($"--target needs a RID, e.g. --target win-x64.{Environment.NewLine}{Usage}");

        default:
            throw Fail($"Unrecognized argument '{args[i]}'.{Environment.NewLine}{Usage}");
    }
}

// AppContext.BaseDirectory points at the compiled output, not at this file, so
// the script locates itself the only way it can.
var here = Path.GetDirectoryName(ScriptPath())!;
var vendor = Path.Combine(here, "vendor");

var hostRid = $"{HostOperatingSystem()}-{HostArchitecture()}";
var rid = target ?? hostRid;

if (rid.Split('-') is not [_, "x64" or "arm64"])
{
    throw Fail($"Unsupported RID '{rid}'; expected <osx|linux|win>-<x64|arm64>.{Environment.NewLine}{Usage}");
}

// Windows has no "lib" prefix, and produces a DLL whose exports come from the
// linker rather than from source annotations — see the header comment.
var (os, sharedExtension, sharedFlag, libraryPrefix) = rid.Split('-')[0] switch
{
    "osx" => ("osx", "dylib", "-dynamiclib", "lib"),
    "linux" => ("linux", "so", "-shared", "lib"),
    "win" => ("win", "dll", "-shared", ""),
    var other => throw Fail($"Unsupported RID '{rid}': unknown platform '{other}'.{Environment.NewLine}{Usage}"),
};

// The default C compiler is only ever right for the host; a cross build has to
// say which toolchain to use, because 'cc' would silently produce host binaries
// and drop them in another RID's directory.
var compiler = Environment.GetEnvironmentVariable("CC") is { Length: > 0 } configured
    ? configured
    : rid == hostRid
        ? OperatingSystem.IsWindows() ? "gcc" : "cc"
        : throw Fail(
            $"Cross-compiling {hostRid} -> {rid} needs $CC set to a toolchain that targets {rid}, "
            + "for instance CC=x86_64-w64-mingw32-gcc for win-x64.");

// The archiver has to come from the same toolchain as the compiler, so it is
// derived from $CC's target prefix unless $AR says otherwise.
var archiver = Environment.GetEnvironmentVariable("AR") is { Length: > 0 } configuredArchiver
    ? configuredArchiver
    : compiler.EndsWith("gcc", StringComparison.Ordinal)
        ? string.Concat(compiler.AsSpan(0, compiler.Length - 3), "ar")
        : "ar";

// -fPIC is the default on Windows and warns if asked for; libgcc is linked
// statically so the DLLs stand alone beside the managed assembly.
string[] positionIndependent = os == "win" ? [] : ["-fPIC"];

// Without this, cc stamps every object with the SDK it was built on and the AOT
// link warns that the archive wants a newer macOS than the executable around it.
// The number is ILC's own deployment target for osx-arm64; it moves when that
// does.
string[] deploymentTarget = os == "osx" ? ["-mmacosx-version-min=12.0"] : [];

string[] linkFlags = os == "win"
    ? [sharedFlag, "-static-libgcc"]
    : [sharedFlag, .. deploymentTarget];

// The static archive follows whichever archiver writes it, which on Windows is not
// the one writing the DLLs — see BuildArchive.
var (staticPrefix, staticExtension) = os == "win" ? ("", ".lib") : ("lib", ".a");

// cl.exe and lib.exe ship with Visual Studio and exist nowhere else, so a Windows
// target built from macOS or Linux gets its DLLs and stops there. That is a warning
// rather than an error: cross-building the shared libraries is still worth doing on
// its own, and a consumer's AOT publish fails clearly if the archive it needs is
// absent.
var canBuildArchives = os != "win" || OperatingSystem.IsWindows();

// Which toolset a Developer Command Prompt would select. vcvarsall takes a bare
// architecture for a native build and host_target for a cross one.
var architecture = rid.Split('-')[1];
var vcvarsArchitecture = architecture == HostArchitecture()
    ? architecture
    : $"{HostArchitecture()}_{architecture}";

// Set up once, on the first archive; see UseDeveloperEnvironment.
var developerEnvironmentReady = false;

var output = Path.Combine(here, "artifacts", rid);
var intermediate = Path.Combine(here, "obj", rid);

if (clean)
{
    Delete(Path.Combine(here, "obj"));
    Delete(Path.Combine(here, "artifacts"));
}

Directory.CreateDirectory(vendor);
Directory.CreateDirectory(output);
Directory.CreateDirectory(intermediate);

Console.WriteLine("Vendoring sources");
VendorClone("tree-sitter", TreeSitterVersion, "https://github.com/tree-sitter/tree-sitter");
foreach (var grammar in grammars)
{
    VendorClone(grammar.Name, grammar.Tag, $"https://github.com/tree-sitter/{grammar.Name}");
}

Console.WriteLine($"Building {libraryPrefix}tree-sitter.{sharedExtension}");
{
    // lib/src/lib.c #includes every other core translation unit, so the whole
    // runtime is one compilation.
    var core = Path.Combine(vendor, "tree-sitter", "lib");
    var objectFile = Path.Combine(intermediate, "tree-sitter.o");

    Run(compiler,
        ["-c", "-O2", .. positionIndependent, .. deploymentTarget, "-std=c11",
         "-I", Path.Combine(core, "src"),
         "-I", Path.Combine(core, "include"),
         "-o", objectFile,
         Path.Combine(core, "src", "lib.c")]);

    // shim.c is ours, not upstream's: it exposes the ABI-range macros from
    // api.h as functions, which is the only way anything outside C can read
    // them. Compiled against the same include path as the runtime above, so the
    // numbers it returns are the ones this artifact was built with.
    var shimObjectFile = Path.Combine(intermediate, "shim.o");
    Run(compiler,
        ["-c", "-O2", .. positionIndependent, .. deploymentTarget, "-std=c11",
         "-I", Path.Combine(core, "include"),
         "-o", shimObjectFile,
         Path.Combine(here, "shim.c")]);

    // Nothing in the core is annotated for export, so on Windows every symbol
    // reaches the DLL's export table by way of ld's export-all default. The
    // grammars below are the opposite case: parser.c marks its one entry point
    // __declspec(dllexport), which turns that default off and leaves exactly it.
    Run(compiler, [.. linkFlags, "-o", Path.Combine(output, $"{libraryPrefix}tree-sitter.{sharedExtension}"), objectFile, shimObjectFile]);

    BuildArchive(
        "tree-sitter",
        [Path.Combine(core, "src", "lib.c"), Path.Combine(here, "shim.c")],
        [Path.Combine(core, "src"), Path.Combine(core, "include")],
        []);
}

Console.WriteLine($"Building {libraryPrefix}tree-sitter-grammars.{sharedExtension}");
var objectFiles = new List<string>();
foreach (var grammar in grammars)
{
    Console.WriteLine($"  {grammar.Name}{(grammar.Subdirectory == "." ? "" : $"/{grammar.Subdirectory}")}");
    var source = Path.Combine(vendor, grammar.Name, grammar.Subdirectory, "src");

    // -O1, not -O2: a generated parser.c runs to tens of megabytes and the higher
    // optimization level costs minutes for no measurable parse-time win.
    foreach (var unit in (string[])["parser", "scanner"])
    {
        var file = Path.Combine(source, $"{unit}.c");
        if (!File.Exists(file))
        {
            continue;
        }

        var objectFile = Path.Combine(intermediate, $"{grammar.Name}-{grammar.Subdirectory}-{unit}.o");
        Run(compiler, ["-c", "-O1", .. positionIndependent, .. deploymentTarget, "-std=c11", "-I", source, "-o", objectFile, file]);
        objectFiles.Add(objectFile);
    }
}

Run(compiler, [.. linkFlags, "-o", Path.Combine(output, $"{libraryPrefix}tree-sitter-grammars.{sharedExtension}"), .. objectFiles]);

Console.WriteLine();
Console.WriteLine($"Wrote to native/artifacts/{rid}:");
foreach (var file in new DirectoryInfo(output).EnumerateFiles().OrderBy(f => f.Name))
{
    Console.WriteLine($"  {file.Name,-32} {file.Length,10:N0} bytes");
}

return 0;

/// <summary>
/// Compiles <paramref name="sources"/> and archives them into artifacts/ as the
/// static library a native AOT publish links in.
/// </summary>
/// <remarks>
/// <para>
/// Windows is a second toolchain here, and the only place in this script that is
/// not GNU. ILC links with MSVC's link.exe, which does not read what GNU ar
/// writes, so these objects come from cl.exe and the archive from lib.exe.
/// </para>
/// <para>
/// That is allowed precisely because it is an archive. What forces a GNU compiler
/// on the shared libraries is exports — tree-sitter's core annotates none, and
/// only ld exports them anyway — and an archive has no export table to get wrong.
/// </para>
/// </remarks>
void BuildArchive(string name, string[] sources, string[] includes, string[] defines)
{
    var archive = Path.Combine(output, $"{staticPrefix}{name}{staticExtension}");

    if (!canBuildArchives)
    {
        Console.WriteLine($"Skipping {Path.GetFileName(archive)} — needs cl.exe and lib.exe, which only exist on Windows");
        return;
    }

    Console.WriteLine($"Building {Path.GetFileName(archive)}");

    if (os == "win")
    {
        UseDeveloperEnvironment();
    }

    var directory = Path.Combine(intermediate, name);
    Directory.CreateDirectory(directory);

    var objects = new List<string>();
    foreach (var source in sources)
    {
        var objectFile = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(source)}{(os == "win" ? ".obj" : ".o")}");

        Run(
            os == "win" ? "cl.exe" : compiler,
            os == "win"
                // /MT to match the static CRT the runtime's own objects carry;
                // ILC swaps in the dynamic UCRT over the top of that at link time.
                ? ["/nologo", "/c", "/O2", "/MT", "/std:c11",
                   .. includes.Select(include => $"/I{include}"),
                   .. defines.Select(define => $"/D{define}"),
                   $"/Fo{objectFile}",
                   source]
                : ["-c", "-O2", .. positionIndependent, .. deploymentTarget, "-std=c11",
                   .. includes.SelectMany(include => (string[])["-I", include]),
                   .. defines.Select(define => $"-D{define}"),
                   "-o", objectFile,
                   source]);

        objects.Add(objectFile);
    }

    // Removed first because both archivers only replace the members they are
    // given, so a source dropped from the build would otherwise survive.
    File.Delete(archive);
    Run(
        os == "win" ? "lib.exe" : archiver,
        os == "win" ? ["/nologo", $"/OUT:{archive}", .. objects] : ["rcs", archive, .. objects]);
}

/// <summary>
/// Brings the Visual Studio C++ toolchain into this process's environment, so cl.exe
/// and lib.exe run the way every other tool here does.
/// </summary>
/// <remarks>
/// <para>
/// cl.exe reads its include and library paths out of the environment rather than off
/// its command line, so it is not usable straight from <c>PATH</c>: it wants the setup
/// a Developer Command Prompt performs, and that setup is a batch file whose entire
/// effect is environment variables. Reading them back out of the same cmd.exe is the
/// only way to get at them — ILC finds link.exe by exactly this route
/// (findvcvarsall.bat), which is why an AOT publish works from an ordinary shell, and
/// doing it here makes the same true of this script.
/// </para>
/// <para>
/// A no-op when cl.exe already resolves, which covers a Developer Command Prompt and
/// any toolchain put on <c>PATH</c> by hand. vcvarsall prepends to the <c>PATH</c> it
/// inherits and sets nothing a GNU compiler reads, so the shared libraries keep
/// building with the same gcc afterwards.
/// </para>
/// </remarks>
void UseDeveloperEnvironment()
{
    if (developerEnvironmentReady)
    {
        return;
    }

    developerEnvironmentReady = true;

    if (OnPath("cl.exe"))
    {
        return;
    }

    var installer = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Microsoft Visual Studio", "Installer", "vswhere.exe");

    if (!File.Exists(installer))
    {
        throw Fail(
            "The static archives need cl.exe and lib.exe. cl.exe is not on PATH and there is no "
            + $"{installer} to locate it with. Install the \"Desktop development with C++\" workload, or run "
            + "this from a Developer Command Prompt.");
    }

    // -products * so a Build Tools install counts and not only a full Visual Studio,
    // and -requires so an install without the target's C++ component is rejected here
    // rather than by a vcvarsall that quietly sets nothing. Both flags, and the
    // component names, are the ones ILC's own findvcvarsall.bat uses.
    var installation = Capture(installer, here,
        "-latest", "-prerelease", "-products", "*",
        "-requires", architecture == "arm64"
            ? "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
            : "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
        "-property", "installationPath");

    var vcvarsall = installation.Length == 0
        ? ""
        : Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvarsall.bat");

    if (!File.Exists(vcvarsall))
    {
        throw Fail(
            "No Visual Studio install with the C++ tools was found. Add the \"Desktop development with C++\" "
            + "workload through the Visual Studio Installer.");
    }

    Console.WriteLine($"  MSVC {vcvarsArchitecture} from {installation}");

    // Passed as one command line rather than through ArgumentList: cmd /c does its
    // own quote handling, and /s makes it predictable — strip the outer pair, take
    // everything between verbatim.
    var startInfo = new ProcessStartInfo("cmd.exe")
    {
        Arguments = $"/s /c \"\"{vcvarsall}\" {vcvarsArchitecture} > nul && set\"",
        WorkingDirectory = here,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    using var process = Process.Start(startInfo)
        ?? throw Fail("Could not start cmd.exe to read the Visual Studio environment.");

    var variables = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();

    if (process.ExitCode != 0)
    {
        throw Fail(
            $"\"{vcvarsall}\" {vcvarsArchitecture} exited with {process.ExitCode}."
            + $"{Environment.NewLine}{error.Trim()}");
    }

    foreach (var line in variables.Split('\n'))
    {
        // Only the line break, not surrounding whitespace: a value is whatever
        // vcvarsall set, trailing spaces included.
        var setting = line.TrimEnd('\r');
        var separator = setting.IndexOf('=');
        if (separator > 0)
        {
            // Windows environment names are case-insensitive, so the "Path" vcvarsall
            // prints lands on the "PATH" already here rather than beside it.
            Environment.SetEnvironmentVariable(setting[..separator], setting[(separator + 1)..]);
        }
    }

    if (!OnPath("cl.exe"))
    {
        throw Fail(
            $"{installation} has C++ tools but not for {vcvarsArchitecture} — cl.exe is still not on PATH after "
            + "vcvarsall. Add that target's toolset through the Visual Studio Installer.");
    }
}

/// <summary>
/// Clones <paramref name="url"/> at <paramref name="tag"/> into vendor/, or
/// leaves an existing checkout at the right tag alone. Shallow, because only one
/// commit is ever wanted.
/// </summary>
void VendorClone(string name, string tag, string url)
{
    var directory = Path.Combine(vendor, name);

    if (Directory.Exists(Path.Combine(directory, ".git")))
    {
        var current = Capture("git", directory, "describe", "--tags", "--exact-match");
        if (current == tag)
        {
            return;
        }

        Console.WriteLine($"  {name}: {(current.Length == 0 ? "untagged" : current)} -> {tag}, re-cloning");
        Delete(directory);
    }

    Console.WriteLine($"  {name} @ {tag}");
    Run("git",
        "-c", "advice.detachedHead=false",
        // Vendored sources are compiler input, never edited. A contributor whose
        // global config turns autocrlf on would otherwise get CRLFs in the
        // grammar fixtures an external scanner is tested against.
        "-c", "core.autocrlf=false",
        "clone", "--depth", "1", "--branch", tag, "--quiet", url, directory);
}

/// <summary>
/// Runs a command with its output inherited, failing the build on a nonzero exit or
/// on a tool that is not there to start.
/// </summary>
/// <remarks>
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so a path
/// containing a space is passed through as one argument rather than needing to be
/// quoted correctly.
/// </remarks>
static void Run(string file, params string[] arguments)
{
    var startInfo = new ProcessStartInfo(file);
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    Process? started;
    try
    {
        started = Process.Start(startInfo);
    }
    catch (Win32Exception exception)
    {
        // Almost always a tool that is not installed, which is worth saying plainly
        // rather than as an unhandled exception with a stack trace.
        throw Fail($"Could not run '{file}': {exception.Message}");
    }

    using var process = started ?? throw Fail($"Could not start '{file}'.");

    process.WaitForExit();

    if (process.ExitCode != 0)
    {
        throw Fail($"{file} {string.Join(' ', arguments)}{Environment.NewLine}exited with {process.ExitCode}.");
    }
}

/// <summary>Whether <paramref name="tool"/> is resolvable on this process's PATH.</summary>
static bool OnPath(string tool) =>
    (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(directory => File.Exists(Path.Combine(directory, tool)));

/// <summary>Runs a command and returns its trimmed stdout, or an empty string if it failed.</summary>
static string Capture(string file, string workingDirectory, params string[] arguments)
{
    var startInfo = new ProcessStartInfo(file)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        return "";
    }

    var standardOutput = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0 ? standardOutput.Trim() : "";
}

/// <summary>
/// Removes a directory tree, including the read-only files git leaves in
/// <c>.git</c>, which <see cref="Directory.Delete(string, bool)"/> refuses to
/// unlink on Windows.
/// </summary>
static void Delete(string directory)
{
    if (!Directory.Exists(directory))
    {
        return;
    }

    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
    {
        File.SetAttributes(file, FileAttributes.Normal);
    }

    Directory.Delete(directory, recursive: true);
}

/// <summary>The RID platform component for the machine this is running on.</summary>
static string HostOperatingSystem()
{
    if (OperatingSystem.IsMacOS())
    {
        return "osx";
    }

    if (OperatingSystem.IsLinux())
    {
        return "linux";
    }

    if (OperatingSystem.IsWindows())
    {
        return "win";
    }

    throw Fail($"Unsupported platform {RuntimeInformation.OSDescription}.");
}

static string HostArchitecture() => RuntimeInformation.ProcessArchitecture switch
{
    Architecture.Arm64 => "arm64",
    Architecture.X64 => "x64",
    var other => throw Fail($"Unsupported architecture {other}."),
};

/// <summary>
/// Reports a build failure and exits nonzero.
/// </summary>
/// <returns>
/// Never — the process is already gone. It is typed as an exception so callers
/// write <c>throw Fail(...)</c>, which satisfies the compiler's flow analysis in
/// statement and expression position alike.
/// </returns>
static Exception Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(1);
    return new UnreachableException();
}

static string ScriptPath([CallerFilePath] string path = "") => path;

/// <param name="Subdirectory">
/// Path under the checkout holding <c>src/</c>. "." unless the repository ships
/// several grammars.
/// </param>
record Grammar(string Name, string Tag, string Subdirectory);
