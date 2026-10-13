using System.Diagnostics;
using System.Text;
using System.Threading;
using Sandbox.Diagnostics;

namespace Bimp;

/// <summary> What a <see cref="Trace"/> event is. Spans have a Begin and an End; the End carries the span's duration. </summary>
public enum TraceId
{
	None,
	Frame,            // main thread, once a frame: Arg = frame gap in microseconds
	PresentBegin, PresentEnd,       // VideoPlayer.Present(): Arg = segment seq
	TextureData,                    // OnTextureData: Arg = 1 when it went to the screen, 2 held, 3 skipped black
	UploadBegin, UploadEnd,         // Texture.Update: Value (End) = ticks taken, Arg = bytes
	UpdatePlaybackBegin, UpdatePlaybackEnd,
	SegOpen, SegPlay, SegFirstFrame, SegSwap, SegClose,
	PlaybackTime,                   // Arg = segment seq, Value = milliseconds
	DownloadBegin, DownloadEnd,     // a whole segment's (or a chunk's) download: Arg = bytes
	MuxBegin, MuxEnd,
	WriteBegin, WriteEnd,           // Arg = bytes
	ReturnBuffer, FileDelete,
	Hitch,                          // the detector fired: Arg = verdict number, Value = gap in ticks
}

/// <summary>
/// One timeline for everything bimp can see, to find what a hitch lines up with. Any thread writes events into a
/// lock-free ring (24 bytes each, no allocation, one branch when off); <c>bimp_trace_dump</c> writes it as a CSV and
/// the hitch detector classifies a long frame or a long gap between pictures as it happens. See docs/PROFILING.md.
/// </summary>
public static class Trace
{
	struct Event
	{
		public long Ticks;
		public int Id;     // low 16 bits: TraceId, high 16: managed thread id
		public int Arg;
		public long Value;
	}

	const int Size = 1 << 16;
	const int Mask = Size - 1;
	static readonly Event[] ring = new Event[Size];
	static int head = -1;

	static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

	/// <summary> True while recording. Read by every hook. </summary>
	public static bool On;

	/// <summary> Record events, and run the hitch detector (also turns the engine's GPU profiler on). </summary>
	[ConVar( "bimp_trace", Help = "[probe] 1 = record the bimp event timeline and detect hitches (see bimp_trace_dump, bimp_trace_summary)" )]
	public static bool Enabled
	{
		get => On;
		set
		{
			On = value;
			try { GpuProfilerStats.Enabled = value; } catch ( Exception ) { }
			if ( value ) ResetDetector();
		}
	}

	/// <summary> A frame (or gap between pictures) longer than this is a hitch, in milliseconds. </summary>
	[ConVar( "bimp_trace_hitch", Help = "[probe] Hitch detector threshold in milliseconds (default 50)" )]
	public static float HitchMs { get; set; } = 50;

	public static long Now => Stopwatch.GetTimestamp();

	public static void Mark( TraceId id, int arg = 0, long value = 0 )
	{
		if ( !On ) return;
		Write( id, arg, value, Stopwatch.GetTimestamp() );
	}

	static void Write( TraceId id, int arg, long value, long ticks )
	{
		ref var e = ref ring[Interlocked.Increment( ref head ) & Mask];
		e.Ticks = 0;
		e.Id = (int)id | (Environment.CurrentManagedThreadId << 16);
		e.Arg = arg;
		e.Value = value;
		e.Ticks = ticks;
	}

	/// <summary>
	/// A span: records Begin now and End (with the duration) on Dispose, and times the same region under
	/// <see cref="Performance.Scope"/> so it shows in the engine's timings. <c>using var s = Trace.Span( ... );</c>
	/// </summary>
	public static Span Begin( TraceId begin, TraceId end, string scope = null, int arg = 0 )
	{
		if ( !On ) return default;
		var s = new Span { end = end, arg = arg, start = Stopwatch.GetTimestamp(), on = true };
		Write( begin, arg, 0, s.start );
		if ( scope is not null ) s.scope = Performance.Scope( scope );
		return s;
	}

	public ref struct Span
	{
		internal TraceId end;
		internal int arg;
		internal long start;
		internal bool on;
		internal Performance.ScopeSection scope;

		/// <summary> Change the argument recorded with the End event (for example the bytes once known). </summary>
		public void SetArg( int value ) => arg = value;

		public void Dispose()
		{
			if ( !on ) return;
			scope.Dispose();
			var t = Stopwatch.GetTimestamp();
			Write( end, arg, t - start, t );
		}
	}

	// ---- worker gauge: how many mux/download workers run at once (the only thread-pool pressure proxy we can see)

	static int workers;
	public static int Workers => workers;

	public static void WorkerEnter() { if ( On ) Interlocked.Increment( ref workers ); }
	public static void WorkerExit() { if ( On ) Interlocked.Decrement( ref workers ); }

	// ---- hitch detector (main thread only)

	static long lastUploadTicks;
	static double uploadEmaMs;
	static double pendingUploadGapMs;
	static long lastDetect;
	static ulong lastFrameNumber = ulong.MaxValue;
	static long lastTimestamp;
	static readonly Event[] window = new Event[Size];
	static readonly string[] Verdicts =
	{
		"GC", "RENDER/GPU", "TEX-UPLOAD", "DECODER-LATE", "MAIN-THREAD-LATE + DECODER BURST",
		"SEGMENT-OPEN/SWAP", "WORKER-CONTENTION", "MAIN-THREAD-WORK", "UNKNOWN (engine / render thread / OS)",
	};

	static readonly int[] verdictCounts = new int[Verdicts.Length];
	static double worstGapMs;
	static int hitches;

	static void ResetDetector()
	{
		lastUploadTicks = 0;
		uploadEmaMs = 0;
		pendingUploadGapMs = 0;
		lastDetect = 0;
		lastTimestamp = 0;
		worstGapMs = 0;
		hitches = 0;
		Array.Clear( verdictCounts );
	}

	/// <summary> A picture just went to the screen texture (main thread). Tracks the gap between pictures. </summary>
	public static void Uploaded()
	{
		if ( !On ) return;
		var t = Stopwatch.GetTimestamp();
		if ( lastUploadTicks != 0 )
		{
			var gap = (t - lastUploadTicks) * MsPerTick;
			if ( gap < 1000 )
			{
				var limit = Math.Max( 25, uploadEmaMs * 2.2 );
				if ( uploadEmaMs > 0 && gap > limit && gap > pendingUploadGapMs ) pendingUploadGapMs = gap;
				uploadEmaMs = uploadEmaMs <= 0 ? gap : uploadEmaMs * 0.95 + gap * 0.05;
			}
		}
		lastUploadTicks = t;
	}

	/// <summary> Once per rendered frame, from the main thread. Records the frame and checks for a hitch. </summary>
	public static void Frame()
	{
		if ( !On ) return;
		if ( Application.FrameCount == lastFrameNumber ) return; // several players, one frame
		lastFrameNumber = Application.FrameCount;

		var now = Stopwatch.GetTimestamp();
		var frameMs = lastTimestamp == 0 ? 0 : (now - lastTimestamp) * MsPerTick;
		lastTimestamp = now;
		Write( TraceId.Frame, (int)(frameMs * 1000), 0, now );

		var textureGap = pendingUploadGapMs;
		pendingUploadGapMs = 0;
		var gapMs = Math.Max( frameMs, textureGap );
		var threshold = HitchMs;
		var isFrame = frameMs >= textureGap;
		if ( !isFrame && textureGap < Math.Min( threshold, 40 ) ) return;
		if ( isFrame && frameMs < threshold ) return;
		if ( lastDetect != 0 && (now - lastDetect) * MsPerTick < 1000 ) return; // rate limit
		lastDetect = now;

		Detect( now, gapMs, isFrame );
	}

	static void Detect( long now, double gapMs, bool isFrame )
	{
		// the hitch window: the gap itself, plus a bit before it for context
		var gapTicks = (long)(gapMs / MsPerTick);
		var from = now - gapTicks;
		var context = now - (long)(500 / MsPerTick);

		int count = 0;
		var cursor = HeadIndex();
		for ( int i = 0; i < Size && count < Size; i++ )
		{
			var e = ring[(cursor - i) & Mask];
			if ( e.Ticks == 0 || e.Ticks < context ) break;
			window[count++] = e;
		}

		double upload = 0, present = 0, updPlay = 0, mux = 0, write = 0, download = 0;
		int callbacks = 0, burst = 0, segEvents = 0, maxWorkers = workers;
		var burstFrom = now - (long)(60 / MsPerTick);
		for ( int i = 0; i < count; i++ )
		{
			var e = window[i];
			if ( e.Ticks < from ) continue;
			var id = (TraceId)(e.Id & 0xFFFF);
			switch ( id )
			{
				case TraceId.UploadEnd: upload += e.Value * MsPerTick; break;
				case TraceId.PresentEnd: present += e.Value * MsPerTick; break;
				case TraceId.UpdatePlaybackEnd: updPlay += e.Value * MsPerTick; break;
				case TraceId.TextureData: callbacks++; if ( e.Ticks >= burstFrom ) burst++; break;
				case TraceId.SegOpen: case TraceId.SegPlay: case TraceId.SegFirstFrame: case TraceId.SegSwap: case TraceId.SegClose: segEvents++; break;
			}
		}

		// worker spans overlap the gap if they ended inside it or are still running (the begin without an end is
		// enough: it started before the gap ended)
		for ( int i = 0; i < count; i++ )
		{
			var e = window[i];
			var id = (TraceId)(e.Id & 0xFFFF);
			var endsInside = e.Ticks >= from;
			if ( !endsInside ) continue;
			switch ( id )
			{
				case TraceId.MuxEnd: mux += e.Value * MsPerTick; break;
				case TraceId.WriteEnd: write += e.Value * MsPerTick; break;
				case TraceId.DownloadEnd: download += e.Value * MsPerTick; break;
			}
		}

		var gcMs = TimeSpan.FromTicks( PerformanceStats.GcPause ).TotalMilliseconds;
		var gpuMs = (double)PerformanceStats.GpuFrametime;
		var gen2 = PerformanceStats.Gen2Collections > 0;
		var ours = upload + present + updPlay;

		int verdict;
		if ( gcMs > gapMs * 0.3 || gen2 ) verdict = 0;
		else if ( isFrame && ours < gapMs * 0.25 && gpuMs > gapMs * 0.4 ) verdict = 1;
		else if ( upload > gapMs * 0.3 ) verdict = 2;
		else if ( present > gapMs * 0.3 && callbacks == 0 ) verdict = 3;
		else if ( !isFrame && callbacks <= 1 && ours < gapMs * 0.25 ) verdict = 3; // main thread fine, the only callback is the one that ended the gap
		else if ( present > gapMs * 0.3 && burst >= 3 ) verdict = 4;
		else if ( segEvents > 0 ) verdict = 5;
		else if ( (mux > 0 || write > 0) && maxWorkers >= 1 ) verdict = 6;
		else if ( updPlay > gapMs * 0.3 ) verdict = 7;
		else verdict = 8;

		hitches++;
		verdictCounts[verdict]++;
		worstGapMs = Math.Max( worstGapMs, gapMs );
		Write( TraceId.Hitch, verdict, now - from, now );

		var vram = "";
		try { vram = $", vram {GpuProfilerStats.VideoMemoryUsed / 1048576}/{GpuProfilerStats.VideoMemoryBudget / 1048576} MB"; } catch ( Exception ) { }
		var flags = (mux > 0 ? " mux-active" : "") + (write > 0 ? " write-active" : "") + (download > 0 ? " download-active" : "") + (maxWorkers > 0 ? $" workers={maxWorkers}" : "");
		Log.Info( $"[bimp trace] hitch {gapMs:0}ms ({(isFrame ? "frame" : "gap between pictures")}) -> {Verdicts[verdict]} | " +
			$"upload {upload:0.0} present {present:0.0} playback {updPlay:0.0} callbacks {callbacks} (last 60ms: {burst}) segment events {segEvents} | " +
			$"gc {gcMs:0.0}ms gen2 {gen2} gpu {gpuMs:0.0}ms frametime {PerformanceStats.FrameTime * 1000:0.0}ms{vram} |{flags}" );
	}

	static int HeadIndex() => Interlocked.CompareExchange( ref head, 0, 0 );

	[ConCmd( "bimp_trace_summary", Help = "[probe] Print (and reset) how many hitches the detector found, by probable cause" )]
	public static void Summary()
	{
		var sb = new StringBuilder();
		sb.Append( $"[bimp trace] {hitches} hitch(es), worst {worstGapMs:0}ms; events recorded {HeadIndex() + 1}" );
		for ( int i = 0; i < verdictCounts.Length; i++ )
			if ( verdictCounts[i] > 0 ) sb.Append( $"; {Verdicts[i]} x{verdictCounts[i]}" );
		Log.Info( sb.ToString() );
		hitches = 0;
		worstGapMs = 0;
		Array.Clear( verdictCounts );
	}

	/// <summary> Write the ring to <c>lab/trace_NAME.csv</c> in FileSystem.Data: time in ms since the first event, thread, event, arg, value. </summary>
	[ConCmd( "bimp_trace_dump", Help = "[probe] Write the recorded event timeline to lab/trace_NAME.csv (default name: the time)" )]
	public static void Dump( string name = null )
	{
		var last = HeadIndex();
		var count = Math.Min( last + 1, Size );
		if ( count <= 0 ) { Log.Info( "[bimp trace] nothing recorded (set bimp_trace 1 first)" ); return; }

		var copy = new Event[count];
		for ( int i = 0; i < count; i++ ) copy[i] = ring[(last - i) & Mask];
		var file = $"lab/trace_{(string.IsNullOrWhiteSpace( name ) ? DateTime.Now.ToString( "HHmmss" ) : name)}.csv";

		_ = GameTask.RunInThreadAsync( () =>
		{
			var events = copy.Where( e => e.Ticks != 0 ).OrderBy( e => e.Ticks ).ToArray();
			if ( events.Length == 0 ) return;
			var t0 = events[0].Ticks;
			var sb = new StringBuilder( events.Length * 40 );
			sb.AppendLine( "ms,thread,event,arg,value_ms" );
			foreach ( var e in events )
			{
				var id = (TraceId)(e.Id & 0xFFFF);
				var isEnd = id.ToString().EndsWith( "End" );
				var value = id == TraceId.PlaybackTime ? e.Value.ToString() : isEnd ? (e.Value * MsPerTick).ToString( "0.000" ) : e.Value != 0 ? (e.Value * MsPerTick).ToString( "0.000" ) : "";
				sb.Append( ((e.Ticks - t0) * MsPerTick).ToString( "0.000" ) ).Append( ',' ).Append( e.Id >> 16 ).Append( ',' ).Append( id ).Append( ',' ).Append( e.Arg ).Append( ',' ).AppendLine( value );
			}
			FileSystem.Data.CreateDirectory( "lab" );
			FileSystem.Data.WriteAllText( file, sb.ToString() );
			Log.Info( $"[bimp trace] wrote {file} ({events.Length} events, {(events[^1].Ticks - t0) * MsPerTick / 1000:0.0}s)" );
		} );
	}
}
