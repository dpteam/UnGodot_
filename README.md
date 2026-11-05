# UnGodot

A Godot reverse engineering toolkit in **C# / .NET 9** — a port of
[gore](https://github.com/figurophobia/gore) (Rust), extended with an "anything you point it at"
discovery layer, multi-pack merging and streaming access to packs larger than 2 GiB.

It takes a Godot game (a `.pck`, a self-contained `.exe`, a `.zip`/`.apk` that carries a pack,
or an entire game folder), finds every pack, recovers the pack encryption key when the pack is
encrypted, extracts and decrypts the contents, and can then rebuild the project natively:
`project.godot`, decompiled GDScript, PNG textures and text resources/scenes. GDRE Tools stays
available for the parts the native pipeline does not cover yet.

```
$ ungodot detect "\\DPT0\d$\Acorn"
input            \\DPT0\d$\Acorn (directory)
packs found      6
   1. content\acorn\sonic0.pck
      PCK (standalone Godot pack), v1, Godot 1.0.0, 9477 files, 1.86 GiB
   ...
   4. content\acorn\sonic3.pck
      PCK (standalone Godot pack), v1, Godot 1.0.0, 33337 files, 1.86 GiB [base pack]
   ...
```

## Requirements

* .NET 9 SDK (or runtime) — `dotnet --version` ≥ 9.
* Windows, Linux or macOS. AES-NI/ARM-Crypto gives a ~20× faster key search; there is a
  portable fallback.

## Build and run

```bash
dotnet build -c Release
dotnet run --project src/UnGodot.Cli -c Release -- --help
dotnet test                       # 79 tests: packs, crypto, key search, discovery, decompiler, recovery, textures, resources
dotnet publish src/UnGodot.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
```

The executable is called `ungodot`.

## Usage

```
ungodot <command> [options] [path]

Commands:
  detect   <path>     Find every Godot pack around a file or folder
  info     <path>     Show the pack header and its file table
  key      <path>     Recover the pack encryption key (prints it in hex)
  extract  <path>     Extract the contents (all packs found, merged)
  recover  <path>     Recover a full Godot project with GDRE Tools
  decompile <path>    Decompile .gdc bytecode to GDScript source (native, no GDRE)
  restore  <path>     Native pipeline: extract, project.godot, scripts, textures, resources
  plugins  <project>  Restore the native GDExtension libraries
  editor   [project]  Install and/or open the Godot editor the project needs
  setup               Download GDRE Tools
  selftest            Check the crypto and pack code on synthetic data
```

Options: `-k/--key <HEX>`, `-o/--out <DIR>`, `--gdre <PATH>`, `--scan <PATH>`,
`--wordlist <FILE>`, `--filter <GLOB>`, `--jobs <N>`, `--json`, `-v`, `-q`,
`--log-file`, `--no-log-file`, `--dry-run`, `--no-plugins`, `--all`, `--no-merge`.
`ungodot help` prints the full list.

### Examples

```bash
# What is in this game folder, and which pack is the base one?
ungodot detect "D:\Games\MyGame"

# Inspect one pack (works on a .exe with an appended/embedded pack too)
ungodot info "D:\Games\MyGame\game.pck"

# Extract a single pack, or a whole folder (all packs merged, base pack last)
ungodot extract game.pck -o ./raw
ungodot extract "D:\Games\MyGame" -o ./raw

# Only scripts and scenes
ungodot extract game.pck --filter "*.gd;*.gdc;*.tscn;project.binary" -o ./scripts

# Recover a project you can open in the editor
ungodot setup
ungodot recover game.pck -o ./project

# Encrypted pack whose key is not embedded: point at a memory dump
ungodot extract game.pck --scan dump.bin
```

## How it works

### Containers and pack formats

| Format | Engine | Notes |
|---|---|---|
| v0 | Godot 2.x | no flags, no MD5, engine version left empty |
| v1 | Godot 3.x | legacy table layout, no per-file flags |
| v2 | Godot 4.0 | explicit `file_base`, table right after the header |
| v3 / v4 | Godot 4.x | stored directory offset, relative file base |

The locator understands standalone packs, packs appended to an executable (the tail pointer
Godot writes), packs embedded anywhere in a binary, and packs whose `GDPC` magic was renamed
(found by structure, verified by the table + MD5). `Pck` entries are read through a buffered
reader over a random-access source, so packs bigger than 2 GiB never have to fit in memory.

### Dynamic discovery ("never give up")

`detect`/`extract`/`info` all go through the same resolver:

1. the path you named, if it is a pack or an executable with a pack;
2. otherwise the folder you named, scanned recursively (bounded) for `.pck`, executables and
   archives;
3. otherwise the folders *around* the file (parent and grandparent), because games split
   binaries and content into `exec/` and `content/<name>/` — Sonic Colors Ultimate is exactly
   that layout;
4. a missing path walks up to the closest existing folder instead of failing;
5. unreadable files (a running game, an antivirus lock, a dying network share) are reported
   and skipped — the rest of the packs are still found. Files are opened with
   `FileShare.ReadWrite | Delete` and reads are retried.

ZIP/APK archives are inspected for a `.pck` inside and extracted to a temporary file on demand.
CRI CPK containers (`PCCriPak.cpk`, audio/movies) are recognised and skipped with a clear note
instead of being mistaken for a broken pack.

### Key recovery

The pack is the oracle. The encrypted file table starts with `[MD5][length][IV][ciphertext]`
and its plaintext always begins with the length of the first file path followed by that path,
so decrypting the first blocks with a candidate key produces either plausible text or noise.
Every 32 byte window of the executable, its sibling libraries and `--scan` files is tried,
first on the data sections (`.rdata`/`.data`/`.rodata`), then over the whole file, in tiers
from strict to exhaustive, with a byte-diversity pre-filter and a final MD5-verified decrypt
of the whole table. A password wordlist is the last resort.

The AES-256 key schedule and block encryption use AES-NI intrinsics directly, so a 64 MiB
exhaustive pass takes well under a second on a modern CPU.

### Extraction

Entries are decoded, decrypted (AES-256-CFB, `FileAccessEncrypted` framing), MD5-verified when
the pack stores a checksum, and written in parallel through a `.part` file plus an atomic
rename. Paths are sanitised (`res://../../etc/passwd` is rejected, `user://` lands in
`@@user@@/`), long Windows paths are handled, and a single broken entry does not abort the run.
The output includes `ungodot_manifest.txt` (MD5, path, size per file).

When several packs are present they are merged into one directory with the base pack
(`project.binary`) written **last**, so its project settings win — the same layering the game
uses for DLC and patches. `recover` does this automatically before calling GDRE Tools.

## Logging

`System.Diagnostics.Trace` with listeners created **in code** — no `app.config`, no
`appsettings.json`, no `<system.diagnostics>` section anywhere:

* `Trace.Listeners` is cleared of `DefaultTraceListener`, so a message is never emitted twice
  (once by our sink, once by the debugger bridge);
* exactly one console sink writes to **stderr** (stdout stays machine-readable, so
  `ungodot info game.pck --json | jq` works);
* an optional size-capped rolling file sink (default
  `%LOCALAPPDATA%\ungodot\logs\ungodot.log`, `--no-log-file` to disable);
* levels: `trace|debug|info|warn|error|off`, `-v` = debug, `-q` = warnings;
* child processes (GDRE, `dotnet`) are pumped through one renderer that keeps GDRE's parallel
  progress bars on one line and prints their messages above it, again exactly once.

## Layout

| Path | What lives there |
|---|---|
| `src/UnGodot.Core/IO` | random-access file source, buffered reader, long-path helpers |
| `src/UnGodot.Core/Crypto` | AES-256(-NI) schedule, CFB, MD5, `FileAccessEncrypted` blob |
| `src/UnGodot.Core/Pck` | header parsing v0–v4, locator, file table |
| `src/UnGodot.Core/KeyFind` | oracle, tiered parallel scan, password recovery |
| `src/UnGodot.Core/Extract` | safe paths, parallel extraction, MD5 verification |
| `src/UnGodot.Core/Discovery` | the "find every pack around this input" resolver |
| `src/UnGodot.Core/Workspace` | orchestration: discovery → open → key → extract |
| `src/UnGodot.Core/Binfmt` | PE/ELF section readers for the key search |
| `src/UnGodot.Core/Gdre` | GDRE Tools lookup, download, output renderer |
| `src/UnGodot.Core/Godot` | editor version detection and downloads |
| `src/UnGodot.Core/Plugins` | GDExtension native library restoration (FMOD/Spine/Sentry) |
| `src/UnGodot.Variants` | Godot binary variants (gdc constants, project.binary, .res) and text writer |
| `src/UnGodot.GDScript` | GDScript bytecode container, revision detection, decompiler |
| `src/UnGodot.Cli` | the `ungodot` command line |
| `src/UnGodot.Recovery` | recovery pipeline, project.binary > project.godot, stages and report |
| `src/UnGodot.Textures` | Godot 3 stream textures, BC1/BC2/BC3/BC4/BC5 + raw formats, PNG writer |
| `src/UnGodot.Resources` | binary resources (RSRC/RSCC), legacy variants, `.tres`/`.tscn` writer |
| `tests/UnGodot.Tests` | 79 tests: crypto vectors, synthetic packs v0-v4, encrypted packs, key search, discovery, ZIP, decompiler, recovery, textures, resources |

`refs/` holds the upstream sources this port was written from (`gore` in Rust, `gdsdecomp` in
C++); it is ignored by git. See `THIRD_PARTY_NOTICES.md`.

## Native decompilation (no GDRE)

`ungodot decompile` turns Godot 3 compiled scripts back into source. This is a full port of
GDRE's bytecode decompiler, not a wrapper: the `GDSC` container, the XOR-obfuscated
identifier table, the packed token stream (1 or 4 bytes per token), binary variants for the
constant pool, and the token pretty-printer that rebuilds line structure and indentation.

```bash
# One script, next to it (script.gd)
ungodot decompile game/scripts/player.gdc

# A whole tree, keeping the folder layout
ungodot decompile extracted/ -o extracted_decompiled

# Hint the engine version (major.minor is enough)
ungodot decompile extracted/ --engine 3.1 -o out
```

The revision is detected from the bytecode alone: every revision that shares the bytecode
version is validated against the stream, including the number of arguments at every built-in
call site — that is what separates 3.1.0 from 3.1.1, or 3.2 from 3.5. The opcode and function
tables for all known revisions ship inside the assembly (from GDRE Tools, MIT).

Status:

* GDScript 1.0 (Godot 2.x, 3.x): **supported**, output is byte-identical to GDRE Tools.
* GDScript 2.0 (Godot 4.x, `GDSC` v100+): recognised, but the payload is Zstandard-compressed
  and is not implemented yet.
* Encrypted scripts (`.gde`): recognised and reported, decryption is on the roadmap.

Validation on Sonic Colors Ultimate (Godot 3.1.2, revision `514a3fb`): all 38 shipped `.gdc`
files decompile to files **byte-identical** to GDRE Tools 2.7.4, with no engine hint passed.

## Native recovery pipeline

`ungodot restore` runs the native stages over a game (or an already extracted folder):

1. extract every pack found, base pack last;
2. `project.binary` → `project.godot` (`ECFG` reader + Godot text writer);
3. `.gdc` → `.gd` with the native decompiler, compiled scripts moved to `.autoconverted/`;
4. `.stex` → `.png` (BC1/BC2/BC3/BC4/BC5, raw L8/LA8/R8/RG8/RGB8/RGBA8/RGBA4444/RGBA5551,
   RGBE9995, float/half formats);
5. binary resources → text: `.material`/`.mesh`/`.anim`/`.res` to `.tres`, `.scn` and packed
   scenes to `.tscn`;
6. a machine readable `ungodot_recovery.json` report.

```bash
ungodot restore "D:\Games\MyGame" -o ./project
ungodot restore game.pck --filter "*.gdc;project.binary" -o ./project   # quick look
```

Stages fail independently: a broken stage is recorded in the report and the remaining ones
still run. `--no-scripts`, `--no-textures`, `--no-resources`, `--keep-gdc` and
`--engine 3.1.2` are available.

Textures are exported with their original base name (`res://.import/hero.png-<md5>.s3tc.stex`
becomes `hero.png`), the best decodable platform variant wins (desktop S3TC over ASTC or a
`-low` variant), and same-named sources keep both files by suffixing the import hash. Mobile
formats that need a full ASTC/ETC/PVRTC/BPTC decoder are reported instead of guessed.

Resources are decoded from the real binary format: `RSRC` files and the `RSCC` container
(`FileAccessCompressed`: mode, block size, total size, a block-size table, then Zstandard or
Deflate blocks), the legacy variant ids used inside resources, inline and table string
references, and the `_bundled` dictionary of a `PackedScene`, which is rebuilt into
`[node]`/`[connection]` blocks. `PackedScene` node parents stored as node paths, instance
placeholders, groups and connections are handled. The binary original is kept next to the
text copy, because game resources still reference `res://thing.material`.

Validated on Sonic Colors Ultimate: the 35 project settings are written, GDRE Tools parses the
resulting `project.godot` (and re-saves it line-for-line identical), all 72 scripts of a
multi-pack extraction decompile with revision 3.1.1, and 2 603 of the 2 605 stream textures of
`sonic0.pck` convert to PNG in about 45 s (the two HDR ones included), with 188 name collisions
kept as separate files. Decoded pages were checked visually against the game's UI and stage
textures. Of the 3 773 binary resources of `sonic3.pck` (718 materials, 1 288 animations,
45 meshes, 70 scenes and 1 652 `.converted.res` files), all convert in ~14 s with zero failures
(1 179 of them are packed scenes); a random sample of the text output re-saves through the
engine's own text loader.

Still to come, in this order:

* resource *references between files*: after conversion, `res://thing.material` paths inside
  scenes still point at the binary originals, which Godot 3 loads fine but a text-only project
  would want rewritten;
* the Godot 4 variant decoder, so `project.binary`, scripts and resources of 4.x projects work.

## Verified on real data

Sonic Colors Ultimate (`\\DPT0\d$\Acorn`, Godot 3.1.2 pack v1, 11 GiB in 6 packs):

* discovery finds all 6 packs plus the CRI CPK (reported and skipped);
* every pack is parsed and its table read (9 477 / 8 827 / 6 972 / 33 337 / 4 783 / 14 001 files);
* a full extraction of `sonic5.pck` produced 14 001 files / 852 MB with zero failures in 53 s
  over SMB;
* GDRE Tools recovered the extracted sample into a Godot 3.1.2 project;
* 2 603 of 2 605 stream textures of `sonic0.pck` convert to PNG from their S3TC/RGBE9995 data
  in ~45 s (BC1/BC3/RGBE9995 decoders, no external library), with the two remaining formats
  reported instead of guessed;
* 3 773 binary resources of `sonic3.pck` (materials, animations, meshes, imported scenes and
  `.converted.res` files) convert to `.tres`/`.tscn` in ~14 s with zero failures; the output
  re-saves through the engine's text loader, and the packed scenes rebuild their full node
  trees (skeletons, mesh instances, animation players) from the `_bundled` dictionary.

## Limitations

* Sparse bundle packs (Android/iOS) are rejected with an explicit message.
* Hardened engines that derive the pack key at runtime (Godot Secure) need a memory dump
  (`--scan dump.bin`); packs switched to Camellia are not decrypted.
* GDRE Tools is an external dependency (`ungodot setup` downloads it); project recovery quality
  is GDRE's.
* C# projects are extracted like any other; recovering the C# side needs the game's assembly.
* Use it on games you are allowed to inspect. The output remains covered by the game's licence.

## License

MIT for this port. Upstream `gore` is MIT as well. GDRE Tools is not included and has its own
terms.