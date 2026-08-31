// Compiled into the tree-sitter runtime artifact, beside lib.c.
//
// tree-sitter's accepted grammar ABI range is two preprocessor macros in
// tree_sitter/api.h with no accessors behind them, so a caller outside C cannot
// read it — the only way to ask is to build a parser and watch
// ts_parser_set_language return false. These two functions make the range
// readable, so the managed layer can reject an unusable grammar where it is
// still holding the grammar's name, without allocating a parser to find out.
//
// The point is that they compile from the same header as the runtime they ship
// with: a bump of TreeSitterVersion in build.cs moves both together, which a
// pair of constants mirrored into C# would not.
//
// Deliberately not annotated __declspec(dllexport). Nothing in tree-sitter's
// core is either, so the Windows DLL gets its exports from ld's export-all
// default, and one annotated symbol anywhere in the link turns that default off
// and leaves only the annotated ones. See the linker comments in build.cs.

#include <tree_sitter/api.h>

uint32_t ts_shim_language_abi_version(void) {
  return TREE_SITTER_LANGUAGE_VERSION;
}

uint32_t ts_shim_min_compatible_language_abi_version(void) {
  return TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION;
}
