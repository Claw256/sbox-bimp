using System.Text;

namespace Bimp;

/// <summary>
/// Frame by frame recording of a media player around a pause / resume / seek, for diagnosing playback.
/// Run <c>bimp_probe pause</c> (or resume, toggle, seek+10, seek-10, seekto:60, none) in the console,
/// the recording is logged when it finishes.
/// </summary>
public static class MediaProbe
{
	static MediaPlayer target;
	static readonly List<string> rows = new();
	static RealTimeSince since;
	static float duration;
	static string pendingAction;
	static float actionAt;
	static float lastPlayback = -1;
	static readonly List<string> notes = new();

	internal static bool IsRecording( MediaPlayer p ) => target is not null && p == target;

	/// <summary>
	/// "frames" probe: count every frame the decoder delivers (see <see cref="VideoFrameSink"/>) to find dropped / late frames.
	/// </summary>
	internal static bool CountFrames { get; private set; }
	static readonly List<(double time, string who)> frames = new();

	internal static void CountFrame( string who )
	{
		if ( !CountFrames ) return;
		lock ( frames ) frames.Add( (RealTime.NowDouble, who ?? "video") );
	}

	/// <summary>
	/// "tex" probe: read a small patch of the video texture every frame and note when its content changes - what
	/// the screen actually shows, through the normal GPU texture path (unlike "frames", which replaces it).
	/// </summary>
	static bool texProbe;

	/// <summary> "pace" probe: like "tex" but without reading the texture back from the GPU - just when each frame was uploaded, and render frame times. </summary>
	static bool readTexture;
	static readonly List<double> texChanges = new();
	static readonly List<double> renderGaps = new();
	static readonly List<(double time, double ms)> slowFrames = new();

	static readonly List<(double time, double ms, string who)> slowPresents = new();
	static double presentTotal;
	static int presentCalls;

	/// <summary> How long one VideoPlayer.Present() call took (the engine decodes/delivers frames inside it, on the main thread). </summary>
	internal static void PresentTook( double ms, string role, int seq )
	{
		if ( !texProbe ) return;
		presentTotal += ms;
		presentCalls++;
		if ( ms > 4 ) slowPresents.Add( (RealTime.NowDouble, ms, $"{role} seg {seq}") );
	}

	/// <summary>
	/// Garbage collections seen during the probe, from the engine's per-frame counters (<see cref="Sandbox.Diagnostics.PerformanceStats"/>
	/// is public, unlike System.GC): when, the highest generation collected, and how long everything was paused.
	/// </summary>
	static readonly List<(double time, int gen, double pauseMs)> gcEvents = new( 1024 );
	static readonly int[] gcCounts = new int[3];
	static double gcPauseTotal;
	static long bytesAllocated;

	static void AddRenderFrame()
	{
		var ms = RealTime.Delta * 1000;
		renderGaps.Add( ms );
		if ( ms > 30 ) slowFrames.Add( (RealTime.NowDouble, ms) );

		int g0 = Sandbox.Diagnostics.PerformanceStats.Gen0Collections, g1 = Sandbox.Diagnostics.PerformanceStats.Gen1Collections, g2 = Sandbox.Diagnostics.PerformanceStats.Gen2Collections;
		var pause = TimeSpan.FromTicks( Sandbox.Diagnostics.PerformanceStats.GcPause ).TotalMilliseconds;
		bytesAllocated += Sandbox.Diagnostics.PerformanceStats.BytesAllocated;
		gcCounts[0] += g0; gcCounts[1] += g1; gcCounts[2] += g2;
		gcPauseTotal += pause;
		if ( g0 + g1 + g2 > 0 || pause > 0.5 ) gcEvents.Add( (RealTime.NowDouble, g2 > 0 ? 2 : g1 > 0 ? 1 : 0, pause) );
	}

	/// <summary> The GCs within a frame either side of a slow frame, e.g. "[gc1 38ms]". </summary>
	static string GcNear( double time, double frameMs )
	{
		var near = gcEvents.Where( g => Math.Abs( g.time - time ) <= frameMs / 1000 + 0.02 ).ToList();
		if ( near.Count == 0 ) return "";
		return $"[gc{near.Max( g => g.gen )} {near.Sum( g => g.pauseMs ):0}ms]";
	}

	static string GcReport( double seconds )
	{
		var big = gcEvents.Where( g => g.pauseMs > 10 ).Take( 30 ).Select( g => $"{g.time - gcEvents[0].time:0.00}s:gc{g.gen} {g.pauseMs:0}ms" );
		return $"GC: gen0 {gcCounts[0]}, gen1 {gcCounts[1]}, gen2 {gcCounts[2]}; pause total {gcPauseTotal:0}ms, max {(gcEvents.Count > 0 ? gcEvents.Max( g => g.pauseMs ) : 0):0.0}ms; " +
			$"main thread allocated {bytesAllocated / 1048576.0 / Math.Max( 1, seconds ):0.00} MB/s; pauses > 10ms: {gcEvents.Count( g => g.pauseMs > 10 )} [{string.Join( " ", big )}]";
	}

	static void ResetGc()
	{
		gcEvents.Clear();
		Array.Clear( gcCounts );
		gcPauseTotal = 0;
		bytesAllocated = 0;
	}
	static ulong lastTexHash;
	static readonly Color32[] texPatch = new Color32[32 * 32];

	/// <summary> Five 32x32 patches spread over the picture - one small patch can be pixel-identical across genuinely new frames (H.264 skip blocks). </summary>
	static readonly (float x, float y)[] PatchSpots = { (0.5f, 0.5f), (0.2f, 0.25f), (0.8f, 0.25f), (0.2f, 0.75f), (0.8f, 0.75f) };

	static void SampleTexture( MediaPlayer p )
	{
		AddRenderFrame();
		var tex = fileVideo is not null ? (fileNative ? fileVideo.Texture : fileSink.Texture) : p.Backend?.Texture;
		if ( tex is null || !tex.IsLoaded || tex.Width < 128 || tex.Height < 128 ) return;
		ulong h = 1469598103934665603UL;
		foreach ( var (fx, fy) in PatchSpots )
		{
			tex.GetPixels( ((int)(tex.Width * fx) - 16, (int)(tex.Height * fy) - 16, 32, 32), 0, 0, texPatch.AsSpan(), ImageFormat.RGBA8888 );
			foreach ( var c in texPatch ) h = (h ^ (ulong)(c.r | (c.g << 8) | (c.b << 16))) * 1099511628211UL;
		}
		if ( h != lastTexHash ) texChanges.Add( RealTime.NowDouble );
		lastTexHash = h;
	}

	static int deliveredFrames, deliveredRepeats;
	static readonly List<(double time, double brightness)> blackFrames = new();
	static readonly List<double> uploadTimes = new();
	static ulong lastDeliveredHash;

	/// <summary> The frame just uploaded by a <see cref="VideoFrameSink"/>: during the "tex" probe, note whether its centre patch repeats the last one's (repeats in the content itself aren't stutter). </summary>
	internal static void DeliveredFrame( ReadOnlySpan<byte> rgba, int width, int height )
	{
		if ( !texProbe || width < 96 || height < 96 ) return;
		// a sparse sample of the whole frame (every 7th pixel of every 7th row)
		ulong h = 1469598103934665603UL;
		long sum = 0, count = 0;
		for ( int y = 0; y < height; y += 7 )
		{
			var row = y * width * 4;
			for ( int x = 0; x < width * 4; x += 28 )
			{
				h = (h ^ (ulong)(rgba[row + x] | (rgba[row + x + 1] << 8) | (rgba[row + x + 2] << 16))) * 1099511628211UL;
				sum += rgba[row + x] + rgba[row + x + 1] + rgba[row + x + 2];
				count += 3;
			}
		}
		var brightness = count > 0 ? sum / (double)count : 0;
		if ( brightness < 6 ) blackFrames.Add( (RealTime.NowDouble, brightness) );
		deliveredFrames++;
		uploadTimes.Add( RealTime.NowDouble );
		if ( h == lastDeliveredHash ) deliveredRepeats++;
		lastDeliveredHash = h;
	}

	/// <summary> A frame uploaded without its pixels at hand (Motion JPEG): just when. </summary>
	internal static void DeliveredFrame()
	{
		if ( !texProbe ) return;
		deliveredFrames++;
		uploadTimes.Add( RealTime.NowDouble );
	}

	static string TextureReport()
	{
		if ( !readTexture ) texChanges.AddRange( uploadTimes ); // pace: the uploads are the picture changes
		if ( texChanges.Count < 3 )
		{
			// nothing playing: the render frame baseline only
			var r = renderGaps.OrderBy( x => x ).ToList();
			if ( r.Count == 0 ) return "tex: no changes seen";
			var s0 = slowFrames.Count > 0 ? slowFrames[0].time : 0;
			return $"no video uploads\nrender frames: {r.Count}, median {r[r.Count / 2]:0.0}ms p99 {r[(int)(r.Count * 0.99)]:0.0}ms max {r[^1]:0.0}ms\n" +
				$"render frames > 30ms: {slowFrames.Count} [{string.Join( " ", slowFrames.Take( 30 ).Select( f => $"{f.time - s0:0.00}s:{f.ms:0}ms{GcNear( f.time, f.ms )}" ) )}]\n" +
				GcReport( r.Sum() / 1000 );
		}
		var gaps = new List<double>();
		for ( int i = 1; i < texChanges.Count; i++ ) gaps.Add( (texChanges[i] - texChanges[i - 1]) * 1000 );
		var span = texChanges[^1] - texChanges[0];
		var sorted = gaps.OrderBy( x => x ).ToList();
		double Pct( List<double> l, double q ) => l[(int)Math.Clamp( q * (l.Count - 1), 0, l.Count - 1 )];
		var rs = renderGaps.OrderBy( x => x ).ToList();
		var sb = new StringBuilder();
		sb.AppendLine( $"tex: {texChanges.Count} picture changes in {span:0.00}s = {(texChanges.Count - 1) / span:0.0}/s; interval median {Pct( sorted, 0.5 ):0.0}ms p95 {Pct( sorted, 0.95 ):0.0}ms max {sorted[^1]:0.0}ms" );
		sb.AppendLine( $"render frames: {rs.Count}, median {Pct( rs, 0.5 ):0.0}ms p99 {Pct( rs, 0.99 ):0.0}ms max {rs[^1]:0.0}ms" );
		var buckets = gaps.GroupBy( g => g < 25 ? "<25" : g < 42 ? "25-42" : g < 58 ? "42-58" : g < 75 ? "58-75" : ">=75" ).OrderBy( g => g.Key ).Select( g => $"{g.Key}ms:{g.Count()}" );
		sb.AppendLine( $"intervals: {string.Join( ", ", buckets )}" );
		var holds = gaps.Select( ( g, i ) => (g, t: texChanges[i + 1] - texChanges[0]) ).Where( x => x.g >= 58 ).Take( 25 ).Select( x => $"{x.t:0.00}s:{x.g:0}ms" );
		sb.AppendLine( $"holds >= 58ms: {gaps.Count( g => g >= 58 )} [{string.Join( " ", holds )}]" );
		sb.AppendLine( $"delivered frames: {deliveredFrames}, of which were identical to the previous frame (sparse whole-frame sample): {deliveredRepeats}" );
		var t0 = uploadTimes.Count > 0 ? uploadTimes[0] : 0;
		sb.AppendLine( $"render frames > 30ms: {slowFrames.Count} [{string.Join( " ", slowFrames.Take( 30 ).Select( f => $"{f.time - t0:0.00}s:{f.ms:0}ms{GcNear( f.time, f.ms )}" ) )}]" );
		sb.AppendLine( GcReport( rs.Sum() / 1000 ) );
		sb.AppendLine( $"Present() calls: {presentCalls}, average {(presentCalls > 0 ? presentTotal / presentCalls : 0):0.00}ms; slower than 4ms: {slowPresents.Count} [{string.Join( " ", slowPresents.OrderByDescending( x => x.ms ).Take( 15 ).OrderBy( x => x.time ).Select( x => $"{x.time - t0:0.00}s:{x.ms:0}ms({x.who})" ) )}]" );
		sb.AppendLine( $"near-black frames uploaded: {blackFrames.Count} [{string.Join( " ", blackFrames.Take( 30 ).Select( b => $"{b.time - t0:0.00}s(avg {b.brightness:0.0})" ) )}]" );
		if ( uploadTimes.Count > 2 )
		{
			// exact: when the picture on screen changed (every upload is a new frame), no GPU readback involved
			var ug = new List<(double at, double gap)>();
			for ( int i = 1; i < uploadTimes.Count; i++ ) ug.Add( (uploadTimes[i] - uploadTimes[0], (uploadTimes[i] - uploadTimes[i - 1]) * 1000) );
			var us = ug.Select( x => x.gap ).OrderBy( x => x ).ToList();
			sb.AppendLine( $"uploads: {uploadTimes.Count} = {(uploadTimes.Count - 1) / (uploadTimes[^1] - uploadTimes[0]):0.0}/s; interval median {Pct( us, 0.5 ):0.0}ms p95 {Pct( us, 0.95 ):0.0}ms max {us[^1]:0.0}ms; gaps >= 50ms: {ug.Count( x => x.gap >= 50 )} [{string.Join( " ", ug.Where( x => x.gap >= 50 ).Take( 20 ).Select( x => $"{x.at:0.00}s:{x.gap:0}ms" ) )}]" );
		}
		sb.AppendLine( $"per second (uploads/clock ms advanced): {SecondsTimeline( t0 )}" );
		return sb.ToString();
	}

	/// <summary> Player clock during the pace probe, every frame. </summary>
	static readonly List<(double time, float playback)> clockSamples = new();

	/// <summary> Each second of the run: frames uploaded, and how far the player's clock moved (ms). </summary>
	static string SecondsTimeline( double t0 )
	{
		if ( clockSamples.Count < 2 ) return "-";
		var sb = new StringBuilder();
		var end = clockSamples[^1].time;
		var ci = 0;
		for ( var s = t0; s < end; s += 1 )
		{
			var n = uploadTimes.Count( u => u >= s && u < s + 1 );
			while ( ci < clockSamples.Count - 1 && clockSamples[ci].time < s ) ci++;
			var cj = ci;
			while ( cj < clockSamples.Count - 1 && clockSamples[cj].time < s + 1 ) cj++;
			var advanced = (clockSamples[cj].playback - clockSamples[ci].playback) * 1000;
			sb.Append( $"{s - t0:0}:{n}/{advanced:0} " );
		}
		return sb.ToString();
	}

	static string FrameReport()
	{
		List<(double time, string who)> f;
		lock ( frames ) f = frames.ToList();
		if ( f.Count < 3 ) return "frames: none delivered";

		var gaps = new List<(double at, double gap, string who)>();
		for ( int i = 1; i < f.Count; i++ )
			gaps.Add( (f[i].time - f[0].time, (f[i].time - f[i - 1].time) * 1000, f[i - 1].who == f[i].who ? f[i].who : $"{f[i - 1].who} -> {f[i].who}  SEGMENT SWAP") );
		var span = f[^1].time - f[0].time;
		var sorted = gaps.Select( g => g.gap ).OrderBy( x => x ).ToList();
		double Pct( double p ) => sorted[(int)Math.Clamp( p * (sorted.Count - 1), 0, sorted.Count - 1 )];

		var sb = new StringBuilder();
		sb.AppendLine( $"frames: {f.Count} in {span:0.00}s = {f.Count / span:0.0} fps; gap median {Pct( 0.5 ):0.0}ms p95 {Pct( 0.95 ):0.0}ms max {sorted[^1]:0.0}ms" );
		var median = Pct( 0.5 );
		var bad = gaps.Where( g => g.gap > median * 1.8 ).ToList();
		sb.AppendLine( $"long gaps (> 1.8x median): {bad.Count}" );
		foreach ( var g in bad.Take( 30 ) ) sb.AppendLine( $"   at {g.at * 1000,7:0}ms  gap {g.gap,6:0.0}ms  ({g.who})" );
		return sb.ToString();
	}

	/// <summary>
	/// [probe] Skip VideoFrameSink's Texture.Update (frames are still counted as uploads) - separates the upload's cost from the rest.
	/// </summary>
	[ConVar( "bimp_probe_noupload", Help = "[probe] Don't upload decoded frames to the screen texture (still counted)" )]
	public static bool NoUpload { get; set; }

	static VideoPlayer fileVideo;
	static VideoFrameSink fileSink;
	static ulong lastFilePresent;
	static Scene fileScene;

	/// <summary> The probe file plays to the engine's own texture: the "tex" probe reads that one. </summary>
	static bool fileNative;

	/// <summary>
	/// [probe] Play a finished video file from FileSystem.Data straight through one VideoPlayer and a VideoFrameSink - no
	/// segmenter, no swaps - to compare against the live pipeline. Not drawn anywhere; its frames count as uploads for "pace".
	/// </summary>
	[ConCmd( "bimp_probe_file", Help = "[probe] Play a FileSystem.Data video directly (looped), no argument stops it; 'native' = the engine's own texture, no frame sink; 'once' = no loop" )]
	public static void FileCmd( string path = null, string mode = null )
	{
		fileNative = mode == "native";
		fileVideo?.Dispose();
		fileSink?.Dispose();
		fileVideo = null;
		fileSink = null;
		if ( string.IsNullOrWhiteSpace( path ) ) return;

		fileSink = new VideoFrameSink();
		fileVideo = new VideoPlayer();
		if ( !fileNative ) fileSink.Attach( fileVideo, null, null, "file" );
		fileVideo.Repeat = mode != "once";
		fileVideo.Play( FileSystem.Data, path );
		fileScene = Game.ActiveScene;
		Log.Info( $"[bimp probe] playing {path} directly" );
	}

	/// <summary> Called every frame by the media players: keeps the <c>bimp_probe_file</c> video running (once a frame). </summary>
	internal static void PresentFile()
	{
		if ( fileVideo is null || lastFilePresent == Application.FrameCount ) return;
		lastFilePresent = Application.FrameCount;

		// statics outlive the play session - don't carry a native player into the next one
		if ( Game.ActiveScene != fileScene )
		{
			FileCmd();
			return;
		}

		var t = System.Diagnostics.Stopwatch.GetTimestamp();
		fileVideo.Present();
		PresentTook( System.Diagnostics.Stopwatch.GetElapsedTime( t ).TotalMilliseconds, "file", 0 );
	}

	/// <summary>
	/// [probe] Make garbage like the live pipeline does, with nothing playing: ~1.5 MB/s of 10-200 KB arrays kept ~10 s
	/// (like the per-frame samples held until their segment is written), plus an 8 MB array every 8 s.
	/// </summary>
	[ConCmd( "bimp_probe_garbage", Help = "[probe] Allocate pipeline-like garbage for N seconds" )]
	public static void GarbageCmd( float seconds = 100 ) => _ = MakeGarbage( seconds );

	static async Task MakeGarbage( float seconds )
	{
		var kept = new Queue<(double time, byte[] data)>();
		var random = new Random( 1 );
		var start = RealTime.NowDouble;
		var lastBig = start;
		byte[] big = null;
		Log.Info( $"[bimp probe] making garbage for {seconds}s" );
		while ( RealTime.NowDouble - start < seconds )
		{
			var now = RealTime.NowDouble;
			var a = new byte[random.Next( 10, 200 ) * 1024];
			a[0] = 1;
			kept.Enqueue( (now, a) );
			while ( kept.Count > 0 && now - kept.Peek().time > 10 ) kept.Dequeue();
			if ( now - lastBig >= 8 ) { big = new byte[8 * 1024 * 1024]; big[0] = 1; lastBig = now; }
			await Task.Delay( 70 );
		}
		Log.Info( $"[bimp probe] garbage done ({(big?.Length ?? 0) > 0})" );
	}

	static MediaPlayer latencyTarget;
	static double latencyUntil;
	static readonly List<double> latencies = new(), behinds = new(), avOffsets = new();

	/// <summary>
	/// Record a live player's latency every frame for a while: how long ago the frame on screen was sent (RTCP sender
	/// clock) and how far it is behind the newest frame received.
	/// </summary>
	[ConCmd( "bimp_probe_latency", Help = "[probe] Record live latency for N seconds (sender clock and behind-newest)" )]
	public static void LatencyCmd( float seconds = 30 )
	{
		latencyTarget = Game.ActiveScene?.GetAllComponents<MediaPlayer>().FirstOrDefault( x => x.Backend?.LiveDescription is not null );
		if ( latencyTarget is null ) { Log.Warning( "[bimp probe] no live stream playing" ); return; }
		latencies.Clear();
		behinds.Clear();
		avOffsets.Clear();
		latencyUntil = RealTime.NowDouble + seconds;
		Log.Info( $"[bimp probe] recording latency of {latencyTarget.GameObject.Name} for {seconds}s" );
	}

	internal static void SampleLatency( MediaPlayer p )
	{
		if ( p != latencyTarget || p.Backend is not { } b ) return;
		if ( b.LiveLatency is { } l ) latencies.Add( l );
		if ( b.Loaded ) behinds.Add( b.LiveBehindNewest );
		if ( b.LiveAvOffset is { } av ) avOffsets.Add( av );
		if ( RealTime.NowDouble < latencyUntil ) return;

		static string Stats( List<double> v )
		{
			if ( v.Count == 0 ) return "none";
			var s = v.OrderBy( x => x ).ToList();
			return $"median {s[s.Count / 2]:0.000}s p5 {s[(int)(s.Count * 0.05)]:0.000}s p95 {s[(int)(s.Count * 0.95)]:0.000}s min {s[0]:0.000}s max {s[^1]:0.000}s ({s.Count} frames)";
		}
		Log.Info( $"[bimp probe] latency: sent->screen {Stats( latencies )}\n  behind newest frame {Stats( behinds )}\n  A/V offset (audio minus video) {Stats( avOffsets )}\n  {b.LiveDescription}" );
		latencyTarget = null;
	}

	/// <summary>
	/// Play a url on the first media player in the scene (host), for testing from the console.
	/// </summary>
	[ConCmd( "bimp_play", Help = "Play a url on the first media player in the scene (or the one whose name contains the second argument)" )]
	public static void PlayCmd( string url, string player = null )
	{
		var p = Game.ActiveScene?.GetAllComponents<MediaPlayer>()
			.FirstOrDefault( x => string.IsNullOrEmpty( player ) || x.GameObject.Name.Contains( player, StringComparison.OrdinalIgnoreCase ) );
		if ( p is null ) { Log.Warning( "[bimp probe] no media player in the scene" ); return; }
		_ = p.PlayNowAsync( url, "Console" );
	}

	/// <summary>
	/// Print what every media player in the scene is doing, for diagnosing from the console.
	/// </summary>
	[ConCmd( "bimp_status", Help = "Print the state of every media player in the scene" )]
	public static void StatusCmd()
	{
		foreach ( var p in Game.ActiveScene?.GetAllComponents<MediaPlayer>() ?? Enumerable.Empty<MediaPlayer>() )
		{
			var b = p.Backend;
			var backend = b is null ? "no backend"
				: $"preparing={b.IsPreparing} loaded={b.Loaded} time={b.Time:0.00} {b.Width}x{b.Height} tex={(b.Texture is { } t ? $"{t.Width}x{t.Height} loaded={t.IsLoaded} valid={t.IsValid()}" : "null")} hasVideo={b.HasVideo} audio={b.AudioFormat}{(b.LiveDescription is { } ld ? $" live=[{ld}]" : "")} paused={b.IsPaused} finished={b.Finished} error={b.Error ?? "-"}";
			Log.Info( $"[bimp] {p.GameObject.Name}: status={p.Status ?? "-"} title={p.Title ?? "-"} play={p.PlayUrl ?? "-"} local={p.LocalStreamUrl ?? "-"} t={p.CurrentTime:0.00}/{p.MediaDuration:0.00} seekByReload={p.SeekByReload} | {backend} | notice={p.LocalNotice ?? "-"}" );
		}
	}

	[ConCmd( "bimp_probe_spectrum", Help = "[probe] Print the spectrum and amplitude the visualiser gets from a media player, 4 times a second for N seconds" )]
	public static async void SpectrumCmd( float seconds = 5, string player = null )
	{
		var p = Game.ActiveScene?.GetAllComponents<MediaPlayer>()
			.FirstOrDefault( x => string.IsNullOrEmpty( player ) || x.GameObject.Name.Contains( player, StringComparison.OrdinalIgnoreCase ) );
		if ( p is null ) { Log.Warning( "[bimp probe] no media player in the scene" ); return; }

		var lines = new System.Text.StringBuilder();
		for ( var t = 0f; t < seconds; t += 0.25f )
		{
			var b = p.Backend;
			if ( b is not null )
			{
				var s = b.Spectrum.ToArray();
				var sorted = s.OrderBy( x => x ).ToArray();
				float Q( float q ) => sorted.Length == 0 ? 0 : sorted[(int)Math.Min( sorted.Length - 1, q * sorted.Length )];
				var bands = s.Length == 0 ? "" : string.Join( " ", Enumerable.Range( 0, 8 ).Select( i => s.Skip( i * s.Length / 8 ).Take( Math.Max( 1, s.Length / 8 ) ).Max().ToString( "0.###" ) ) );
				var shown = p.GameObject.GetComponentsInChildren<MediaSpeaker>().Select( x => x.Panel?.Descendants.OfType<SpectrumBars>().FirstOrDefault() ).FirstOrDefault( x => x is not null )?.Shown;
				var heights = shown is null ? "no bars" : string.Join( " ", shown.Select( h => $"{h * 100:0}" ) );
				lines.AppendLine( $"{t:0.00}s audioOnly={b.IsAudioOnly} len={s.Length} min={Q( 0 ):0.###} med={Q( 0.5f ):0.###} p90={Q( 0.9f ):0.###} max={Q( 1 ):0.###} amp={b.Amplitude:0.###} | band max: {bands} | bars %: {heights}" );
			}
			await GameTask.DelaySeconds( 0.25f );
		}
		Log.Info( $"[bimp probe] spectrum of {p.GameObject.Name}\n{lines}" );
	}

	[ConCmd( "bimp_queue", Help = "Queue a url on the first media player in the scene (or the one whose name contains the second argument)" )]
	public static void QueueCmd( string url, string player = null )
	{
		var p = Game.ActiveScene?.GetAllComponents<MediaPlayer>()
			.FirstOrDefault( x => string.IsNullOrEmpty( player ) || x.GameObject.Name.Contains( player, StringComparison.OrdinalIgnoreCase ) );
		if ( p is null ) { Log.Warning( "[bimp probe] no media player in the scene" ); return; }
		p.RequestEnqueue( url );
	}

	[ConCmd( "bimp_remote", Help = "[probe] Open (or close) the full remote for the first media player (or the one whose name contains the argument)" )]
	public static void RemoteCmd( string player = null )
	{
		var p = Game.ActiveScene?.GetAllComponents<MediaPlayer>()
			.FirstOrDefault( x => string.IsNullOrEmpty( player ) || x.GameObject.Name.Contains( player, StringComparison.OrdinalIgnoreCase ) );
		if ( p is null ) { Log.Warning( "[bimp probe] no media player in the scene" ); return; }
		MediaRemote.Toggle( p );
	}

	[ConCmd( "bimp_probe_pad", Help = "[probe] Print the controller state and the open remote's controls; or replay pad moves, e.g. down,down,right" )]
	public static void PadCmd( string moves = null )
	{
		var remote = Game.ActiveScene?.GetAllComponents<MediaRemote>().FirstOrDefault();
		if ( remote is not null && !string.IsNullOrWhiteSpace( moves ) )
		{
			// e.g. "down,down,right": replay pad moves and print where each lands
			foreach ( var m in moves.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
				Log.Info( $"[bimp probe] pad {remote.ProbeMove( m )}" );
			return;
		}
		Log.Info( $"[bimp probe] pad: {(remote is null ? $"usingController={Input.UsingController} controllers={Input.ControllerCount} (no remote open)" : remote.DescribePad())}" );
		foreach ( var screen in Game.ActiveScene?.GetAllComponents<MediaScreen>() ?? Enumerable.Empty<MediaScreen>() )
			Log.Info( $"[bimp probe] screen {screen.GameObject.Name}: {screen.DescribeControls()}" );
	}

	[ConCmd( "bimp_probe_titles", Help = "[probe] Print every scrolling title's size and state" )]
	public static void TitlesCmd()
	{
		foreach ( var root in Game.ActiveScene?.GetAllComponents<PanelComponent>() ?? Enumerable.Empty<PanelComponent>() )
			foreach ( var t in root.Panel?.Descendants.OfType<ScrollingText>() ?? Enumerable.Empty<ScrollingText>() )
				Log.Info( $"[bimp probe] {root.GameObject.Name}: \"{t.Text}\" {t.Describe()}" );
	}

	[ConCmd( "bimp_probe", Help = "Record a media player frame by frame around an action: none, pause, resume, toggle, seek+N, seek-N, seekto:N" )]
	public static void Start( string action = "none", float seconds = 4, float delay = 0.5f )
	{
		var p = Game.ActiveScene?.GetAllComponents<MediaPlayer>().FirstOrDefault( x => x.HasMedia );
		// "pace" with nothing playing measures the render frame times alone - the baseline to compare playback against
		if ( p is null && action is "pace" or "tex" ) p = Game.ActiveScene?.GetAllComponents<MediaPlayer>().FirstOrDefault();
		if ( p is null ) { Log.Warning( "[bimp probe] nothing is playing" ); return; }

		target = p;
		rows.Clear();
		notes.Clear();
		rows.Add( "   t(ms) dt(ms) | synced P expected | native P playback Δplay(ms) Δ-dt(ms) drift(ms) | note" );
		since = 0;
		duration = seconds;
		pendingAction = action;
		actionAt = delay;
		lastPlayback = -1;
		Log.Info( $"[bimp probe] recording {p.GameObject.Name} for {seconds}s, '{action}' at {delay}s" );
	}

	/// <summary>
	/// Something we did to the native player (pause, resume, seek), shown in the row for this frame.
	/// </summary>
	internal static void Note( string note )
	{
		if ( target is not null ) notes.Add( note );
	}

	/// <summary>
	/// Called by the recorded MediaPlayer at the end of its update.
	/// </summary>
	internal static void Sample( MediaPlayer p )
	{
		if ( p != target ) return;

		if ( texProbe )
		{
			if ( readTexture ) SampleTexture( p );
			else AddRenderFrame();
		}

		if ( pendingAction is not null && since >= actionAt )
		{
			notes.Add( $">>> {pendingAction}" );
			Perform( p, pendingAction );
			pendingAction = null;
		}

		var b = p.Backend;

		// pace only dumps rows with a note - don't format (and allocate) one every frame, it's what we're measuring
		if ( texProbe && b is not null ) clockSamples.Add( (RealTime.NowDouble, b.PlaybackTime) );

		if ( texProbe && !readTexture && notes.Count == 0 && since <= duration )
		{
			lastPlayback = b?.PlaybackTime ?? lastPlayback;
			return;
		}

		var sb = new StringBuilder();
		sb.Append( $"{since * 1000,8:0} {RealTime.Delta * 1000,6:0.0} | {(p.Paused ? "P" : "-")} {p.CurrentTime,9:0.000} | " );

		if ( b is not null )
		{
			var t = b.PlaybackTime;
			var dPlay = lastPlayback < 0 ? 0 : (t - lastPlayback) * 1000;
			// how much the native clock moved beyond the real frame time: + means it jumped ahead
			var ahead = b.IsPaused || lastPlayback < 0 ? 0 : dPlay - RealTime.Delta * 1000;
			sb.Append( $"{(b.IsPaused ? "P" : "-")} {t,9:0.000} {dPlay,8:0.0} {ahead,8:0.0} {(b.Time - p.CurrentTime) * 1000,8:0} {b.Width}x{b.Height}{(b.Error is not null ? " ERR " + b.Error : "")}" );
			lastPlayback = t;
		}
		else sb.Append( "(no backend)" );

		if ( notes.Count > 0 ) sb.Append( " | " + string.Join( ", ", notes ) );
		notes.Clear();
		rows.Add( sb.ToString() );

		if ( since > duration )
		{
			// long pacing runs: only the header and the rows with a note (a row with notes has a third " | ") - dumping
			// thousands of rows at once stalls the main thread, which is itself a hitch
			var dump = texProbe ? rows.Take( 1 ).Concat( rows.Skip( 1 ).Where( r => r.Split( " | " ).Length > 3 ) ) : rows;
			Log.Info( "[bimp probe] result\n" + string.Join( "\n", dump ) );
			if ( texProbe )
			{
				Log.Info( "[bimp probe] texture\n" + TextureReport() );
				texProbe = false;
			}
			if ( CountFrames )
			{
				Log.Info( "[bimp probe] frames\n" + FrameReport() );
				CountFrames = false;
			}
			target = null;
		}
	}

	static void Perform( MediaPlayer p, string action )
	{
		action = action.Trim().ToLowerInvariant();
		if ( action == "pause" ) p.SetPaused( true );
		else if ( action == "resume" ) p.SetPaused( false );
		else if ( action == "toggle" ) p.SetPaused( !p.Paused );
		else if ( action == "reload" ) p.ReloadBackend();
		else if ( action is "tex" or "pace" )
		{
			readTexture = action == "tex";
			texChanges.Clear();
			renderGaps.Clear();
			ResetGc();
			lastTexHash = 0;
			deliveredFrames = deliveredRepeats = 0;
			blackFrames.Clear();
			slowFrames.Clear();
			slowPresents.Clear();
			presentTotal = 0;
			presentCalls = 0;
			uploadTimes.Clear();
			clockSamples.Clear();
			lastDeliveredHash = 0;
			texProbe = true;
		}
		else if ( action == "frames" )
		{
			lock ( frames ) frames.Clear();
			CountFrames = true;
		}
		// openat:T - open merged media at T with TimeOffset 0, to see what the native player reports
		else if ( action.StartsWith( "openat:" ) && float.TryParse( action[7..], out var at ) ) p.ReloadBackendAt( at, 0 );
		else if ( action.StartsWith( "seekto:" ) && float.TryParse( action[7..], out var to ) ) p.Seek( to );
		else if ( action.StartsWith( "seek" ) && float.TryParse( action[4..], out var by ) ) p.Seek( p.CurrentTime + by );
	}

	/// <summary> [probe] GET a url the way the sandbox does, and print the status, headers and the start of the body. </summary>
	[ConCmd( "bimp_fetch", Help = "[probe] GET a url through Sandbox.Http and print what came back; 'follow' then GETs the first m3u8 url in it" )]
	public static void FetchCmd( string url, string follow = null ) => _ = Fetch( url, follow == "follow" );

	static async Task Fetch( string url, bool follow = false )
	{
		try
		{
			using var r = await Http.RequestAsync( url );
			var body = await r.Content.ReadAsStringAsync();
			var next = System.Text.RegularExpressions.Regex.Match( body.Replace( @"\/", "/" ), @"https?://[^""\s]+\.m3u8[^""\s]*" );
			if ( follow && next.Success ) _ = Fetch( next.Value );
			Log.Info( $"[bimp probe] fetch {(int)r.StatusCode} {url[..Math.Min( 120, url.Length )]} | {string.Join( "; ", r.Headers.Select( h => $"{h.Key}={string.Join( ",", h.Value )}" ) )} | {body[..Math.Min( 600, body.Length )]}" );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[bimp probe] fetch {url}: {e.Message}" );
		}
	}
}
