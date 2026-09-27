# How BIMP works

The technical side of BIMP: how playback stays in sync, how links are resolved without any external service, how live
streams are ingested, and the engine behaviour it works around. For using BIMP, see the [README](../README.md). For
the measurements behind the smoothness work, see [HITCHING.md](HITCHING.md).

- [Sync](#sync)
- [Resolver](#resolver)
- [Merged YouTube playback](#merged-youtube-playback)
- [Live streams](#live-streams)
- [Engine workarounds](#engine-workarounds)
- [UI](#ui)

## Sync

The host stores `PlayUrl`, `StartTime` (the host clock time at which media time 0 would have played),
`Paused`/`PausedAt` and the queue as `[Sync(SyncFlags.FromHost)]` properties on `MediaPlayer`. Clients' `Time.NowDouble`
already follows the host clock, so each client plays locally and works out where it *should* be:

```
expected = Paused ? PausedAt : Time.NowDouble - StartTime
```

- **The engine's video player can't seek forward in a stream.** `Seek()` jumps its clock, but it keeps reading and
  decoding sequentially from where it was, and throws away every frame as late until it catches up. A big seek
  fast-forwards for minutes. (The engine's own `VideoTextureLoader` notes the late-frame behaviour; it was measured
  frame by frame with `bimp_probe`.) Even a short forward hop costs this: at 4K the decoder is barely faster than real
  time. So merged (YouTube) media **never seeks forward natively**:
  - To seek, join, or correct drift, a client writes new segment files, with absolute timestamps, starting at the
    keyframe where the synced time will be once the first segment is downloaded (the download time is measured as it
    goes). It uses the keyframe just before that point if it's at most 1 s behind. Otherwise it uses the next one and
    waits there until the synced clock reaches it (up to one keyframe interval, 5–7 s on YouTube).
  - At the start of a video it plays from the beginning, a load time behind (typically 0.5–1.2 s), and keeps that
    small lag (up to the 1.5 s drift tolerance) rather than hopping.
  - Measured: a seek lands 19 ms (1080p) / 149 ms (4K) from the synced time.
- Direct files use the native seek, which is fine for backward seeks and short hops.
- Clients correct drift above 1.5 s, or straight away for an explicit seek (buttons, progress bar), which carries its
  own `SeekId`.
- **Pausing never seeks.** The host pauses the timeline at its own picture's actual position. A freshly opened player
  runs until it has landed before it pauses, so a seek while paused shows the new frame.
- If playback should be advancing but hasn't moved for 5 s, the client recreates its player at the expected position,
  up to 3 times per item (a stuck decoder or a dropped connection).
- End of media is detected on the host from the known duration, or from its own playback for files whose duration is
  only known after loading (clients report it back). The host then advances the queue.

Resolution and audio language are local choices. Each client adds `&h=` / `&lang=` to the synced play token
(`MediaStreamOptions`), and changing either reloads only that client's player at the current synced time.

## Resolver

The resolver is plain C# inside the library, within the s&box code whitelist. It needs no external service, no
yt-dlp and no ffmpeg. There are two halves:

- **`BimpResolverSystem`** is a `GameObjectSystem`, so every scene has exactly one. It does nothing until a media
  player asks, and only runs on the host. It works out a link's title, duration, qualities and dubs, caches them for
  30 minutes (so ten people queueing the same link resolve it once), and syncs a **play token** such as
  `bimp:yt/aqz-KE-bpKQ?mode=av&maxh=720` through the player's queue item.
- **Each client resolves its own stream URLs** from the token (`StreamSessions` in `PlayToken.cs`). YouTube stream
  URLs are locked to the IP address that requested them, so the host can't hand its URLs to everyone, and the sandbox
  has no way to proxy bytes to other players. Sessions are cached for 10 minutes per token.

Extractors live in `Code/Resolver/Extractors/` and are registered in `Extractors.cs`:

- **YouTube** (`YouTubeExtractor`, `InnerTubeClients`): the InnerTube player API is called with clients whose formats
  come with plain URLs, so no signature deciphering is needed. The anonymous visitor ID from an embed page avoids the
  "confirm you're not a bot" wall. Each client's URLs are checked with a 1-byte range request, and a client whose
  downloads are refused (403) is skipped. The order is `bimp_yt_clients`. `FormatSelector` picks the formats.
- **Other sites** go through `SiteExtractor`. A site's progressive file is played directly and seeked natively. Failing
  that, its HLS playlist is used: live channels are followed at the live edge, and VODs are remuxed into local MP4
  segments from a start time, seeking by reopening like merged YouTube media. Twitch uses the web player's public GQL
  client ID and playback tokens. Kick, X (the embed API), archive.org and Bandcamp use public JSON or page data.
- **`HlsExtractor`** probes a pasted `.m3u8` to decide between live and VOD.

Sites change, and when one breaks the fix is in `Code/Resolver/Extractors/`. YouTube breaks most often; try
`bimp_yt_clients` first, and `bimp_yt_formats <id>` shows what each client gets.

## Merged YouTube playback

HD video comes as separate VP9/AV1 video and Opus audio files, and the engine plays one file. `WebmSegmenter`
range-downloads a few seconds at a time (`RemoteFile`) and writes standalone WebM **segment files** to
`FileSystem.Data` (`bimp/cache/`):

- Video clusters keep their timecodes and blocks byte for byte. AV1 from a fragmented MP4 is turned into clusters
  first (`Fmp4Reader`, `Fmp4.ClustersFromMp4`). The Cues go at the end, where the engine looks for them. Nothing is
  re-encoded.
- The audio blocks for the same time are woven in among the video blocks (`WebmMux.Interleave`), never ahead of a
  cluster's keyframe. The engine reads a file in order and holds only a few seconds of packets ahead: audio written
  after a whole keyframe interval of video arrived too late and went silent for 1–3 s at a time.
- The first segment is 8 s, so playback starts quickly; after that, segments are 40 s.
- Each segment's audio runs 3 s past its end, and its video 1 s (`VideoOverlapSeconds`). A new player's first frame
  takes 150–520 ms at 1440p/4K, so the current one keeps showing real frames until the next is up.

`SegmentPlayer` plays segment N and starts N+1 in a second, hidden, silent player, then swaps them:

- For merged WebM, the next player is opened only just early enough for its clock to be in step with the current one
  (`clockLead`, learned from each swap). The swap waits until it has delivered 4 frames in a row on time, while the
  current one plays on into its overlap. The audio crossfades over 150 ms.
- Files are deleted as they're played, and the cache folder is wiped on start.
- `VideoFrameSink`: every player's frames go to one texture, uploaded by bimp from `OnTextureData`. The engine's own
  texture path presented frames unevenly.

**YouTube 4K60.** The engine's VP9 decoder can't play 4K60 smoothly (19–45 fps), but its AV1 decoder plays it at a
steady 60. Only YouTube's Android clients get AV1, and YouTube stops serving those URLs after ~88 MB. So bimp plays
AV1 up to an 80 MB budget per session, then switches to VP9 1440p60 at the first VP9 keyframe after the last AV1
segment (`plan.SwitchVideo`). A 403 on AV1 switches early. A 4K60 video with only HDR AV1 plays VP9 1440p60 from the
start.

## Live streams

MPEG-TS over HTTP, HLS, RTSP cameras and Motion JPEG cameras play in-scene, with no external service and no
re-encoding. **Every client connects to the source itself.** The host only syncs the link.

The engine decodes whatever codec the source sends; it can't transcode, and neither can bimp (there's no video encoder
in the sandbox). AV1 plays best: its decoder shows every frame of a file (Media Foundation's H.264 decoder holds back
the last 22), never drops frames when it starts, and presents 1080p60 evenly. H.265 can't be played at all.

### Pipeline

- **Ingest** (`Code/Resolver/Live/`) turns each source into access units, AAC frames, JPEG frames or PCM:
  - `TsDemuxer`: MPEG-TS over HTTP and each HLS segment.
  - `RtspTunnel`: RTSP over HTTP tunnelling. RTP with H.264 (single NAL, STAP-A, FU-A), AV1 (the AOM payload format),
    Motion JPEG (RFC 2435, headers rebuilt), AAC (`mpeg4-generic`) and G.711. It reads RTCP sender reports for the
    latency readout and A/V alignment.
  - `HlsReader`: HLS playlists with MPEG-TS or fMP4 (`#EXT-X-MAP`) segments, separate audio playlists, packed audio
    (ADTS behind an ID3 timestamp), byte ranges and discontinuities. It picks the tallest variant up to
    `bimp_max_height`, preferring AV1 and skipping H.265.
- **Video** can only be decoded by the engine's `VideoPlayer`, and only from finished MP4 files, so it's cut into
  segments at keyframes (`LiveSegmenter`, written by `Mp4Writer` as `avc1` or `av01`) and played back to back by
  `SegmentPlayer`:
  - `low`, H.264: a file per keyframe interval, written the moment the next keyframe arrives. Each file ends in 25
    generated all-skip P frames that push the real frames out of Media Foundation's decoder. The next file opens just
    in time for its first frame to land on the boundary.
  - `low`, AV1: a file per keyframe interval, carrying 0.15 s of the next one; the two players run in step, so a swap
    neither holds nor skips a frame.
  - `normal`: files of at least `bimp_live_segment` seconds, each carrying 1.5 s of the next one (0.15 s for AV1),
    played a segment behind the newest.
- **Audio** in low latency mode is decoded in C# (`AacDecoder` for AAC-LC, checked against ffmpeg at 50–62 dB SNR, and
  `G711`), played through a `SoundStream` held back to match the picture, and lined up by the sender reports. Measured
  A/V offset: within ±50 ms (median).
- **Motion JPEG** needs no segments: each frame is decoded on a worker thread (3 ms at 720p, 7 ms at 1080p) and shown at
  its own timestamp, 60 ms behind the earliest arrival.

### Latency

Measured from the sender's output to the screen, using RTCP sender reports, with the source on the same machine.

| Source | Mode | Behind the live edge |
|---|---|---|
| RTSP / MPEG-TS, H.264, keyframe every 1 s | `low` (default) | **~1.0 s** (1.02 s median at 720p, 1.04 s at 1080p) |
| RTSP / MPEG-TS, H.264, keyframe every 0.5 s | `low` | **~0.5 s** |
| RTSP / MPEG-TS, H.264, keyframe every N s | `low` | about N s + 0.05 s |
| RTSP / MPEG-TS, H.264 | `normal` | ~2 segments (9.5 s with 1 s keyframes and 4 s segments) |
| RTSP, AV1, keyframe every 1 s | `low` | **~1.3 s** (one keyframe interval plus the 0.15 s overlap) |
| RTSP, AV1 | `normal` | 8.1 s |
| RTSP / HTTP Motion JPEG | frame by frame | **~0.07 s** |
| HLS | always `normal` | a few segments |

### Smoothness

90 s runs of `bimp_probe pace` in the editor, 1 s keyframe interval, unless noted. Details in [HITCHING.md](HITCHING.md).

| | Latency | Uploads/s | Gaps ≥ 50 ms | Frames > 30 ms | A/V | MFT drops |
|---|---|---|---|---|---|---|
| RTSP 720p, `normal` | 9.48 s | 29.6 | 46 | 39 | −34 ms | 2 |
| RTSP 720p, `low` | 1.02 s | 30.1 | 47 | 35 | −40 ms | 9 |
| RTSP 1080p + AAC, `low` | 1.04 s | 29.8 | 73 | 18 | +2 ms | 3 |
| RTSP 720p, `low`, 0.5 s keyframes | 0.52 s | 31.0 | 22 | 27 | −82 ms | 34 |
| RTSP MJPEG 720p (60 s) | 0.07 s | 29.8 | 10 | 8 | −47 ms | – |
| VRCDN RTSP 1080p + AAC, `low` (60 s) | 1.06 s | 29.8 | 30 | 13 | −10 ms | 13 |
| VRCDN MPEG-TS 1080p + AAC, `low` (60 s) | 1.05 s | 30.0 | 24 | 12 | +7 ms | 11 |
| RTSP 720p AV1 + AAC, `low` (45 s) | 1.33 s | 30.6 | 7 | 6 | – | 0 |
| RTSP 720p AV1 + AAC, `normal` (45 s) | 8.14 s | 29.8 | 18 | 13 | – | 0 |

The AV1 rows' gaps all come right after engine render stalls, not at segment swaps. "MFT drops" are Media
Foundation's "H264: dropping pending sample (MFT deadlock)" when a decoder starts; low latency starts one every
keyframe interval, so it sees more.

### RTSP details

Addon code gets no TCP or UDP sockets, so RTSP needs a server or camera that supports **RTSP over HTTP tunnelling**
(the QuickTime scheme: one long HTTP GET down, requests up in one long base64 POST; a server that doesn't answer that
way gets a POST per request). An unanswered request or dropped tunnel reconnects up to 5 times, alternating the two
POST styles. Basic and Digest authentication are supported.

## Engine workarounds

Found by measuring test files through the engine's own recorder and frame callbacks:

- **Mono AAC in MP4:** the engine takes the channel count from the `mp4a` header, which many encoders set to a
  placeholder 2 for mono; Media Foundation then outputs mono into a stereo stream, which crackles. `Mp4ChannelFix`
  reads the header by range request and, if it disagrees with the AAC stream, plays a copy with the field corrected
  (files up to 400 MB).
- **Mono Opus in WebM:** the engine's Opus decoder always outputs stereo but sizes the stream from the track's channel
  count, so bimp declares mono Opus as stereo in the segment files it writes (valid Opus, plays cleanly).
- **Uneven frame presentation:** the engine's `VideoPlayer` → texture path held frames (1–4 holds/s of 67 ms+), so
  `VideoFrameSink` uploads every `OnTextureData` frame itself.
- **Black first frame:** a freshly opened player's first output is pure black; the sink skips leading black frames.
- **Spectrum:** only `MusicPlayer` exposes a spectrum (raw FFT magnitudes, 257 bins); `SpectrumBars` maps it to dB
  with a treble tilt and auto-gain. Video playback has no spectrum, so the bars animate instead.

## UI

- **`PanelFit`** keeps world panels laid out at a design size and scaled to the panel (`WorldPanel.RenderScale`), and
  sizes the screen's panel and collider to its model.
- **`MediaScreen`** works out what the camera or mouse points at on the panel itself (a plane trace), so looking and
  USE work from any distance and through `PlayerController` or not. `UseHandled` makes sure one USE press acts once.
- **`ScrollingText`** is a single-line title that scrolls back and forth when it doesn't fit.
- **`InputGlyph`** shows an input action's button (`Input.GetGlyph`), re-read every frame so it follows the device.
- **`PadNavigation`** drives the remote with a gamepad. Pads only reach games through input actions, so it reads
  whatever actions the game binds to A, B and the D-pad (from `Input.GetActions()`), plus the left stick. It runs in
  `Component.ISceneStage.Start()` (before fixed update) and then sets `Input.Suppressed`, so the player doesn't also
  move. Up/down moves by row, left/right within the row; drop down menus (engine `Popup`s) are navigated with their
  own selection.
- **`MenuStyle`** gives the remote's drop down menus a stylesheet matching the remote: popups live in the root panel,
  where a component's stylesheet doesn't reach.
- **`RecentLinks`** keeps the links a client has seen played, in `data/bimp/recent.json`.
- **`MediaRemote`** is a `ScreenPanel` created on demand; it scales with the screen height (as if 1080 high) times
  `bimp_ui_scale`, capped at 90% of the screen width.
