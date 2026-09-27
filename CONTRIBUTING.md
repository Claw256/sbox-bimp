# Contributing to BIMP

Thanks for helping. Bug reports, fixes and new site extractors are all welcome.

## Getting set up

1. Install [s&box](https://sbox.game) and open `bimp.sbproj` in the editor. It's a **library** project (`rexi/bimp`), not a game.
2. Open `scenes/mediaplayer_test.scene`: it has a screen, a speaker and a single-player controller.
3. Press play, look at the screen and press USE to open the remote, or use the console commands below.

The engine's managed source ([sbox-public](https://github.com/Facepunch/sbox-public)) is the best reference for how the
UI, input and `VideoPlayer` behave.

## Project layout

| Path | What's there |
|---|---|
| `Code/MediaPlayer.cs` | The networked component: synced state, queue, permissions, host requests |
| `Code/MediaBackend.cs`, `SegmentPlayer.cs`, `VideoFrameSink.cs` | Local playback: engine players, segment swaps, frame upload |
| `Code/MediaScreen.razor`, `MediaSpeaker.razor`, `MediaRemote.razor` | The screen, speaker display and remote UI (each with a `.razor.scss`) |
| `Code/ScrollingText.cs`, `InputGlyph.cs`, `PadNavigation.cs`, `MenuStyle.cs`, `SpectrumBars.cs`, `PanelFit.cs` | Shared UI pieces |
| `Code/MediaInteract.cs`, `UseHandled.cs` | USE handling |
| `Code/MediaSettings.cs` | Console variables |
| `Code/MediaProbe.cs`, `LatencyLab.cs` | Diagnostic console commands |
| `Code/Resolver/` | Link resolving: `BimpResolverSystem` (host), `PlayToken` (per-client sessions), `FormatSelector` |
| `Code/Resolver/Extractors/` | One extractor per site (YouTube, Twitch, Kick, Vimeo, SoundCloud, HLS...) |
| `Code/Resolver/Media/` | WebM/MP4 parsing and writing, range downloads, the YouTube segmenter |
| `Code/Resolver/Live/` | Live ingest: RTSP tunnel, HLS, MPEG-TS, AAC decoding, live segmenter |
| `Assets/` | Prefabs and test scenes |
| `docs/` | [ARCHITECTURE.md](docs/ARCHITECTURE.md) (how it works) and [HITCHING.md](docs/HITCHING.md) (measurement log) |

## Rules of the road

- **Stay inside the s&box sandbox.** Game code runs against an API whitelist. Some ordinary .NET calls aren't
  allowed (for example `Environment.NewLine`), there are no raw sockets, no clipboard, no processes. Check the
  compile output after every change.
- **No external services.** BIMP must work with nothing but the game: no hosted resolver, relay or proxy.
- **Each client resolves and streams its own media.** The host only syncs a play token or link and the timeline.
  Stream URLs are tied to the client that requested them.
- **Measure, don't guess.** Playback changes should come with before/after numbers from the probe commands
  (see below). Keep workarounds for engine behaviour documented where they live.
- **Keep it a library.** Don't add game-mode settings, startup scenes or input bindings of your own; use the game's
  actions (see `PadNavigation` for how controller buttons are read).

## Code style

Match the surrounding code. `.editorconfig` sets most of it:
- Tabs, braces on their own line, and spaces inside call, declaration and statement parentheses: `Foo( x )`,
  `if ( ready )`. Indexers stay tight: `list[i]`.
- `///` doc comments on types and non-obvious members that explain *why*, briefly. Plain, short comments inside code.
- Small, focused files; one extractor per site.

## Testing in the editor

Console commands (type them in the editor console, or send them through the editor's MCP server with
`console_command` and read the result with `read_console`):

| Command | |
|---|---|
| `bimp_play <url> [player]` | Play a link on the first media player (or the one whose name contains `player`) |
| `bimp_queue <url> [player]` | Queue a link |
| `bimp_status` | Every player's state, stream, live counters and last error |
| `bimp_remote [player]` | Open or close the remote |
| `bimp_probe <action> [seconds]` | Record a player frame by frame around an action (`none`, `pause`, `seek+N`, `pace`...) and log a table |
| `bimp_probe_latency <seconds>` | Record live latency |
| `bimp_probe_spectrum [seconds] [player]` | The spectrum the visualiser gets and the bar heights it shows |
| `bimp_probe_titles` | Every scrolling title's size and scroll position |
| `bimp_probe_pad [moves]` | Controller state and the remote's controls; or replay pad moves (`down,down,right,a`) |
| `bimp_probe_file <path> [native\|once]` | Play a `FileSystem.Data` file directly, bypassing BIMP's pipeline |
| `bimp_fetch <url> [follow]` | GET a url through `Sandbox.Http` and print what came back |
| `bimp_yt_formats <id>` | The formats each YouTube client gets, and whether they download |
| `bimp_yt_fetch …`, `bimp_lab_*`, `bimp_probe_garbage` | Lower-level experiments (see their help text) |

Tips:
- **Stop play mode before editing code.** A hotload while a live stream has async work pending has crashed the
  editor.
- Changes made to scene objects during play mode through editor tools can land in the editor's scene; check for
  unsaved scene changes before committing.
- YouTube stream sessions are cached for 10 minutes per play token, so repeat tests of the same video reuse them.

## Pull requests

1. Keep a PR to one change, and describe what you measured or tested (commands and numbers are ideal).
2. Make sure the project compiles in the editor with no errors, and that `scenes/mediaplayer_test.scene` still plays.
3. Update the README or `docs/` if behaviour, settings or supported sites change.

## Reporting bugs

Include:
- the link (or the kind of link) and what happened;
- the output of `bimp_status`, and any `[bimp]` warnings in the console;
- for stutter or sync issues, a `bimp_probe pace 30` table if you can.

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
