# Roadmap

Goal: the 3D world on screen and looking right, then 30 fps, then 60. Current results: [Status](Status.md).

## Now

| Step | State | Next |
|---|---|---|
| Boot to the world | done; startup freeze fixed | longer boots past the prologue card |
| Device loss | two causes fixed; still lost ~10 min in, in hair simulation | boot with hair sim skipped, then fix it |
| Lighting | world mostly dark; light sources show | PIX capture of the lighting pass |
| Picture goes white after the intro | exposure suspected | exposure trace in a long boot |
| Green credits cards | traced to the texture-release fix | fix in testing |
| Frame rate | ~200-400 ms a frame (2.5-5 fps) | game memory in video memory; faster shaders |

## Frame rate

| Item | State |
|---|---|
| Game memory in video memory | `--gpu-memory-vram`: stable, not yet faster; tuning what moves |
| Shared game heap | blocked by the 64 KiB address map; needs a finer map (translator batch) |
| Translator batch | fast stores, no wave emulation where unneeded, #61, #62: tested, ~7% faster |
| Shader replay | capture one dispatch in a boot, replay and time it offline (`fivestar shader replay`) |
| Hand-written shaders | `--shader-overrides`; first targets from the replay ranking |
| Lower resolution | `--output 1920x1080` works (renders 1280x720) |

## Later

| Item | State |
|---|---|
| Ray tracing | passes skipped, stand-ins fill their outputs; real BVH builds open |
| Hair holes | tile classify leaves empty tiles |
| Audio sync | drifts while frames are slow; follows frame rate |
| AMD GPUs | world geometry draws since #55; not tuned |
| Proton / Steam Deck | not started |

Details per area: [loader.md](https://github.com/ThatRecompDev/five-star-recomp/blob/main/docs/loader.md),
[shader-translation.md](https://github.com/ThatRecompDev/five-star-recomp/blob/main/docs/shader-translation.md),
[gpu-resources.md](https://github.com/ThatRecompDev/five-star-recomp/blob/main/docs/gpu-resources.md).
