# Finding the cause of a hitch

How to work out *why* video stutters or audio glitches, from bimp's code down through the engine. Nothing here is
built yet except what is marked "exists": it is the result of an investigation, so the next session can start from it.

## What we know

Measured on a 1440p60 VP9 video (`kKLSuRrZwwE`, quality 4320), 50 s `bimp_probe pace` runs:

- Pacing is normally clean: median frame 8.3 ms, about 60 pictures a second.
- Intermittently (2 of 3 runs on the older code) there are bursts of 100-250 ms stalls about every 220 ms, for 2-12 s,
  not lined up with a segment swap or a download finishing. After the streamed mux and pooled download changes,
  3 runs had none of the bursts, but that proves little because they were intermittent.
- **Main-thread GC pauses were small** (7-20 ms, 30-60 ms in total over 50 s) even during a 359 ms stall, so GC is not
  what those stalls show.
- A run with `bimp_probe_noupload 1` (frames decoded and counted, `Texture.Update` skipped) was clean. One run only.
  This is the lead: the texture upload (`VideoFrameSink` -> `Texture.Update` -> native `AsyncSetTextureData2`), or the
  render thread behind it.
- The A/V drift column showed a steady about -880 ms on this video; it is unexplained and may be a probe artefact.

## What can and can't be seen from bimp

Callable from a library (the whitelist allows `Sandbox.Engine/*`; checked in `Sandbox.Access/Rules`):

- `Stopwatch.GetTimestamp`, `Interlocked`, `lock`, `SemaphoreSlim`, `Channels`, `ConcurrentDictionary`,
  `Environment.CurrentManagedThreadId`, `[ThreadStatic]`.
- `Sandbox.Diagnostics.PerformanceStats`: `FrameTime`, `GpuFrametime` (may read 0 without a render debug flag),
  `BytesAllocated` (may leave out other threads), `Gen0/1/2Collections`, `GcPause`, `LastSecond`, and
  `Timings.{Update,Render,Ui,Video,Audio,GcPause,...}` with a 256-frame `History`. All are per frame, from the main loop.
  `Timings.Video` only covers animated textures, not `VideoPlayer`.
- `Performance.Scope("name")`: a timer that shows up in `Timings.Get(name)` and in Superluminal (the engine ships its
  API DLL). Works from any thread.
- `GpuProfilerStats` (`Enabled` is settable): per-pass GPU time and VRAM use. It times scene passes, so a texture copy
  may not show.
- `VideoPlayer.PlaybackTime`, `Application.FrameCount`, `RealTime`, `Time.Delta`.

Not callable (and so not observable from managed code): `GC.CollectionCount`, `GC.GetTotalPauseDuration` and the like
(only `SuppressFinalize` is allowed; use the `PerformanceStats` deltas), `Thread`, `ThreadPool` counters,
`Process`, `Volatile`, `ConcurrentQueue`. The `VideoPlayer` native counters (video decode seconds, packet and frame
queue depth) are `internal`, and so is its `Sound` handle: there is **no audio position, underrun or buffer data for a
`VideoPlayer`**. Audio glitches can only be inferred (the engine's `PlaybackTime` stalling while the wall clock runs).
`OnTextureData` frames carry no timestamp. The decoder thread's CPU use, the mixer thread, the render thread and the
GPU timeline are invisible; so is the OS (scheduler, disk, antivirus).

How frames travel: the native decoder decodes on its own threads, and `OnTextureData` is called synchronously inside
`VideoPlayer.Present()` on the main thread, so arrival times are quantised to main-thread frames. A main-thread stall
followed by a burst of callbacks means the decoder had the frames queued (it was fine); no callbacks at all means it
was late. `Texture.Update` pins the span and calls `AsyncSetTextureData2` (managed code does no waiting); anything
that blocks is native. The renderer is **Vulkan** (`rendersystemvulkan.dll`: `vkWaitForFences`, `vkQueueSubmit`,
`vkQueuePresentKHR`), so D3D/DXGI-based tools describe presentation only partly. A 4K frame is about 33 MB through
`Texture.Update` (2 GB/s at 60 fps); 1440p is 14.7 MB.

## Plan: one timeline for everything we can see (not built)

**Event ring.** A static array of 65,536 24-byte structs (`long Ticks` from `Stopwatch.GetTimestamp`, `int Id`,
`int Arg`, `long Value`), written lock-free (`Interlocked.Increment` on a head index) from any thread, behind a
`bimp_trace` convar. About 60 ns an event, no allocation. `bimp_trace_dump` copies it, sorts by ticks and writes a merged
CSV to `FileSystem.Data/lab/` from a worker thread. Do not use `List<string>`, string formatting, texture readback or
frame hashing in the traced path (the existing `tex` probe does, which perturbs timing).

**Events to record**

- Main thread: frame start (`RealTime.Delta`, `PerformanceStats` frame/GPU/GC fields), `Present` begin/end, every
  `OnTextureData` call (including the black-skipped and held ones), `texture.Update` begin/end, `UpdatePlayback`
  (`Backend.Present`, `SetAudio`, `CorrectDrift`, captions) spans, segment `Open` / `Play()` / `FirstFrame` / `Swap` /
  `Close`, and each player's `PlaybackTime`.
- Workers: download chunk begin/end (bytes, time to first byte), segment download, mux parse / assemble / write
  begin/end, `ReturnBuffer`, file delete, each with the managed thread id; a gauge of how many mux/download workers run
  at once (the only thread-pool pressure proxy available).

**Automatic hitch detector.** When a frame gap exceeds about 50 ms (or no `OnTextureData` for 2.2x the median
interval), at most once a second: copy the last 500 ms of the ring plus the `PerformanceStats` and `Timings`
counters, and log one line with the probable cause. First match wins: GC pause above 30% of the gap or a gen2 ->
`GC`; a long frame with our spans under 25% of it and high GPU time -> `RENDER/GPU`; `texture.Update` over 30% of the
gap -> `TEX-UPLOAD`; `Present` long with no `OnTextureData` inside -> `DECODER-LATE` (flag `mux/download worker active`);
`Present` long then a burst of callbacks -> `MAIN-THREAD-LATE + DECODER BURST`; `Open/Play/Close/Swap` overlapping ->
`SEGMENT-OPEN/SWAP`; mux or write overlapping with workers >= 1 -> `WORKER-CONTENTION`; large `UpdatePlayback` spans ->
`MAIN-THREAD-WORK`; otherwise `UNKNOWN (engine / render thread / OS)`.

**Also:** wrap the upload and the mux in `Performance.Scope`, turn on `GpuProfilerStats` and log its GPU frame time
and VRAM, and keep `bimp_probe_noupload` and `bimp_probe_file` (plays a file through a sink, with no segmenter or
workers) as controls.

## Hypotheses and what each looks like on the timeline

| Cause | Signature | Counter-evidence |
|---|---|---|
| Decoder starved by mux/download workers | mux or download span overlaps the gap; callbacks stop, then burst; main-thread frames normal; `texture.Update` fast; clean when replayed from a prebuilt file | gaps with no worker active |
| GC | `GcPause` large (or a gen2) in the same frame as the gap; every thread's events stop | small GC during a long stall (already seen) |
| Texture upload blocking | `texture.Update` itself is long, inside the gap; `Present` normal | `Update` fast but still stutters: the stall is downstream |
| Render thread / GPU | frame gap large while all our spans are short; `GpuFrametime` or `Timings.Render` high | our spans add up to the frame time |
| Main-thread work | spans inside `OnUpdate` add up; `Open`/`Play()`/`Close` inside the gap | |
| Disk IO of segment files | write long, or a stall right after a segment is ready / opened; `Play()` and first-frame latency grow | inferred only |
| Segment swap | gap starts at `Swap` / `Open` / `FirstFrame` | |
| Thread-pool starvation | long wait between a download ending and the mux starting, with no CPU work | counters not callable |
| Audio underrun | `PlaybackTime` stalls while the wall clock advances; a dip at `Swap` (volume ramp) | can't be proven from managed code |

## Outside the process (needs an elevated shell)

Installed on this machine: WPR (`C:\Windows\System32\wpr.exe`, with CPU, DotNET, Audio, Video, GPU, DiskIO profiles),
`tracerpt`, `xperf` and WPA (`C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\`), GPUView, PresentMon
(`C:\Program Files\NVIDIA Corporation\FrameViewSDK\bin\PresentMon_x64.exe`), Nsight Systems
(`C:\Program Files\NVIDIA Corporation\Nsight Systems 2025.5.2\target-windows-x64\nsys.exe`), the .NET 6/8/9/10 SDKs,
winget and choco. Not installed: `dotnet-trace`, `dotnet-counters`, PerfView, Superluminal, RenderDoc, Process
Explorer. The editor process is `sbox-dev.exe`; the GPU is an RTX 5080 (plus an AMD iGPU and several virtual display
adapters in the compositor path).

Ranked by how well each names the cause of a 100-250 ms gap:

1. In-process timeline and `Performance.Scope` (above): no admin, decisive for GC, main-thread work and the cost of the
   upload call; blind to the render thread, GPU, OS and audio.
2. **WPR with CPU sampling and context switches** (elevated): names the blocked thread, its wait stack (for example
   `vkWaitForFences` in `rendersystemvulkan.dll`) and the thread that woke it, and shows whether decoder or audio
   threads were starved. Keep captures to 10-20 s; native frames resolve to module+offset (no symbols).
   `wpr -start CPU -start DotNET -start Audio -start Video -start GPU -filemode`, reproduce the burst, then
   `wpr -stop C:\Users\Rexami\bimp_hitch.etl "bimp stutter"`. Analyse with `tracerpt <etl> -of CSV` or `wpaexporter`
   with a profile exporting CPU Usage (Precise) and (Sampled). `wpr -cancel` if one is left running.
3. **Nsight Systems** (`nsys profile --help` for flags; may not attach to a running editor): best view of the Vulkan
   side, with submit and fence waits per queue and GPU copy time.
4. The engine's own `profiler_no_upload` console command: runs `sbox-profiler.exe` (UAC prompt) and writes an ETL.
5. PresentMon: whether presents are late or missing; it may not see the editor's Vulkan swapchain.
6. `dotnet-trace collect -p <sbox-dev pid> --providers Microsoft-Windows-DotNETRuntime:0x1:5` (GC events), or
   `dotnet-counters`: confirms GC more strongly; adds least, since the engine's `GcPause` is already process-wide.
7. The engine's `perf_hub` console (hitch detection and CPU/GPU/GC series; its `audio` tab needs cheats).
   Audio: the WPR Audio profile, plus the engine's audio debug overlay (`AverageMixTimeMs`); there is no underrun counter.

## Suggested order

1. Build the ring, the hooks and the detector; run 60 s on `kKLSuRrZwwE` at 1440p60 a few times and read the verdicts.
2. Play the same video's file with `bimp_probe_file` (no segmenter, no workers): stalls only in the live run point at
   worker contention or file churn; stalls in both with a long `texture.Update` point at the upload; stalls in both
   with a short `Update` and `Present` point at the render thread or GPU.
3. One elevated WPR capture during a burst to name the waiting thread, then Nsight Systems to separate GPU and
   render-thread stalls.

Exists today: `bimp_probe pace|tex|frames|pause|...` (frame gaps, upload times, the `GC:` line, `Present` timings, notes
at segment events), `bimp_probe_noupload`, `bimp_probe_file`, `bimp_probe_texture`, `bimp_probe_dump`, `bimp_probe_garbage`.
Its gaps: notes are frame-quantised strings with no timestamp (the worker ones are appended from other threads without
a lock), `texture.Update` and the mux are not timed, and nothing correlates threads.
