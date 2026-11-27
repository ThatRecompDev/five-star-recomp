# #35: cheaper wave64 mesh shaders on 32-lane GPUs

Notes on the three ideas from issue #35, grounded in [shader-translation.md](shader-translation.md).

## The problem

The console runs every mesh shader in 64-lane waves and the game's code depends on that: ballots read as numbers,
`readfirstlane` of per-lane values, GS_ALLOC_REQ counts. On a 32-lane GPU (NVIDIA) each console wave becomes two
hardware waves that cannot see each other's lanes. The exact fix, the **superstep form**, cuts the program at every
wave operation, deposits values in groupshared memory and passes a **group barrier** per cut. Correct, but
`MSNC_VS_LinearDepthSkin` has about 85 such stops: ~75 µs per round, and a draw can take seconds. 2,548 of 8,765 mesh
programs have floor = whole wave and so take this path on NVIDIA. AMD is already solved by #29 (RDNA runs mesh
shaders natively at 64).

## Idea 1: XCL-style compute pre-pass at `[WaveSize(64)]`

**Why it works.** Mesh shaders cannot choose their wave size, but compute shaders can (`[WaveSize(64)]`, SM 6.6+).
NVIDIA Ampere and later support wave64 *compute* even though their mesh waves are 32. So: run the real program as a
compute shader pinned to 64 lanes (console-exact, zero barriers), write vertices and primitives to a buffer, then a
trivial mesh shader copies that buffer into `SetMeshOutputCounts` and the outputs.

**What already exists.** `MeshComputeFallback` (`.msfb_cs.hlsl` / `.msfb_vs.hlsl`) rewrites a mesh program as
compute plus a vertex-pulling vertex shader, and there is already a `.msfb_cs.w64.hlsl` variant pinned to the console
wave. That is about 80 % of this idea. The missing piece is a mesh-shader emitter instead of the VS, so the draw stays a
mesh dispatch (ExecuteIndirect arguments, primitive culling and per-primitive attributes stay intact).

**Where to start.**

1. `VariantSelector` (flowchart in shader-translation.md): add a branch "adapter supports `WaveSize(64)` compute but
   measured mesh wave < floor → pre-pass".
2. A new emitter beside `MeshComputeFallback` that generates `.msfb_ms.hlsl`.
3. Runtime: the loader needs a compute dispatch before the mesh dispatch, sharing an intermediate buffer.
   [d3d12x-pipelines.md](d3d12x-pipelines.md) shows how pipelines are keyed; the breadcrumb and ExecuteIndirect code
   already handles multi-pass loader-owned work (`loader:<what>` pipelines).

**Cost and risk.** One extra buffer and barrier per draw; indirect draws need the compute pass to read the same
argument buffer. Still vastly cheaper than 85 group barriers.

## Idea 2: prove more operations wave-size independent

The compiler-analysis route: shrink the 2,548 whole-wave programs. The top reasons the analysis gives up:

| Reason | Programs |
|---|---:|
| `readfirstlane` of a per-lane value in an output or GS_ALLOC_REQ | 1,338 |
| integer VCC branch | 588 |
| VCC branch whose mask lost its relation to EXEC | 304 |
| execz path that cannot be skipped | 246 |

Each row is a candidate rule in `Translation/Waves/WindowAnalysis` (floor computation) or `SkipRegion` (deciding
that a path may be skipped or taken alone). Example attack on the biggest row: a `readfirstlane` is fine when the
value is provably uniform across the console wave even if per-lane in form, e.g. loaded from a uniform address or
derived from SGPRs only. Extending `WindowAnalysis.EntryFacts` to track "uniform on all active lanes" more precisely
would move programs from floor 64 to floor ≤ 32 and off the superstep path entirely.

This is the most contained work: pure translator, testable with `fivestar shader translate` and the floor histogram
in shader-translation.md, no runtime changes.

### First step (branch `issue-35-uniform-first-lane-reads`)

Three rules in `Translation/Waves`, each sound by the prelude's semantics and covered by a floor test in
`WaveEmulationTests` that the old analysis fails:

1. **Compound assignments keep facts** (`WindowStatement.Parts`, `StatementTransfer.Apply`). `v_mad_u64_u32` and the
   64-bit shifts (the address math of `base + readfirstlane(index) * stride`) emit
   `if (active) { lo = (uint)_p; hi = (uint)(_p >> 32); } }`, which parsed as an opaque statement and dropped every
   uniformity fact, so the `readfirstlane` that followed was "of a per-lane value". The halves are now applied as the
   guarded assignments they are.
2. **Bits equal on all lanes are the console's under any EXEC** (`ValueRules.FirstLane`). `readfirstlane(x)` returns
   some lane's `x` even when the window has no active lane or its active lanes are not the console's, so the
   `AllUniform` bits (an SGPR copy, the group's bits of the mesh thread id) are exact regardless. Before, the result
   was unknown unless EXEC was exact and non-empty.
3. **Non-effect opaque statements propagate uniformity** (`StatementTransfer.Opaque`). `GetDimensions`, the
   class-switch loads and `asuint(double, lo, hi)` now give their results the uniformity of their reads (like any
   assignment), an `asuint` split marks its out-parameters assigned (a soundness fix: they were left at their declared
   "exact and uniform"), and effects (atomics' old values) stay per-lane.

What this cannot prove, by design: a guarded uniform-address load leaves inactive lanes at 0 (uniform on the active
lanes, not on all), so its `readfirstlane` under a shrunk EXEC is only "held" and is lost when EXEC is restored; and
TransformD's data-dependent counts still need `FirstLaneExchange`. The corpus histogram above has to be re-run with
`fivestar shader translate` by someone holding the shader dump.

## Idea 3: make `--fast-waves` safe

`--fast-waves` keeps the native form and approximates: each hardware wave's `readfirstlane` is taken as the console's
and "any lane" branches are decided per hardware wave. 1,399 of 2,540 programs run this way. It is parked because of
`0194d7ba` (`MSNC_VS_CascadeShadows_drawskinnedTexture_3c28b1bd`): a branch at 0x9a8 on `vw_any(m_s_vcc)` is decided
per hardware wave and the two sides have **different numbers of mesh barriers** (one or two yields), so the two halves
of a console wave disagree on the barrier count and the group hangs.

The doc's own diagnosis: the analysis treats that branch as uniform because its facts come from the approximated
`readfirstlane` values, so the dangerous branch is never checked. Refusing every barrier under a branch kills fast
waves for everything (0 of 127 sampled).

**Fix: two fact sets.** Run `WindowAnalysis` once with exact facts and once with fast facts. For any branch that leads
to paths with unequal barrier counts, require the *exact* facts to prove it uniform; otherwise fall back to the
superstep form for that program only. That keeps most of the 1,399 fast and refuses only the genuinely
divergent-barrier ones.

## Which to pick

- **Fastest payoff for NVIDIA users:** Idea 1. It attacks the worst programs (LinearDepthSkin,
  PropFoliageSkinDeferred, TerrainNormalTint) directly and reuses the fallback machinery.
- **Lowest risk, pure translator work:** Idea 2.
- **Best bang for compiler-analysis work:** Idea 3. Bounded, well diagnosed, one known failing program to validate
  against.
