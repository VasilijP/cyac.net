# cyac.net

.NET 10 / C# port of a legendary CYAC from 1991: *Chuck Yeager's Air Combat* for DOS.

[![CI](https://github.com/VasilijP/cyac.net/actions/workflows/ci.yml/badge.svg)](https://github.com/VasilijP/cyac.net/actions/workflows/ci.yml)

> **Status: early release (0.1.0).** A Windows (64-bit) download is on the
> [Releases](https://github.com/VasilijP/cyac.net/releases) page. **It contains no game data: you must
> supply your own copy of the original game** (the files, or the zip holding them; see below). Unzip the
> download, put the originals into its `game/` folder and start `cyac-fly.exe`. Nothing has to be
> installed — the .NET runtime is inside the zip. Linux and macOS have no download yet; build from
> source (below).

This is an unofficial, fan-made project. It is not affiliated with, endorsed by or connected to
Electronic Arts or the estate of Chuck Yeager. All trademarks belong to their owners and are used only
to name the game this port works with. **The repository contains no game data.** To play, you need
your own copy of the original game.

**TLDR**: `dotnet run --project src/CYAC.Port.Host -c Release -- --mission 25` is how I run it to launch a
mission directly. The defaults are the ones to play with: full screen at 1920x1080, vsync, mode-13hx's
fast mode, no text readout. Remove the `--mission 25` and you get a menu; add `--windowed` to run in a
window, `--width`/`--height` to change the size.

## What you need

The DOS release, version 1.0. The port identifies the files by content, not by name, and refuses any
other set:

| File | Size (bytes) | SHA-256 |
|:--|--:|:--|
| `yeager.exe` | 184,310 | `a97e65866f30abfc90513e1724fd8185341dd1a9430f6a62862fa9e9f294f83a` |
| `1a.lib` | 34,426 | `a64da3a42616be15937f25f145735e43afd79a32a75f950ea4f067abd5068853` |
| `1b.lib` | 101,681 | `dff404f043357d3a663a896e70a0a8b4089387b2e5aad61b7ec3211f67d2fb03` |
| `2a.lib` | 276,276 | `c4cbe41a63319c9c5d8f8e8ce1aa5ea90aa0e31b6a0343641406a7bd84c20461` |
| `2b.lib` | 85,683 | `48ad7846deaa8e25da55838511f3b8df8c427cd0431c8f19836a8f9d15ec49b9` |
| `3a.lib` | 339,508 | `189f8953173b31f5294f6c1daf05bb0094659af4c3a73217f6a433b43d287a0a` |
| `4a.lib` | 292,628 | `bcf23ec6babfdc922be196156032da6f48f88b390ebda70ea1e65cf3a1e068f9` |

`yeager.cfg` is not needed.

## How it works

The port never runs or reads the original program while you play. A transform step (`cyac-transform`)
first turns your copy into an **open data tree**: JSON for tables, missions, models and aircraft; PNG
for images; WAV for speech. It then proves the conversion lossless by rebuilding the originals from
the tree and comparing them byte for byte. The game reads only that tree, so everything in it is
readable and editable.

You do not have to run that step yourself. Drop the files, or the zip holding them, into the `game/`
folder and start the port: a **start-up check** finds them by content (inside folders and zips, names
and letter case do not matter), unpacks and converts them, verifies the result, installs it and loads
it — about a second and a half on a modern machine, and about half a second on later starts, which
reuse the tree. Each step is shown as it runs, with the reason when one is not green. Press Enter to fly.

## Where the port keeps things

Everything lives in one folder, the first of these that fits:

1. the folder given with `--home`;
2. the folder named by `CYAC_HOME`;
3. in a source checkout, the folder above the build output that holds `game/` or `sources/`;
4. the folder the executable sits in, when it is writable — so an unzipped copy keeps to itself;
5. otherwise a per-user folder: `%LOCALAPPDATA%\CYAC`, `~/Library/Application Support/CYAC`, or
   `$XDG_DATA_HOME/cyac` (else `~/.local/share/cyac`).

In it: `game/` (where you put the original files; `--game` points elsewhere), `data/` (the tree),
`port.json`, `settings.json`, `stats.json`, `screenshots/` and `preflight.log`. `port.json` holds the
port's own choices — which cockpit windows start open, and that every mission is available from the
start — each explained in the file. Delete any of these and the next start writes it again with its
defaults.

A tree kept somewhere else is named with `--data <dir>` or the `CYAC_DATA` environment variable; both
the game and the tests read it.

## Build and run from source

Prerequisites:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Vulkan:
  - **Windows**: the loader ships with your GPU driver.
  - **Linux**: `sudo apt install libvulkan1` (or your distribution's equivalent).
  - **macOS**: for now, the [LunarG Vulkan SDK](https://vulkan.lunarg.com/sdk/home#mac) with its
    `setup-env.sh` sourced in the shell you run from. Bundling MoltenVK is planned.

Steps, from the repository root:

```sh
# 1. put the original files, or the zip holding them, into game/ (git-ignores it)
# 2. fly: the start-up check builds the data tree on the first run, then opens the main menu
dotnet run --project src/CYAC.Port.Host -c Release
```

To convert the files yourself instead, or to check a copy without playing:

```sh
dotnet run --project src/CYAC.Port.Transform -c Release -- game data --verify   # a folder or a zip
dotnet run --project src/CYAC.Port.Host -c Release -- --preflight               # the checks, on the console
```

## Known limitations

- **Five missions can be flown but never won** — ALONE, BOLO, GAUNTLET, INSTR and MOOLAH. Their
  win rules are not modelled yet, so the sortie runs and the debriefing never reports success.
- **No joystick support.** Keyboard and mouse only, for now.
- **Enemy jets break off and leave.** That is the original's own behaviour, not a port bug: the
  engagement rules give an opponent reasons to disengage, and the port reproduces them.

## Tests

This repository carries none. A fork is free to bring its own.

## For developers

The port keeps its instruments: headless rendering to PNG, screenshots with a dump of the 3-D scene
that can be re-rendered offline, optional traces, render and culling switches, and many command-line
options. `--help` lists the thirty-odd a player needs, grouped, and `--help-all` adds the instrument
and developer options behind them:

```sh
dotnet run --project src/CYAC.Port.Host -c Release -- --help
```

Comments and option texts cite the original program: `image@0x1234` is a byte offset into the
original executable's unpacked program image, and `asset:<archive>/<NAME>@0x12` an offset inside one
asset of one of its `.lib` archives, so any claim about the original can be checked against the bytes.

Before opening a pull request, please read [CONTRIBUTING.md](CONTRIBUTING.md) — especially the one
hard rule about original game data.

| Path | What |
|:--|:--|
| `src/CYAC.Port.Core` | Simulation: flight and combat kernels, missions, the data-tree loaders. No project references. |
| `src/CYAC.Port.Render` | Software renderer (CPU): pixels into a buffer, nothing else. |
| `src/CYAC.Port.Audio` | Sound synthesis: float samples, no device. |
| `src/CYAC.Port.Host` | The executable (`cyac-fly`): window, input, front end, sound output. |
| `src/CYAC.Port.Transform` | `cyac-transform`: original files → open data tree, and back. |
| `src/CYAC.Formats` | Codecs for the original file formats, used by the transform. |
| `src/CYAC.Port.Preflight` | The start-up check: find the originals, build the data tree, verify it. |
| `external/mode-13hx` | The window / Vulkan presentation layer, embedded (see Credits). |

### Continuous integration

Every push and pull request builds the solution — warnings are errors — and smoke-runs it on Windows,
Linux and macOS ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)): both programs must answer
`--help`, `cyac-fly` must answer `--version`, and `cyac-fly --preflight` on an empty home folder must
refuse by name rather than crash. The runners have no original game files, so that refusal is the one
start-up path CI can check — and it is the one a new player meets first. A `v*` tag builds the
self-contained download — `win-x64` for now; `linux-x64` and `osx-arm64` are prepared in the matrix and
switched on once each has been played from a published zip — and attaches it to a **draft** release for
a human to check and publish ([`.github/workflows/release.yml`](.github/workflows/release.yml)). The tag
must match the `<Version>` in `src/Directory.Build.props`, which is the number both programs report.

## Credits

- [mode-13hx](https://github.com/VasilijP/mode-13hx) (MIT): the frame presentation, window and input layer, embedded as-is with small local changes listed in `external/mode-13hx/VENDOR.md`.
- [Silk.NET](https://github.com/dotnet/Silk.NET) (MIT), [CommandLineParser](https://github.com/commandlineparser/commandline) (MIT),
  [tgalib-core](https://github.com/VasilijP/tgalib-core) (MIT).
- [SDL2](https://www.libsdl.org/) (zlib), through Silk.NET.SDL: the sound device on every platform (macOS uses
  AudioToolbox by default; `--audio-output sdl` selects SDL there too).

## License

[MIT](LICENSE)
