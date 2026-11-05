# Third-party notices

`ungodot` is MIT licensed. It contains data and is derived from work by others:

## gore

* https://github.com/figurophobia/gore
* MIT License, Copyright (c) figurophobia
* The pack parsing, crypto, key recovery, extraction, GDRE integration and CLI design in
  `UnGodot.Core` / `UnGodot.Cli` are a C# port of `gore` (Rust). The upstream source is kept
  in `refs/gore/` for reference and is not part of the build.

## GDRE Tools (gdsdecomp)

* https://github.com/GDRETools/gdsdecomp
* MIT License, Copyright (c) 2019 bruvzg
* `UnGodot.GDScript` is a C# port of the GDScript bytecode decompiler from GDRE Tools
  (`bytecode/bytecode_base.cpp`, `gdscript_v1_tokenizer_compat.cpp`, `variant_writer_compat.cpp`
  and the generated per-revision tables).
* `src/UnGodot.GDScript/Data/bytecode_versions.json` is copied verbatim from
  `misc/bytecode_versions.json` of GDRE Tools.
* `src/UnGodot.GDScript/Data/function_arg_counts.json` is extracted from the generated
  `bytecode/*.cpp` tables of GDRE Tools.
* The upstream source is kept in `refs/gdre/` for reference and is not part of the build.

## Godot Engine

* https://godotengine.org
* MIT License, Copyright (c) 2014-present Godot Engine contributors,
  Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur
* The GDScript bytecode and variant formats implemented here are defined by the engine, and
  parts of the decompiler logic originate in Godot's own tokenizer sources (via GDRE Tools).

`ungodot` does not include GDRE Tools itself; `ungodot recover` calls a separately downloaded
binary and `ungodot setup` fetches it from its official releases.