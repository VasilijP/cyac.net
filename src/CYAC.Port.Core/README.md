# CYAC.Port.Core — the native port's engine core

The conventions every contribution to this project follows.

## What this is / is not

- A clean, idiomatic C# 14 / .NET 10 engine core for Chuck Yeager's Air Combat — the game as a
  *program*, not as an emulated image. Standard C#: records, enums, spans, `readonly struct`s,
  no DGROUP offsets in game logic, no instruction accounting, no interrupt model.
- It loads the **transformed open-format data tree** produced by `cyac-transform`; original bytes
  never appear in this source tree, and `CYAC.Formats` is a transform-time dependency only. It
  drives the **dual-kernel** simulation. Precision-sensitive transforms are float-native by law.

## Dependency rule (non-negotiable)

`CYAC.Port.*` references clean libraries only (`CYAC.Formats`).

## The schema is the ground truth of the data model

`Schema/state_schema.json` is generated, never hand-edited. It carries the named globals and the
struct layouts recovered from the original program. Port types that *represent* an
original struct or global keep the link **as metadata, not as semantics**:

```csharp
[OriginalStruct("s_aircraft_master")]
public sealed class Aircraft { [OriginalField("+0xD6", "ctrl_aoa_block")] public ControlAxis Aoa { get; } ... }
```

so a comparator can map port state ↔ original memory, while the port's own model stays clean. Naming: port names are *domain* names (`Aircraft`, `WeaponClass`),
the original `g_*`/`s_*` identifiers live only inside the attributes.

## Field classes — declared, not implied

Every simulation quantity is tagged as **INT-only** (reproducible spine: flags, timers, AI-VM
state, RNG, mission logic), **DUAL** (kinematics; both kernels maintain it), or **FLOAT-only**
(presentation). Decision-side code receives an `IntegerView` only — the compiler enforces the
partition. Model types carry the intended class in their doc comment.

## Integer semantics are deliberate

Where a value is INT-only or the integer half of a DUAL pair, keep the original width and wrap
semantics on purpose (`short`/`ushort`, BAM angles wrap mod 65536) — a "fixed" overflow is a
gameplay divergence and belongs in the quirk registry, never in a silent int promotion.

## Verification expectations for every contribution

- `dotnet build` warning-free — warnings are errors here.
- Tests are *behavioral*: they assert what the original does, and cite the original's bytes for it.
- When sources disagree, the original's bytes win.

## Layout

```
Schema/     state_schema.json (generated) + its loader/model (SchemaModel)
Primitives/ Bam16, Q-format fixed-point helpers, Lfsr16 (Galois RNG), ...
Model/      domain model per subsystem (Aircraft, WorldObject, WeaponClass, Mission, ...)
Sim/        determinism kernel: TickClock (STEP_TICKS=5, Q8 accumulator), RandomStreams
            (Sim.* on Lfsr16, Fx/Audio on xoshiro256**, Split/Compat), InputLog (+ CYEV v7 import)
Sim/Flight/ integer flight kernel: the FME envelope queries + the AoA probes; the per-frame
            stages ThrottleFuelStage (S0→S1), HeadingAccumulatorStage (S1→S2, S6→S7) and
            ControlIntegrationStage (S2→S3, both arms) over ControlAxisKernel's generic leaves, with
            IVelocityDynamics / IKernelWorld / IKernelRandom as the seams; VelocityDynamics — the
            aoa_physics_tick subtree that fills IVelocityDynamics (angular decay, the four
            accumulators, the ±cap integrator); ApplyVelocityStage (S3→S4, the 12-phase attitude /
            position integrator) over EulerRateTransform + BodyVelocityProjection, and
            DamageCheckStage (S4→S5, S5→S6 — the ground-contact outcome and the taildragger ground
            attitude); FlightKernel — the per-frame DRIVER (aircraft_per_frame_update @0x2A69E) that
            runs all eight stages over a FlightKernelState, with DtPolicy (TickClock = the port's law,
            Recorded = replay of a recorded frame's own dt) and the scene-start dt=1 rule, plus
            FlightKernelEra — the era descriptor (step ticks, dt policy, stream policy, field-set
            hash) InputLog stamps; and Trace/ — the reader for a `cyac-flight-trace` file
Sim/Combat/ integer combat kernel: the DGROUP combat register file + pool arena + spawn table +
            the 55-byte engagement record and its list; the projectile row
            CombatObjectTick/CombatSpawnDriver; Grid/ the world-grid quadtree; Geometry/ the
            manoeuvring engine; EngagementNodePass + the acquisition state machine; Vm/ the AI
            bytecode interpreter; Lifecycle/ the admitter, the .S module dispatch and the
            destruction pool; Player/ the fire chain, the damage roulette, the sustain tick,
            lock-on and countermeasures; and CombatKernel — the per-frame DRIVER that runs the
            gameplay body of mission_state_machine in ladder order over all of them, with
            ICombatFrameChannels as the four named external channels and CombatKernelEra as the
            era descriptor
Data/       DataLocator/DataTree — the runtime reads the transformed tree only (plan L2); and
            DgroupConstants/DgroupDocumentCodec — the game's CONSTANT DGROUP address
            space rebuilt from those documents, which is what the combat and flight kernels read
            instead of data/exe/image.l1.bin. Reading a DGROUP byte no document publishes THROWS and
            names the offset, so an un-transformed table is a loud first-frame failure, never a
            silent zero
```
