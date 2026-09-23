# Vendored dependency — mode-13hx (feature/vulkan, plus an OpenGL presenter)

Upstream: https://github.com/VasilijP/mode-13hx/tree/feature/vulkan
Vendored at commit `84d209f6e8fb94f381cabc669c934f882640e9ec`, excluding `.git`, `bin`, `obj`.

Role in CYAC: the port's **executable host** — window, presentation (OpenGL by default, Vulkan on
request), keyboard + mouse. It is an Exe project used as a subproject of `cyac.net.sln`;
`CYAC.Port.Host` references `external/mode-13hx/src/mode13hx.csproj` and supplies its own `Main`.

Rules: treat it as a dependency. No large-scale refactoring. Extensions this port needs (more
key/input mapping, small adjustments) are allowed here and are candidates to **backport upstream**;
keep every local change listed below so the diff against upstream stays reviewable (`diff -r`
against a fresh checkout of the commit above).

## The OpenGL presenter (local addition, `--gfx`)

Upstream's presenter is Vulkan, chosen for its compute shaders (the frame-compression path). A game
only needs a presenter, and Vulkan asks things of a player's machine that OpenGL does not: a loader
(MoltenVK on macOS, loaded by hand), and drivers in order — a laptop GTX 1070 died silently until its
Intel iGPU driver was updated too. So this copy carries a second presenter and selects one with
`--gfx`: `opengl` (the default) is a 3.3 core, forward-compatible context, which every desktop driver
of the last decade provides, Apple's frozen 4.1 included; `vulkan` is upstream's presenter, untouched,
and the only one `--frame-compression` works with. Upstream `main` is OpenGL too but on OpenTK, with
a frame buffer that needs a live GL context in its constructor; CYAC builds frame buffers with no
window (`--headless`, `--preflight --shot`, `--render-scene`), so it was not taken; its quad, its
GLSL 330 shaders and its texture set-up were.

| File | Change | Why |
|:--|:--|:--|
| `src/Presentation/GlRenderer.cs` | **Added.** One RGBA8 texture (frame-height wide, frame-width tall: the frame is column-major), one full-screen quad with upstream `main`'s vertices, `glTexSubImage2D` per frame, GLSL 330 shaders read from `resources/gl.vert` and `gl.frag`. Logs the renderer and version on start. | The default presenter. Backportable as a second presenter. |
| `src/resources/gl.vert`, `src/resources/gl.frag` | **Added**, upstream `main`'s GLSL 330 shaders under their own names (`shader.vert`/`.frag` stay the Vulkan GLSL 450 sources of the `.spv` files). | |
| `src/Configuration/CommonOptions.cs` | Added `--gfx opengl\|vulkan` (default `opengl`) and `UseVulkan`. | The switch. |
| `src/Presentation/EngineWindow.cs` | Creates one presenter or the other in `OnLoad`; `OnRenderFrame` has a short OpenGL branch (`Use()` → `Present(pixels)` → `ReleaseFrame()`) ahead of the unchanged Vulkan path; both are disposed. `--frame-compression` without `--gfx vulkan` prints a note and presents uncompressed. | |
| `src/Program.cs` | `WindowOptions.Default` or `DefaultVulkan` by `--gfx`. | |
| `src/mode13hx.csproj` | `Silk.NET.OpenGL` 2.22.0 added beside the Vulkan packages; `gl.vert`/`gl.frag` copied to the output. | |

## Local changes vs upstream 84d209f

| File | Change | Why |
|:--|:--|:--|
| `src/Configuration/CommonOptions.cs`, `src/Presentation/EngineWindow.cs` | Added `CommonOptions.CloseOnEscape` (a plain property, default `true`, no CLI option) and guarded the `Key.Escape → window.Close()` line on it. | `EngineWindow` closed the window on ESC before the control table was updated, so an application could never bind ESC. CYAC's in-flight menu is the original game's ESC menu, so the host sets it `false` and quits through its own Exit action (`SetExitAction(window.Close)`). Upstream behaviour is unchanged by default. Backportable as-is. |
| `src/Model/Control.cs` | Locked the static control registry: one `private static readonly object ControlsGate`, taken by `Control.Create` around its `TryGetValue`-then-`Add` and by `Control.Reset` around its enumeration. | `Create` was an unsynchronised read-then-insert over a **static** `Dictionary`, and `CommonOptions`' field initialisers call it eight times, so two threads building options at once raced and one threw `"An item with the same key has already been added. Key: VK_FORWARD"` when two test classes built options at the same time. `Reset` enumerates the same dictionary and would throw if an insert landed mid-walk. Upstream behaviour is unchanged for a single-threaded caller: the lock is uncontended and `Create` runs a handful of times per process. Backportable as-is. |
| `src/Model/Control.cs` | Added `ControlEnum.APP_BASE = 1000` (one enum member + a comment; no code changes). | An application that needs more actions than the built-in five declares them as `APP_BASE + n`. `Control.Create`, `CommonOptions.KbControls` and `Control.Reset` already work with any `ControlEnum` value, so this is a documented anchor rather than new behaviour. CYAC binds 17 flight/cockpit controls off it (`src/CYAC.Port.Host/Configuration/FlyOptions.cs`). Backportable as-is. |

## Examined and deliberately not changed — the mouse

Two mouse questions looked as though they needed a local change here. Neither did; below is why,
plus one upstream defect worth a backport.

| Question | Finding |
|:--|:--|
| Read the mouse's ABSOLUTE window position? | **No — there is none to read.** `EngineWindow.OnLoad` puts every mouse in `CursorMode.Raw`, which Silk.NET defines as "cursor is invisible, and is restricted to the center of the screen; mouse motion is not scaled". A locked cursor has no meaningful window position, so DELTAS are what the mode gives and what `OnUpdateFrame` already accumulates into `MOUSE_DELTA_X/Y`. CYAC integrates them host-side (`FrontEndMouse`), dividing by the front end's integer scale and keeping a fractional position so a slow mouse still moves at scale 10. |
| Hide the OS cursor over the window in menu mode? | **Already hidden**, by the same `CursorMode.Raw` — in every mode, menu and flight alike, and restored by the OS when the window closes. Nothing to add. |
| Upstream defect (reported, not patched) | `CommonOptions.cs`:68 files the left mouse button under the dictionary key `ControlEnum.MOUSE_BUTTON_LEFT` but binds it to `Control.Create(ControlEnum.VK_USE)` — the same `Control` INSTANCE its own default `Key.Space` binding uses (`CommonOptions.cs`:60). So upstream, a left click and the Space key are indistinguishable, and `Control.Create(MOUSE_BUTTON_RIGHT)` (the neighbouring line, correct) has no left-hand twin. The one-token fix is `{ ControlEnum.MOUSE_BUTTON_LEFT, Control.Create(ControlEnum.MOUSE_BUTTON_LEFT) }`, but it would change behaviour for any upstream application that relies on the alias, so CYAC does not make it: `FlyOptions` clears `KbControls` and binds no key to `VK_USE`, which makes `VK_USE` the left mouse button and nothing else in this application. |

Everything else CYAC needs was possible without touching the vendored tree: several keys can share
one control (`Control.Create` returns one instance per `ControlEnum`, and `EngineWindow` OR-s each
bound key into it), and the headless frame dump drives `FrameBuffer` / `Canvas` through their public
API with no window at all.
