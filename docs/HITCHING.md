# Live stream hitching: findings

> A measurement log from developing BIMP: what caused stutter, latency and audio drop-outs, how it was measured,
> and what was changed. Newest sections are at the end. For how BIMP works now, see [ARCHITECTURE.md](ARCHITECTURE.md).

Status as of 2026-09-24 (updated after the GC investigation, see "Problem 2: root cause"). Test stream: VRCDN HTTP MPEG-TS, `https://stream.vrcdn.live/live/<name>.ts`
(1920x1080, 30 fps H.264 with B-frames off, AAC). The OBS encoder reports zero dropped frames.

## How it was measured

All figures come from `bimp_probe pace <seconds>` inside the editor, in play mode, with play mode restarted after
every code change. `pace` does no GPU readback, so it doesn't disturb what it measures. It records:

- every video frame upload to the screen texture (`VideoFrameSink`), with its interval;
- every render frame time on the main thread, plus a list of frames slower than 30 ms;
- how long each `VideoPlayer.Present()` call took;
- the live pipeline's own events: segment cut, segment write (on a worker thread), `Play()`, first frame, swap and close;
- how many near-black frames were uploaded.

A good run shows about 30 uploads/s, each 33 ms apart. A "hitch" shows up as a cluster of 58–90 ms gaps, where the
native player skips frames to catch up.

## Problem 1: black flashes at segment swaps (fixed)

**Root cause.** The engine's H.264 decoder (Media Foundation) outputs a pure black frame as the first frame of every
new file. bimp plays a live stream as a chain of ~8 s MP4 segments. At each swap, that black frame reached the screen
through the held-frame hand-over, so the screen flashed black every ~8 s.

**Fix.** `VideoFrameSink` now drops a newly attached player's leading black frames, up to 8, until the first real
picture arrives. It checks with a quick 32×32 sampled luma test that bails out early. The previous frame stays on
screen meanwhile.

**Verified.** `near-black frames uploaded: 0` across every run since the fix.

## Problem 2: periodic hitches (root cause found: in the engine, not bimp)

### What the evidence shows

1. **Every hitch starts with a stall on the main thread.** Each cluster of upload gaps begins on a render frame that
   took 40–90 ms (the normal frame time is 8.3 ms). After the stall, the native player drops frames for about 1 s to
   catch up, and that's what shows as a hitch.
2. **bimp's own work isn't what stalls.** All of it is timed and much shorter than the stalls, and none of it
   happens at the same moments:

   | operation | cost | thread |
   |---|---|---|
   | segment cut (snapshot) | 0–0.3 ms | main |
   | segment write (MP4 mux and file write) | 6–15 ms | worker |
   | `Play()` on the next segment | 0.5–1.2 ms | main |
   | `Present()` | 0.46 ms average (10,934 calls) | main |
   | closing the old segment player | 6–9 ms | main |

3. **The stalls are periodic and track playback, not the segment rhythm.** They came roughly every 10 s (2.3, 9.7,
   20.1, 30.5, 41.5, 51.9, 59.2 s), while segments arrive every 8.3 s.
4. **The idle baseline is clean.** A fresh play session with nothing playing had only 4 frames over 30 ms in 60 s.
   Playback is what brings in the stalls.
5. **The "MFT deadlock" warning is related but isn't the cause.** Two of the three drops in the earlier run fell
   inside stalls, and one didn't. The warning comes from the synchronous decoder in engine2.dll when a sample arrives
   late. It's a symptom of stalls, not something bimp can change.

### First diagnosis: garbage collection (disproved)

The pattern first pointed to GC pauses: regular stalls, only while playing, not linked to any timed operation. The
`System.GC` APIs are blocked in the sandbox, so this was tested indirectly. Two allocation sources were cut:

- the 8 MB held-frame array allocated per swap;
- the double copy of every TS access unit.

The next run showed fewer stalls. The changes stay in, because they're cheaper, but the conclusion was wrong. That
"halving" was run-to-run variance. Direct measurement (below) shows GC isn't the cause.

## Problem 2: root cause (measured directly)

### New instrument: GC counters that the sandbox allows

`Sandbox.Diagnostics.PerformanceStats` is public in Sandbox.Engine, which the whitelist allows, even though
`System.GC` isn't. It reports the following for every frame:

- `Gen0Collections`, `Gen1Collections`, `Gen2Collections`;
- `GcPause`: total pause ticks, from `GC.GetTotalPauseDuration()`;
- `BytesAllocated`.

`bimp_probe pace` now reads these every frame and reports:

- each slow frame with any GC within a frame of it, e.g. `8.23s:69ms[gc1 63ms]`;
- a `GC:` line with collection counts, total and maximum pause, and main-thread MB/s allocated.

The probe also stopped formatting a row every frame in `pace` mode, so it adds nothing of its own. The engine runs
with `GCLatencyMode.SustainedLowLatency` (`Sandbox.AppSystem/AppSystem.cs`).

### Experiments

Every run was 90 s of `bimp_probe pace`, in steady state, with play mode freshly restarted. "Slow frames" are render
frames over 30 ms, not counting the probe's first frame. "GC-linked" counts the slow frames with a GC within a frame
of them.

| run | slow frames | GC-linked | max GC pause | upload gaps ≥ 50 ms | `Present()` > 4 ms (max) | MFT drops |
|---|---|---|---|---|---|---|
| idle, nothing playing | 1 (84 ms) | 0 | 8.8 ms | – | – | 0 |
| idle, straight after play start | 9 | 1 (gen1 34 ms) | 33.6 ms | – | – | 0 |
| **B** idle + `bimp_probe_garbage` (+2.4 MB/s, 10–200 KB arrays kept 10 s, 8 MB every 8 s) | 4 | 0 | 9.0 ms | – | – | 0 |
| **A** VRCDN live 1080p | 4 (max 51 ms) | 0 | 9.8 ms | 4 | 2 (11 ms) | 0 |
| **A2** VRCDN live 1080p, repeat (old per-frame probe rows) | 10 (max 61 ms) | 0 | 10.1 ms | 11 | 10 (12 ms) | 0 |
| **C** VRCDN live 1080p, `bimp_probe_noupload 1` (no `Texture.Update`) | 2 (max 75 ms) | 0 | 9.5 ms | 7 | 2 (20 ms) | 1 |
| **E1** a 100 s recording of the same stream as one finished 1080p MP4, one `VideoPlayer` (`bimp_probe_file`) | 6 (max 89 ms) | 0 | 13.3 ms | 7 | 3 (12 ms) | 2 (at open) |
| **E2** the same recording re-encoded to 720p, one `VideoPlayer` | 1 (97 ms) | 0 | 17.6 ms | 5 (one cluster) | **0** | 0 |

Main-thread allocation was 3.8–3.9 MB/s idle and 4.5–4.8 MB/s while playing. bimp adds under 1 MB/s.

### What the table shows

1. **GC isn't the cause.** Across 23 slow frames in the playback runs (A, A2, C, E1, E2), none had a GC anywhere near
   it. In steady state, no GC paused for more than 17.6 ms. Pipeline-like garbage with nothing playing (B)
   produced no GC-linked stall, and neither did a gen0 pause of 17.6 ms (E2). The one GC-linked stall in any run
   was a 34 ms gen1 collection seconds after play mode started, with nothing playing.
2. **bimp's live pipeline isn't the cause.** One finished 1080p file played straight through one `VideoPlayer`
   (E1) involves no segmenter, swaps, muxing, worker writes or held frames. It hitched at least as much as the live
   path (A).
3. **The texture upload isn't the cause.** Skipping `Texture.Update` (C) kept the stalls. It lowered the average
   `Present()` from 0.43 to 0.35 ms, so the upload costs about 0.08 ms per frame.
4. **The native decoder's `Present()` spikes at 1080p and not at 720p.**
   - At 1080p, `VideoPlayer.Present()` averages 0.44 ms but has bursts of 4–20 ms calls. The worst stalls contain
     one. A: 11 ms inside the 47 ms frame. C: 20 ms inside the 75 ms frame. E1: 12 ms inside the 59 ms frame. A2:
     a burst of 4–12 ms calls at 40–41 s alongside six slow frames.
   - At 720p (E2), `Present()` averages 0.23 ms, with no call over 4 ms.
   - `Present()` runs the engine's native H.264 (Media Foundation) CPU path: decode, colour conversion to RGBA for
     `OnTextureData`, and frame timing, all closed source in engine2.dll.
5. **The game thread also stalls with nothing playing.** Idle runs had 1–9 frames of 30–97 ms that no GC explains.
   These come from the engine or editor. A stall of any cause makes the native player drop a frame or two to
   catch up, which shows as 58–86 ms upload gaps.
6. **Most MFT-deadlock drops happen when a decoder opens.** Two came at a direct file's open, and one came at a live
   segment swap (C). They're a separate, rarer source of single-frame gaps; see the README limitation.

### Conclusion

The remaining hitching comes from inside the engine:

- occasional 5–20 ms spikes in the native 1080p H.264 decode/convert inside `VideoPlayer.Present()`, which a 720p
  source doesn't show;
- game-thread stalls that happen even with nothing playing;
- occasional Media Foundation frame drops when a decoder starts.

bimp can't reach any of these from the sandbox. Its pipeline adds no stalls of its own that could be measured: A
has fewer slow frames than E1, a single player with no pipeline. GC is ruled out by direct measurement.

### What changed in bimp in this round

- `bimp_probe pace` reads the engine's GC counters and tags each slow frame with the GC next to it. It no longer
  formats a row every frame, so the probe allocates nothing per frame.
- `SegmentPlayer` times `Present()` with `Stopwatch.GetTimestamp()` and builds its label only for slow calls.
  Before, it allocated a `Stopwatch` and a string for every call, probe or not. Direct (non-segment) video times
  `Present()` too.
- Probe tools, all labelled `[probe]` in `MediaProbe.cs`:
  - `bimp_probe_noupload 1` skips the texture upload;
  - `bimp_probe_garbage <s>` generates pipeline-like garbage;
  - `bimp_probe_file <data path>` plays a `FileSystem.Data` video directly through one player, stopping at the end
    of the play session.

  The E1/E2 test files were `probe_1080.mp4` (stream copy of 100 s of the VRCDN stream) and `probe_720.mp4` (x264
  720p, no B-frames), in the project's data folder.

The planned zero-allocation rework (a sample arena, a one-pass `moov`, pooled ADTS frames) wasn't done. With GC
ruled out, it would add complexity for no measurable gain.

### If you want to go further

- **Use a 720p source for the smoothest playback.** For example, OBS output at 1280×720. E2 shows no `Present()`
  spikes at 720p.
- **Measure outside the editor.** Several idle stalls may come from the editor itself. A standalone game build
  wasn't tested here.
- **Report to Facepunch.** Suggested report: 1080p H.264 through `VideoPlayer` with `OnTextureData` shows 5–20 ms
  `Present()` spikes (E1 reproduces it with a single local file), plus the MFT-deadlock drop at decoder start.

## Low latency live streams (2026-09-24)

`bimp_live_latency low` (the default) cuts H.264 into a file per keyframe interval with no overlap. This section is
what the engine allows, how it was measured, and what low latency costs in smoothness.

### What the engine allows (verified in sbox-public and by experiment)

- **No way to push video into `VideoPlayer`.** It only has `Play( url )` and `Play( fs, path )`
  (`engine/Sandbox.Engine/Systems/Render/Multimedia/VideoPlayer.cs`). The native interface
  (`engine/Definitions/engine/videoplayer.def`) has no stream, buffer or append call.
- **A growing file doesn't work.** The native player opens the file with write sharing denied: appending while it plays
  fails with "being used by another process". A file cut short of what its `moov` declares plays nothing. Frames
  padded to fixed-size slots with filler NAL units (type 12) do play fine in a finished file.
- **The decoder holds back the last 22 frames of every file** (0.73 s at 30 fps) and never drains them. The test
  stream signals `max_num_reorder_frames=0` and `max_dec_frame_buffering=1`, so this isn't DPB reordering, and
  rewriting the SPS wouldn't help.
- **Generated all-skip P frames flush it.** These are CAVLC slices with `mb_skip_run` = every macroblock, their own PPS
  id, and frame_num/POC carried on from the last real frame. Appending 23–26 of them delivered all 30 of 30 real
  frames. `H264.SkipFrame` / `H264.SkipPps` build them, 25 per file.
- **A file can start mid-GOP** if the frames before the wanted one are squeezed into a few ms each. At 1080p it takes
  6 ms per frame (1 tick each broke the decoder: 3–4 MFT drops per open), so a late cut costs up to about 1.5 s of
  lead with an 8 s GOP. Not used; low latency cuts at keyframes.
- **The engine ignores MP4 edit lists** (an empty edit didn't delay playback, it just made each segment 50 ms longer
  than bimp expected) **and composition offsets for delivery**: frames come out at the decoder's own pace.
- **A fresh player's timing.** It delivers a black frame about 40 ms after `Play()` (skipped by `VideoFrameSink`), then
  real frame 0 at about 80 ms, then a steady 33 ms cadence. `SegmentPlayer` used to sync the two players' clocks for the
  audio crossfade, and so swapped about 40 ms late (a 76 ms hold, then a 10 ms one, every swap). Low latency files
  have no audio, so it now just opens the next file one start latency plus 15 ms before the boundary.
- **JPEG decode is cheap**: `Bitmap.CreateFromBytes` (SkiaSharp) takes 3.1 ms at 720p and 6.7 ms at 1080p on a
  worker, and `Texture.Update( Bitmap )` 0.2 / 2.3 ms on the main thread. So Motion JPEG plays frame by frame with no
  `VideoPlayer`.
- **`SoundStream.QueuedSampleCount` / `LatencySamplesCount` count mixer samples** (44100 Hz,
  `AudioEngine.SamplingRate`), not the stream's. `LivePcmAudio` used to divide by the stream's rate, so 8 kHz G.711
  looked 5.5x more queued than it was: audio piled up ~4 s deep and played ~0.3 s late, drifting. It now uses the
  mixer rate, never queues more than 0.3 s, and skips audio that would be heard behind the picture. RTCP sender
  reports line the audio track up with the video.

### Sandbox limits hit along the way

- A custom `HttpContent` needs `System.Net.TransportContext`, which isn't whitelisted. So the RTSP tunnel's long POST
  is a `StreamContent` over a pipe stream.
- `SocketsHttpHandler` buffers request body writes that fit its 4 KB connection buffer until the body ends. A write
  over 8 KB goes straight out, so each RTSP request is sent as one write padded with 9 KB of base64 CRLFs, which RTSP
  servers skip between messages.
- A `catch` block containing an `await` compiles to `ExceptionDispatchInfo`, which isn't allowed. Move the await out
  of the catch.
- **Editing code while a live stream plays can crash the editor.** Hotload can't carry a pending async method whose
  signature changed, and its continuation throws on a thread-pool thread. Stop play mode before editing.

### Measuring latency

`bimp_probe_latency <seconds>` samples every frame:
- **sent→screen**: the RTCP sender report's wall clock for the frame on screen, against the local clock.
- **behind newest frame**: stream time from the frame on screen to the newest frame received.
- **A/V offset**: the stream time of the audio being heard minus that of the picture.

The test server (`rtsp_tunnel_server_lat.py`: ffmpeg `testsrc2` + x264 zerolatency, Digest auth, a long POST or a POST
per request) sends its own sender reports, stamped with when it relays each frame. ffmpeg's own reports take the RTP
time from wall time since its first report, which read 0.5 s too low.

### Results

Each run is 90 s of `bimp_probe pace` (60 s where noted), in play mode, with play restarted per run.

| run | latency | uploads/s | p95 interval | gaps ≥ 50 ms | frames > 30 ms | A/V median | MFT drops |
|---|---|---|---|---|---|---|---|
| 720p `normal`, 1 s keyframes | 9.48 s | 29.6 | 40.8 ms | 46 | 39 | −34 ms | 2 |
| 720p `low`, 1 s keyframes | 1.02 s | 30.1 | 38.9 ms | 47 | 35 | −40 ms | 9 |
| 1080p + AAC `low`, 1 s | 1.04 s | 29.8 | 40.9 ms | 73 | 18 | +2 ms | 3 |
| 720p `low`, 0.5 s keyframes | 0.52 s | 31.0 | 38.9 ms | 22 | 27 | −82 ms | 34 |
| RTSP MJPEG 720p (60 s) | 0.07 s | 29.8 | 34.8 ms | 10 | 8 | −47 ms | – |
| HTTP MJPEG 720p (30 s) | 0.07 s* | 29.9 | 49.7 ms | 26 | 4 | – | – |
| VRCDN RTSP 1080p + AAC `low` (60 s) | 1.06 s* | 29.8 | 42.0 ms | 30 | 13 | −10 ms | 13 |
| VRCDN MPEG-TS 1080p + AAC `low` (60 s) | 1.05 s* | 30.0 | 40.9 ms | 24 | 12 | +7 ms | 11 |

\* Behind the newest frame received. The server's own path isn't included: VRCDN's sender clock read 1.37 s, which
includes network and clock skew.

What the results show:
- **Latency is about one keyframe interval.** Low latency is ~9x lower than normal mode with 1 s keyframes, and
  reaches 0.5 s with 0.5 s keyframes.
- **Gaps and slow frames are the same in both modes.** Both follow the engine's ~8–10 s main-thread stall rhythm
  described above.
- **MFT drops scale with decoder starts**: about one per 10 swaps at 720p, and more at 1080p or with shorter
  keyframe intervals. They're the price of low latency with H.264. Motion JPEG has none, and is the smoothest and
  lowest-latency path.
- **The A/V fix applies to normal mode too.** G.711 used to play ~0.3 s late and drift; normal mode measured −34 ms.

## YouTube: video lagging behind, audio cutting out (2026-09-24)

Reported: on every YouTube video, especially 4K, the video slowed down for a few seconds and then caught up, and a
few seconds later the audio faded out for about a second and came back.

### How it was measured

- `bimp_probe pace` now also prints, for each second, the uploads (frames shown) and how far the player's clock
  moved. The segmenter notes when each segment downloads and is written.
- Audio: the engine's own `video` recorder (the game's mix, not system audio), compared with the source audio
  decoded by ffmpeg. The two are aligned by cross-correlation, then 10 ms windows where the recording is 20 dB below
  the gain-adjusted source are counted as dropouts.
- Local test media: a `lab` extractor (`http://127.0.0.1:8443/lab/NAME` plays `NAME_v.webm` + `NAME_a.webm` from a
  range server) runs the real merged path on known content.

### Causes

1. **Audio came too late in the segment files.** Each entry was a whole YouTube video cluster (one keyframe
   interval: 5–7 s, up to 18 MB at 4K) followed by its audio. The engine reads a file in order and only holds a few
   seconds of packets ahead, so the audio for the start of a cluster arrived too late. It went silent for 1.3–2.9 s,
   once per cluster and at the end of every segment. The same file remuxed by ffmpeg (packet interleaving) played
   with no gaps. Fixed: the audio blocks are woven in among each cluster's video blocks, by time.
   - Clusters must still start with their keyframe. Re-cutting video into 250 ms clusters broke the forward seek
     (it lands on any cluster, and the picture stayed black), and so did an audio block ahead of the keyframe.
2. **The forward seek after loading.** The synced clock starts when the host presses play, so a client that took
   0.5–1.7 s to load hopped forward that much. The engine decodes everything in between while the sound runs on.
   At 4K60 its VP9 decoder is barely faster than real time (ffmpeg's libvpx on one thread: 1.05–1.38x on this
   video), so the picture crawled behind at ~30 frames a second for 4–5 s. Under extra load (the recorder) it never
   caught up and showed nothing. The old file layout had hidden part of this, because the starved audio also held
   the clock back. Fixed: merged media never seeks forward (see "How sync works" in the README).
3. **Segment swaps misjudged the clocks.** Swap timing compared the next player's clock with the current one's as
   if both started at 0, which only live segments do. Merged segments keep absolute timestamps, so the next player
   looked 12–53 s ahead, the start lead collapsed to 0, and every swap after the first opened the next player late.
   Fixed: both clocks are compared in stream time. The first segment's own start latency also seeds the estimate,
   so the first swap isn't late on a slow decoder.
4. **4K60 itself is at the engine decoder's limit.** 19–45 frames a second, content-dependent, even played directly
   through one `VideoPlayer` with no segments. 1440p60 and 1080p60 play at 59–60. This is inside the engine.

### Results

| run | frames/s | gaps >= 50 ms (40 s) | audio dropout windows |
|---|---|---|---|
| 4K60, before | 45.9 (30 in the first 4 s) | 48 in 70 s | 18–20% (1.2–1.3 s gap at the first swap) |
| 4K60, after | 19–43 (engine limit) | – | swaps covered by the audio tail |
| 1080p60, after | 59.9 | 0 | 0.9% (quiet speech, not holes) |
| 1440p60, after | 59.4 | 2 | – |
| Seek +30 s, after | – | – | lands 19 ms (1080p) / 149 ms (4K) off, no native seek |

Live RTSP after the change, as a regression check: `low` 1.04 s latency, 30.6 frames/s, A/V −35 ms; `normal`
9.49 s, clocks in step at swaps (±20 ms).

## Codecs, live swaps and YouTube 4K60 (2026-09-25)

Engine 26.09.22, same machine. The file tests are `bimp_probe_file` with ffmpeg test files in `lab/`. The live tests
are `bimp_probe pace` against a loopback RTSP server fed by ffmpeg (720p30, 1 s keyframes) and against VRCDN.

### The engine's decoders

| file (1080p60 unless noted) | frames/s | gaps >= 50 ms (15 s) | frames shown of a 30 frame file |
|---|---|---|---|
| H.264 in MP4 (Media Foundation) | 56.9 | 8 | 8 (holds back 22) |
| AV1 in MP4 | 60.0 | 0 | 30 |
| AV1 in WebM | 59.9 | 0 | – |
| H.265 in MP4 (`hvc1`) | nothing decoded, no error | – | – |

AV1 in a moov-first MP4 plays, so live AV1 uses the same writer as H.264 with an `av01`/`av1C` sample entry. AV1
holds nothing back, so it needs no skip-frame filler. Its decoders never logged "MFT deadlock" drops in any run.

### Low latency: a held frame at every swap

With the source at 1 s keyframes, both codecs held one frame at nearly every swap (60 a minute).

**Cause:** A new player's clock starts at `Play()`, but its first frame arrives ~50 ms later: the decoder hands frame 0
over only once frame 1 is due. Opened just in time, the next player skipped its first ~50 ms at every swap. Playback
crept up on the live edge (the join slack went from 0.2 s to 0 in ~10 s, logged per swap). From then on every next
file arrived just as it was needed, and every swap held the last frame.

**Tried and dropped:**
- Stretching the first frame (a lead-in), shifting the second frame up to 1 ms after the first, and holding the last
  frame back a few ms per swap. The first trades the skip for a hold; the other two cut the first-frame time to
  17 ms or kept the slack steady.
- In every case the new player's start still cost ~40–50 ms, as either a skip or a hold. There's no way to warm a
  decoder up before its clock starts.

**Fix, for AV1:** each file also carries the first 0.15 s of the next GOP. The current player plays on into it while
the next one comes up in step, so a swap neither skips nor holds. The two clocks are kept in step, as in normal mode.

**H.264 keeps the old just-in-time files.** With the next keyframe inside the file, Media Foundation dropped 3x the
frames (16 "MFT deadlock" drops against 5).

| 720p30 RTSP, `low` | latency | gaps >= 50 ms | MFT drops |
|---|---|---|---|
| H.264 (45 s) | 1.02 s | 12 | 1 |
| AV1, before the overlap (60 s) | 1.04 s | 62–73 (one per swap) | 0 |
| AV1, with the overlap (45 s) | 1.33 s | 7 (all after render stalls) | 0 |

On VRCDN (1080p30 H.264 from OBS, 60 s), `low` gave 1.24 s latency, 20 gaps and 6 MFT drops; `normal` gave 9.6 s,
18 gaps and 1 drop. The gaps came in bursts of 3–4 after 56–90 ms render stalls and slow `Present()` calls when the
next segment's H.264 decoder opened. That's inside the engine. An AV1 source avoids the decoder-open drops.

### YouTube 4K60

AV1 plays 4K60 at 60, but YouTube serves the Android clients' AV1 URLs for only ~88 MB. bimp plans AV1 segments up
to an 80 MB budget per session, filling the last one with as much as still fits, then switches to VP9 1440p60 at the
first VP9 keyframe at or after the last AV1 segment's end.

The last AV1 file runs on past the switch point (1 s, see below), but its timeline ends there. The VP9 player comes up
exactly there: swaps landed 0–0.1 s late, with the first VP9 frame 74–144 ms after opening.

Measured: 4K AV1 to 25.5 s, then 58–60 fps at 1440p. With two ffmpeg encoders running on the same machine, the 4K AV1
part ran at 40–57 fps, so the decode is CPU bound.

A 4K60 video with only HDR AV1 froze outright as VP9 4K, so it now plays VP9 1440p60 from the start (58.3 fps).

## YouTube 4K: a hitch at every segment swap (2026-09-27)

Reported on `2ww1DpGaOr8` at 4K: occasional hitches. They came at the segment swaps (12.5 s, the AV1-to-VP9 switch, then
every ~40 s), each a 100–450 ms hold or skip.

**Cause.** A new player's clock runs from `Play()`, but its first frame at 1440p/4K comes 150–520 ms later. The next
segment was opened `startLatency + leadCorrection` early, which can't satisfy both needs:
- The file ended exactly at the boundary, so a short lead held the last picture until the first frame arrived. For
  example "first frame 227ms after opening … current at 53.215/53.053".
- A long lead (the first swap, seeded by the first segment's slow start) put the next clock ahead, so the picture
  skipped: "next ahead by 191ms".

**Fix (merged WebM only).**
- Each segment file carries 1 s of the next segment's video (`VideoOverlapSeconds`). Only the start of the cluster the
  cut falls in is downloaded, and it's counted against the AV1 budget.
- The next player opens just early enough for its clock to be in step (`clockLead`, learned from each swap, ~20–50 ms).
- The swap then waits until the next player has delivered 4 frames in a row less than 45 ms apart. Swapping on its
  first frame showed its catch-up as an 80–120 ms gap.
- HLS VOD (MP4 segments) keeps the old timing. With the settle wait, Media Foundation dropped an H.264 sample ("MFT
  deadlock") in 3 of 3 runs, and the picture broke up until the next keyframe; the old timing had 0 in 3.

| 130 s, `2ww1DpGaOr8` | uploads/s | p95 interval | gaps >= 50 ms at swaps |
|---|---|---|---|
| 4K AV1 then VP9 1440p, before | 59.4 | 22.5 ms | 3 of 4 swaps, 139–143 ms |
| VP9 1440p throughout, after | 59.8 | 17.4 ms | 0 of 3 (2 gaps of 53–58 ms at the first swap) |
| 4K AV1 then VP9 1440p, after | 58.8 | 25.1 ms | 0 of 4 (three 50–51 ms gaps just after the 53 s swap) |
| 1080p60, 70 s: before / after | 58.3 / 60.0 | 24.0 / 17.2 ms | 4 gaps in all (max 93 ms) / 0 (max 25.6 ms) |

In the last 4K run OneDrive Sync was using 35% of the CPU. The 50–84 ms mid-segment gaps from 53 to 82 s came from
that load, not from swaps.
