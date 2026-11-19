<!-- Copyright (c) 2026 Legion Builds -->
<!-- SPDX-License-Identifier: LGPL-2.1-or-later -->
# SLua words (generated)

`slua_words.tsv` lists the words the script editor colours and explains in an SLua (Lua) script: the Luau reserved words and
the `ll`, `ossl`, `iw` and `bot` functions. It is read-only data: do not edit it by hand.

- Source of the functions: the SLua script engine's own function tables, the generated binding files `LlBindings.g.cs` and
  `OsBindings.g.cs` in `Source/SLua.ScriptEngine/Bindings` of OpenSim-Tranquillity. Each table line gives a function's name,
  what it returns and the types of its parameters; the tooltip holds only that. No text of the official viewer's keyword files
  is used.
- The Luau reserved words are the language's own (<https://luau.org/syntax>) and are listed in the generator.
- Regenerate, and commit the result, when the engine's tables change:
  `pwsh -File tools/generate-slua-words.ps1 -BindingsDir <the Bindings folder above>`.
- The file is embedded in the Viewer.Shell assembly and read by `LslKeywords.ShippedSlua`.
