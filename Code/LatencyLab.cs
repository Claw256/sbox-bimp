using System.IO;
using System.Text;
using Bimp.Resolver.Live;

namespace Bimp;

/// <summary>
/// [probe] Experiments on the engine's H.264 <see cref="VideoPlayer"/> for low-latency live playback: what it holds
/// back at the end of a file, whether filler frames flush it, whether it plays a file that's still being written, and
/// what JPEG decoding costs. Test streams (raw H.264 with frame-number blocks in the top-left corner) live in
/// FileSystem.Data "lab/".
/// </summary>
public static class LatencyLab
{
	sealed class Au
	{
		public List<byte[]> Nals = new();
		public bool Key;
	}

	static readonly Dictionary<string, (List<Au> aus, byte[] sps, byte[] pps)> cache = new();

	static (List<Au> aus, byte[] sps, byte[] pps) Load( string file )
	{
		if ( cache.TryGetValue( file, out var c ) ) return c;
		var data = FileSystem.Data.ReadAllBytes( $"lab/{file}" ).ToArray();
		var aus = new List<Au>();
		byte[] sps = null, pps = null;
		Au cur = null;
		var curHasSlice = false;
		foreach ( var nal in H264.SplitAnnexB( data, 0, data.Length ) )
		{
			var t = H264.NalType( nal );
			if ( t == H264.NalSps ) sps ??= nal;
			if ( t == H264.NalPps ) pps ??= nal;
			var firstSlice = t is 1 or 5 && nal.Length > 1 && (nal[1] & 0x80) != 0;
			if ( cur is null || (curHasSlice && (firstSlice || t is 6 or 7 or 8 or 9)) )
			{
				cur = new Au();
				aus.Add( cur );
				curHasSlice = false;
			}
			cur.Nals.Add( nal );
			if ( t is 1 or 5 ) curHasSlice = true;
			if ( t == 5 ) cur.Key = true;
		}
		return cache[file] = (aus, sps, pps);
	}

	/// <summary> The frame number burned into the picture: two 64x64 blocks, luma (n % 16) * 14 + 20. </summary>
	static int FrameId( ReadOnlySpan<byte> rgba, int w, out int r0, out int r1 )
	{
		r0 = rgba[(32 * w + 32) * 4];
		r1 = rgba[(32 * w + 96) * 4];
		static int Digit( int r )
		{
			var y = r * 219 / 255.0 + 16; // limited range (what the decoder's RGB conversion assumes)
			return Math.Clamp( (int)Math.Round( (y - 20) / 14 ), 0, 15 );
		}
		return Digit( r0 ) + 16 * Digit( r1 );
	}

	//
	// running experiment
	//

	static VideoPlayer player;
	static Scene scene;
	static double openedAt;
	static double stopAt;
	static string name;
	static readonly List<(double t, int id, float playback)> delivered = new();
	static readonly StringBuilder calib = new();
	static double finishedAt = -1;
	static Action<double> onTick;
	static Func<string> extraReport;
	static ulong lastTick;
	static double presentMax;
	static int slowPresents;

	static void Begin( string label, string path, double seconds )
	{
		End( false );
		name = label;
		delivered.Clear();
		calib.Clear();
		finishedAt = -1;
		presentMax = 0;
		slowPresents = 0;
		scene = Game.ActiveScene;
		player = new VideoPlayer();
		player.OnFinished += () => finishedAt = RealTime.NowDouble - openedAt;
		player.OnTextureData = ( span, size ) =>
		{
			var id = FrameId( span, (int)size.x, out var r0, out var r1 );
			if ( delivered.Count < 4 ) calib.Append( $"[{r0},{r1}->{id}] " );
			delivered.Add( (RealTime.NowDouble - openedAt, id, player?.PlaybackTime ?? 0) );
		};
		player.Audio.Volume = 0;
		openedAt = RealTime.NowDouble;
		stopAt = openedAt + seconds;
		player.Play( FileSystem.Data, path );
	}

	static void End( bool report = true )
	{
		if ( player is null ) return;
		if ( report ) Log.Info( $"[bimp lab] {name}\n{Report()}" );
		player.Dispose();
		player = null;
		onTick = null;
		extraReport = null;
		if ( report && repeats.Count > 0 ) repeats.Dequeue()();
		else if ( !report ) repeats.Clear();
	}

	static readonly Queue<Action> repeats = new();

	[ConCmd( "bimp_lab_segn", Help = "[probe] bimp_lab_seg, N times in a row" )]
	public static void SegRepeatCmd( int count, string file = "id720.h264", int from = 0, int frames = 30, string variant = "plain" )
	{
		repeats.Clear();
		for ( int i = 1; i < count; i++ ) repeats.Enqueue( () => SegCmd( file, from, frames, variant ) );
		SegCmd( file, from, frames, variant );
	}

	static string Report()
	{
		var sb = new StringBuilder();
		sb.AppendLine( $"calibration {calib}" );
		if ( delivered.Count == 0 ) { sb.AppendLine( $"no frames delivered, OnFinished {finishedAt:0.00}, PlaybackTime {player?.PlaybackTime:0.000}, duration {player?.Duration:0.00}" ); if ( extraReport is not null ) sb.AppendLine( extraReport() ); return sb.ToString(); }
		sb.AppendLine( $"first frame {delivered[0].t * 1000:0}ms after Play(), {delivered.Count} frames, last at {delivered[^1].t * 1000:0}ms (playback {delivered[^1].playback:0.000}), OnFinished {(finishedAt < 0 ? "never" : $"{finishedAt * 1000:0}ms")}, final PlaybackTime {player?.PlaybackTime:0.000}, Present() max {presentMax:0.0}ms, >4ms {slowPresents}" );

		// ids as runs: 0-29 30 30 31-40
		var runs = new List<string>();
		int start = delivered[0].id, prev = start;
		for ( int i = 1; i <= delivered.Count; i++ )
		{
			var id = i < delivered.Count ? delivered[i].id : -99;
			if ( id == ((prev + 1) & 255) ) { prev = id; continue; }
			runs.Add( start == prev ? $"{start}" : $"{start}-{prev}" );
			start = prev = id;
		}
		sb.AppendLine( $"ids: {string.Join( " ", runs )}" );
		sb.AppendLine( $"id@ms: {string.Join( " ", delivered.Take( 45 ).Select( d => $"{d.id}@{d.t * 1000:0}" ) )}" );
		if ( extraReport is not null ) sb.AppendLine( extraReport() );
		return sb.ToString();
	}

	/// <summary> Called every frame (from the media players). </summary>
	internal static void Tick()
	{
		if ( player is null || lastTick == Application.FrameCount ) return;
		lastTick = Application.FrameCount;
		if ( Game.ActiveScene != scene ) { End( false ); return; }
		var now = RealTime.NowDouble;
		onTick?.Invoke( now - openedAt );
		var ps = System.Diagnostics.Stopwatch.GetTimestamp();
		player.Present();
		var pms = System.Diagnostics.Stopwatch.GetElapsedTime( ps ).TotalMilliseconds;
		presentMax = Math.Max( presentMax, pms );
		if ( pms > 4 ) slowPresents++;
		if ( now > stopAt ) End();
	}

	static List<VideoSample> Samples( List<Au> aus, int from, int count )
	{
		var list = new List<VideoSample>();
		for ( int i = 0; i < count && from + i < aus.Count; i++ )
			list.Add( new VideoSample { Dts = i * 3000L, Pts = i * 3000L, Key = aus[from + i].Key, Data = H264.ToAvcc( aus[from + i].Nals ) } );
		return list;
	}

	/// <summary>
	/// E1: one file of <paramref name="frames"/> frames from <paramref name="from"/> (a keyframe), no overlap, with
	/// filler after its real end: "plain", "dup:K" (last frame repeated), "skip:K" (all-skip P frames).
	/// "seek:S" seeks to S seconds right after opening.
	/// </summary>
	[ConCmd( "bimp_lab_seg", Help = "[probe] Play one segment built from a lab stream: file from frames variant" )]
	public static void SegCmd( string file = "id720.h264", int from = 0, int frames = 30, string variant = "plain" )
	{
		var (aus, sps, pps) = Load( file );
		var info = H264.ParseSps( sps );
		var v = Samples( aus, from, frames );
		float seekTo = -1;
		foreach ( var part in variant.Split( ',' ) )
		{
			var parts = part.Split( ':' );
			var k = parts.Length > 1 && int.TryParse( parts[1], out var n ) ? n : 0;
			if ( parts[0] == "seek" && parts.Length > 1 && float.TryParse( parts[1], out var s ) ) seekTo = s;
			else if ( parts[0] == "shift" )
			{
				// presentation times k ms later than decode times (a composition offset on every frame)
				foreach ( var sample in v ) sample.Pts = sample.Dts + k * 90L;
			}
			else if ( parts[0] == "fast" )
			{
				// the first k frames squeezed into k ticks: decoded straight away, never shown
				var step = parts.Length > 2 && int.TryParse( parts[2], out var ms ) ? ms * 90L : 1;
				for ( int i = 0; i < v.Count; i++ ) v[i].Dts = v[i].Pts = i < k ? i * step : k * step + (i - k) * 3000L;
			}
			else if ( parts[0] == "dup" )
			{
				for ( int i = 0; i < k; i++ )
					v.Add( new VideoSample { Dts = v[^1].Dts + 3000, Pts = v[^1].Dts + 3000, Data = v[^1].Data } );
			}
			else if ( parts[0] == "skip" )
			{
				// carry on from the last real frame's slice header
				H264.SliceInfo last = default;
				foreach ( var au in aus.Skip( from ).Take( frames ) )
					foreach ( var nal in au.Nals )
						if ( H264.ParseSlice( nal, info ) is { } si ) { last = si; break; }
				const int ppsId = 1;
				for ( int i = 0; i < k; i++ )
				{
					var nals = new List<byte[]>();
					if ( i == 0 ) nals.Add( H264.SkipPps( info, ppsId ) );
					nals.Add( H264.SkipFrame( info, ppsId, ref last ) );
					v.Add( new VideoSample { Dts = v[^1].Dts + 3000, Pts = v[^1].Dts + 3000, Data = Avcc( nals ) } );
				}
			}
		}

		const string path = "lab/out_seg.mp4";
		using ( var f = FileSystem.Data.OpenWrite( path ) ) Mp4Writer.Write( f, 0, v, sps, pps, null, null );
		Begin( $"seg {file} {from}+{frames} {variant} ({v.Count} samples)", path, frames / 30.0 + 2.5 );
		if ( seekTo >= 0 ) player.Seek( seekTo );
	}

	/// <summary> NAL units as AVCC, keeping parameter sets (H264.ToAvcc drops them). </summary>
	static byte[] Avcc( List<byte[]> nals )
	{
		var ms = new MemoryStream();
		foreach ( var nal in nals )
		{
			ms.WriteByte( (byte)(nal.Length >> 24) ); ms.WriteByte( (byte)(nal.Length >> 16) );
			ms.WriteByte( (byte)(nal.Length >> 8) ); ms.WriteByte( (byte)nal.Length );
			ms.Write( nal );
		}
		return ms.ToArray();
	}

	/// <summary> Pad an AVCC sample to exactly <paramref name="size"/> bytes with a filler-data NAL unit (type 12). </summary>
	static byte[] Pad( byte[] avcc, int size )
	{
		var result = new byte[size];
		avcc.CopyTo( result, 0 );
		var fillerLength = size - avcc.Length - 4;
		var at = avcc.Length;
		result[at] = (byte)(fillerLength >> 24); result[at + 1] = (byte)(fillerLength >> 16);
		result[at + 2] = (byte)(fillerLength >> 8); result[at + 3] = (byte)fillerLength;
		result[at + 4] = 0x0C;
		for ( int i = at + 5; i < size - 1; i++ ) result[i] = 0xFF;
		result[size - 1] = 0x80;
		return result;
	}

	/// <summary>
	/// E2: a growing file. The moov declares every frame (padded to one fixed size), <paramref name="pre"/> frames are
	/// written before Play(), the rest appended in real time. <paramref name="gapAt"/> > 0 stops appending for
	/// <paramref name="gapSeconds"/> at that frame (an underrun).
	/// </summary>
	[ConCmd( "bimp_lab_grow", Help = "[probe] Play a file while it's being written: file pre frames [gapAt gapSeconds]" )]
	public static void GrowCmd( string file = "id720.h264", int pre = 15, int frames = 300, int gapAt = 0, float gapSeconds = 1, bool prealloc = false )
	{
		var (aus, sps, pps) = Load( file );
		var v = Samples( aus, 0, frames );
		var slot = (v.Max( s => s.Data.Length ) + 64 + 15) / 16 * 16;
		foreach ( var s in v ) s.Data = Pad( s.Data, slot );

		var ms = new MemoryStream();
		Mp4Writer.Write( ms, 0, v, sps, pps, null, null );
		var bytes = ms.ToArray();
		var dataStart = bytes.Length - v.Count * slot;

		const string path = "lab/out_grow.mp4";
		using ( var f = FileSystem.Data.OpenWrite( path ) )
		{
			f.Write( bytes, 0, dataStart + pre * slot );
			// prealloc: the file already has its full length (zeros), frames are written into place
			if ( prealloc ) f.Write( new byte[(v.Count - pre) * slot] );
		}

		var appended = new double[v.Count];
		for ( int i = 0; i < pre; i++ ) appended[i] = 0;
		var next = pre;
		string appendError = null;
		var writeMs = new List<double>();

		Begin( $"grow {file} pre {pre} frames {v.Count} slot {slot}B{(gapAt > 0 ? $" gap {gapSeconds}s at {gapAt}" : "")}{(prealloc ? " prealloc" : "")}", path, frames / 30.0 + 3 );
		onTick = t =>
		{
			while ( next < v.Count && appendError is null )
			{
				var due = (next - pre) / 30.0 + (gapAt > 0 && next >= gapAt ? gapSeconds : 0);
				if ( t < due ) break;
				var sw = System.Diagnostics.Stopwatch.StartNew();
				try
				{
					using var f = FileSystem.Data.OpenWrite( path, prealloc ? FileMode.Open : FileMode.Append );
					if ( prealloc ) f.Seek( dataStart + (long)next * slot, SeekOrigin.Begin );
					f.Write( bytes, dataStart + next * slot, slot );
				}
				catch ( Exception e ) { appendError = e.Message; break; }
				writeMs.Add( sw.Elapsed.TotalMilliseconds );
				appended[next++] = t;
			}
		};
		extraReport = () =>
		{
			// delivered id -> appended time: the delay from the file having a frame to it coming out of the decoder
			var lat = new List<double>();
			var seen = 0;
			foreach ( var d in delivered )
			{
				// unwrap the 8 bit id against the count so far
				var id = d.id + 256 * ((seen - d.id + 128) / 256);
				seen = id;
				if ( id >= pre && id < next ) lat.Add( (d.t - appended[id]) * 1000 );
			}
			lat.Sort();
			var summary = lat.Count > 0 ? $"append->deliver median {lat[lat.Count / 2]:0}ms min {lat[0]:0}ms max {lat[^1]:0}ms over {lat.Count} frames" : "no appended frame delivered";
			return $"appended {next}/{v.Count}{(appendError is not null ? $" APPEND FAILED: {appendError}" : "")}, write avg {(writeMs.Count > 0 ? writeMs.Average() : 0):0.00}ms; {summary}";
		};
	}

	/// <summary> E3: JPEG decode (worker) and upload (main thread) cost. </summary>
	[ConCmd( "bimp_lab_jpeg", Help = "[probe] Time JPEG decode + texture upload: file count" )]
	public static async void JpegCmd( string file = "f1080.jpg", int count = 30 )
	{
		var data = FileSystem.Data.ReadAllBytes( $"lab/{file}" ).ToArray();
		var times = new List<double>();
		Bitmap bmp = null;
		await GameTask.RunInThreadAsync( () =>
		{
			for ( int i = 0; i < count; i++ )
			{
				var sw = System.Diagnostics.Stopwatch.StartNew();
				bmp?.Dispose();
				bmp = Bitmap.CreateFromBytes( data );
				times.Add( sw.Elapsed.TotalMilliseconds );
			}
		} );
		await GameTask.MainThread();
		var mainDecode = System.Diagnostics.Stopwatch.StartNew();
		using ( var b = Bitmap.CreateFromBytes( data ) ) { }
		var mainMs = mainDecode.Elapsed.TotalMilliseconds;

		var tex = Texture.Create( bmp.Width, bmp.Height, ImageFormat.RGBA8888 ).WithDynamicUsage().Finish();
		var up = new List<double>();
		for ( int i = 0; i < count; i++ )
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			tex.Update( bmp );
			up.Add( sw.Elapsed.TotalMilliseconds );
		}
		times.Sort();
		up.Sort();
		Log.Info( $"[bimp lab] jpeg {file} {bmp.Width}x{bmp.Height} {data.Length / 1024}KB: worker decode median {times[times.Count / 2]:0.0}ms max {times[^1]:0.0}ms, main-thread decode {mainMs:0.0}ms, upload median {up[up.Count / 2]:0.00}ms max {up[^1]:0.00}ms" );
		tex.Dispose();
		bmp.Dispose();
	}
}
