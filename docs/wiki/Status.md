# Status

GTA VI does not run yet. Updated 26 November 2026.

## Where it is

- **Boots to the world.** Logo, intro, first-run screens, story load and the opening cutscene (art, credits, dialogue,
  sound) run at 30 fps. The 3D world draws from about present 950.
- **World image:** the city at night is on screen with sane exposure, but only light sources show (lamps, windows, car
  lights); the ground and buildings they should light stay black. Also wrong: blocky road lights, no bounced light
  (needs ray tracing).
- **Device loss:** boots since the 26 November translator batch lose the GPU device before the world; being bisected.
- **Materials:** fixed. Every texture the G-buffer samples now has data (streamed textures are re-read as their pages
  change).
- **Speed:** a world frame takes about 390 ms (about 2.5 fps), down from 1,100 ms.
  Targets: 30 fps, then 60.
- **AMD:** world geometry draws since #55.
- **Memory:** a 10-minute boot grows to about 39 GB private, mostly video memory our textures hold; being reduced.

## Known problems

| Problem | Next step |
|---|---|
| Device lost (0x887a0005) in boots since the translator batch | boot the batch alone, then revert what caused it |
| Surfaces get no direct light | trace the lighting pass of one frame to where it goes black |
| Blocky road lights | trace the shadow-mask pages and cascade shadow draws |
| Heaviest GPU passes: depth pre-pass mesh shader 95 ms, deferred G-buffer 77 ms, our indirect clamp 58 ms | `--mesh-launch` (no per-draw clamp), one clamp per list |
| Texture refresh 84 ms a frame on the submit path | refresh only what the frame reads, off the submit thread |
| Ray tracing | not built; those passes are skipped |

![Progress](progress.svg)

## Components

| Area | Result |
|---|---|
| Shaders | all 25,908 native programs translate and compile to DXIL; our own DXIL backend, dxc as fallback |
| Shader cache | a re-run with nothing changed takes 26 s instead of about 70 minutes |
| Driver pipelines | about 3,850 pipelines warm up in about 35 minutes; ray-tracing ones are skipped |
| Mesh fallback | all 8,765 mesh programs also build as compute plus vertex pulling, for GPUs without mesh shaders |
| Translator checks | 520 shader tests, most on D3D12 WARP; the RDNA decoder matches LLVM on all 44 million instructions |
| Archives | about 68% of archive bytes extract; file names unknown |
| Textures | all 357,115 export exactly with the user's BCPack tables (99.44% without) |
| Models | 99.94% of 4.3 million geometries decode; OBJ export |
| Videos | 515 Bink 2 videos extract |
| Audio | all 1,603,231 sounds export (Opus, XMA2, PCM); XMA2 decoder matches FFmpeg |
| Executable | all 587 imports resolved; the six Xbox libraries load |
| Console runtime | 161 GDK functions shimmed; saves, user and store work offline |
| Graphics (D3D12.X) | every console object wraps a desktop one; console-only methods are logged |
| Setup | `fivestar setup` prepares a dump in one resumable command |

Details per area: [Roadmap](Roadmap.md).
