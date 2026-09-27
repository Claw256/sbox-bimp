using System.IO;
using System.Threading;

namespace Bimp.Resolver.Media;

/// <summary>
/// A finished segment file, ready to play.
/// </summary>
public sealed class MediaSegment
{
	public int Seq;

	/// <summary> Path in <see cref="FileSystem.Data"/>. </summary>
	public string Path;

	/// <summary> Media time (seconds) of the first frame. </summary>
	public float Start;

	/// <summary> Media time (seconds) at which the next segment starts. </summary>
	public float End;

	public bool Last;

	/// <summary> Live: stream time (seconds) at which this segment's clock reads 0. </summary>
	public double StreamStart;

	/// <summary> Live: when the file was finished (<see cref="RealTime.NowDouble"/>). </summary>
	public double ReadyAt;

	/// <summary>
	/// Live: how much of the next segment the file carries after <see cref="End"/> - it plays on into it while the
	/// next player comes up. 0 = it ends at End (low latency H.264, ending in skip frames).
	/// </summary>
	public float Overlap;
}

/// <summary>
/// Turns a separate video WebM + audio WebM into a series of standalone WebM segment files in
/// <see cref="FileSystem.Data"/>, starting at a given time. Each segment is a run of whole video clusters (cue
/// point to cue point) plus the audio for the same span, downloaded with range requests - so playback starts
/// after the first few seconds are fetched, not the whole file. Timestamps stay absolute.
/// <para>
/// Audio only (no video source) writes one file from the cue point before the start time to the end.
/// </para>
/// </summary>
public sealed class WebmSegmenter : ISegmentFeed, IDisposable
{
	/// <summary> The first segment is short so playback starts quickly. </summary>
	const float FirstSegmentSeconds = 8;
	const float SegmentSeconds = 40;

	/// <summary> Audio only files use short clusters, which is also their seek granularity. </summary>
	const long AudioOnlyClusterMs = 5000;

	/// <summary>
	/// A segment's audio runs this far past its end, so it keeps playing while the next segment's player starts - that
	/// one stays silent until its first frame is up: ~0.1 s at 1080p, 0.4-2.6 s for 4K VP9 (measured).
	/// </summary>
	const double AudioTailSeconds = 3.0;

	/// <summary>
	/// A segment's video runs this far into the next one, so its player shows real frames while the next player comes
	/// up. A new player's clock is in step when it's opened ~50 ms early, but its first frame at 1440p/4K comes 150-520 ms
	/// after that (measured) - without this the picture held for that long at every swap.
	/// </summary>
	const double VideoOverlapSeconds = 1.0;

	/// <summary>
	/// YouTube's AV1 urls stop answering after ~88 MB for the clients that get them (measured): plan the AV1 part of
	/// playback to stay under this (counting what the session already downloaded), then carry on with the switch video.
	/// </summary>
	const long Av1BudgetBytes = 80L * 1024 * 1024;

	readonly WebmSource video;
	readonly WebmSource audio;
	readonly WebmSource switchVideo;
	readonly string directory;
	readonly CancellationTokenSource cts = new();

	/// <summary>
	/// Each segment: its video source, its cue range, and - for the last AV1 one - the time it ends at instead of its
	/// last cue (the switch video's first keyframe; the file runs on past it while that one's player comes up).
	/// </summary>
	readonly List<(WebmSource src, int first, int end, long? endAt)> ranges = new();
	readonly Dictionary<int, Task<MediaSegment>> building = new();
	readonly double duration;
	bool disposed;

	/// <summary> Something went wrong building a segment. </summary>
	public string Error { get; private set; }

	public int Count => video is null ? 1 : ranges.Count;

	bool ISegmentFeed.IsLive => false;
	int ISegmentFeed.Newest => building.Where( kv => kv.Value.IsCompletedSuccessfully && kv.Value.Result is not null ).Select( kv => kv.Key ).DefaultIfEmpty( -1 ).Max();
	void ISegmentFeed.Want( int seq ) => _ = GetAsync( seq );

	/// <param name="maxLagSeconds">Start at the cue point before <paramref name="startSeconds"/> if it's at most this far behind, else at the next one.</param>
	/// <param name="switchTo">Carry on with this video once the AV1 budget is used (see <see cref="Av1BudgetBytes"/>).</param>
	public WebmSegmenter( WebmSource video, WebmSource audio, float startSeconds, string directory, float maxLagSeconds = float.MaxValue, WebmSource switchTo = null )
	{
		if ( audio is null ) throw new ArgumentNullException( nameof( audio ) );
		if ( video is not null && video.TimecodeScale != audio.TimecodeScale )
			throw new MergeException( "video and audio use different timecode scales" );

		this.video = video;
		this.audio = audio;
		switchVideo = switchTo is not null && switchTo.TimecodeScale == audio.TimecodeScale ? switchTo : null;
		this.directory = directory;
		duration = Math.Max( video?.Duration ?? 0, audio.Duration );

		FileSystem.Data.CreateDirectory( directory );

		if ( video is not null ) PlanRanges( startSeconds, maxLagSeconds );
		else ranges.Add( (null, audio.IndexAt( audio.TimeOf( startSeconds ) ), audio.Cues.Count, null) );
	}

	void PlanRanges( float startSeconds, float maxLagSeconds )
	{
		var src = video;
		var cues = video.Cues;
		var i = video.IndexAt( video.TimeOf( startSeconds ) );
		if ( i + 1 < cues.Count && startSeconds - video.SecondsOf( cues[i].Time ) > maxLagSeconds ) i++;
		var target = FirstSegmentSeconds;
		var budget = switchVideo is null ? long.MaxValue : Av1BudgetBytes - video.File.BytesRead;

		// what a segment downloads, with its overlap into the next one
		long Cost( int i, int j ) => VideoOverlap( video, i, j, j < video.Cues.Count ? video.Cues[j].Time : long.MaxValue, j >= video.Cues.Count ).readEnd - video.ClusterStart( i );

		while ( i < cues.Count )
		{
			var j = i + 1;
			while ( j < cues.Count && src.SecondsOf( cues[j].Time - cues[i].Time ) < target ) j++;

			if ( src == video && switchVideo is not null && Cost( i, j ) > budget )
			{
				// as much AV1 as still fits, then the switch video from its first keyframe at or after there
				while ( j > i + 1 && Cost( i, j ) > budget ) j--;
				if ( Cost( i, j ) <= budget )
				{
					budget -= Cost( i, j );
					ranges.Add( (src, i, j, null) );
					i = j;
					if ( i >= cues.Count ) break;
				}
				var k = SwitchIndex( cues[i].Time );
				if ( k < 0 ) break;
				EndAtSwitch( switchVideo.Cues[k].Time );
				(src, cues, i, target) = (switchVideo, switchVideo.Cues, k, FirstSegmentSeconds);
				Log.Info( ranges.Count == 0
					? "[bimp] this session's AV1 allowance is used up (YouTube stops serving it after ~88 MB) - playing the VP9 copy"
					: $"[bimp] AV1 until {video.SecondsOf( cues[k].Time ):0.0}s (YouTube stops serving it after ~88 MB), then the VP9 copy" );
				continue;
			}

			if ( src == video ) budget -= Cost( i, j );
			ranges.Add( (src, i, j, null) );
			i = j;
			target = SegmentSeconds;
		}
	}

	/// <summary> The switch video's first keyframe at or after a time, or the last one before it; -1 if it has none. </summary>
	int SwitchIndex( long time )
	{
		var k = switchVideo.FirstIndexFrom( time );
		if ( k >= switchVideo.Cues.Count ) k = switchVideo.Cues.Count - 1;
		return k;
	}

	/// <summary>
	/// The last AV1 segment ends where the switch video starts: its file runs on past that like any other (see
	/// <see cref="VideoOverlap"/>), but its clock hands over there.
	/// </summary>
	void EndAtSwitch( long switchTime )
	{
		if ( ranges.Count == 0 || ranges[^1].src != video ) return;
		var (src, first, end, _) = ranges[^1];
		ranges[^1] = (src, first, end, switchTime);
	}

	/// <summary>
	/// The video a segment ending at <paramref name="t1"/> carries: entries <paramref name="first"/> to kEnd, cut at
	/// cut (<see cref="VideoOverlapSeconds"/> past t1), and the bytes to read for them. Only the start of the cluster
	/// the cut falls in is read (its keyframe plus a margin), not the whole keyframe interval.
	/// </summary>
	static (int kEnd, long cut, long readEnd) VideoOverlap( WebmSource src, int first, int end, long t1, bool last )
	{
		var cues = src.Cues;
		if ( last || t1 == long.MaxValue ) return (end, long.MaxValue, src.ClusterEnd( end - 1 ));

		var cut = t1 + src.TimeOf( VideoOverlapSeconds );
		var k = Math.Max( end - 1, src.IndexAt( cut - 1 ) );
		var kEnd = k + 1;
		var clusterEnd = k + 1 < cues.Count ? cues[k + 1].Time : long.MaxValue;
		if ( clusterEnd <= cut ) return (kEnd, cut, src.ClusterEnd( k ));

		var span = clusterEnd == long.MaxValue ? src.TimeOf( SegmentSeconds ) : clusterEnd - cues[k].Time;
		var bytes = src.ClusterEnd( k ) - src.ClusterStart( k );
		var fraction = Math.Clamp( (cut - cues[k].Time) / (double)Math.Max( span, 1 ), 0, 1 );
		var readEnd = src.ClusterStart( k ) + (long)(bytes * fraction * 1.5) + 512 * 1024;
		return (kEnd, cut, Math.Min( readEnd, src.ClusterEnd( k ) ));
	}

	/// <summary>
	/// An AV1 segment couldn't be downloaded (the url stopped answering early): it and everything after it come from
	/// the switch video instead, from its keyframe at or before the segment's start.
	/// </summary>
	bool SwitchFrom( int seq )
	{
		if ( switchVideo is null || seq >= ranges.Count || ranges[seq].src != video ) return false;
		var from = video.Cues[ranges[seq].first].Time;
		ranges.RemoveRange( seq, ranges.Count - seq );
		var cues = switchVideo.Cues;
		var i = switchVideo.IndexAt( from );
		var target = FirstSegmentSeconds;
		while ( i < cues.Count )
		{
			var j = i + 1;
			while ( j < cues.Count && switchVideo.SecondsOf( cues[j].Time - cues[i].Time ) < target ) j++;
			ranges.Add( (switchVideo, i, j, null) );
			i = j;
			target = SegmentSeconds;
		}
		Log.Info( $"[bimp] AV1 video stopped answering - carrying on with VP9 from {switchVideo.SecondsOf( cues[ranges[seq].first].Time ):0.0}s" );
		return true;
	}

	/// <summary>
	/// The segment, building it (and everything before it) if needed. Also starts on the one after it.
	/// </summary>
	public Task<MediaSegment> GetAsync( int seq )
	{
		if ( seq < 0 || seq >= Count ) return Task.FromResult<MediaSegment>( null );

		var task = Build( seq );
		if ( seq + 1 < Count ) Build( seq + 1 );
		return task;
	}

	/// <summary> The segment if it's finished, else null. </summary>
	public MediaSegment TryGet( int seq )
	{
		return building.TryGetValue( seq, out var t ) && t.IsCompletedSuccessfully ? t.Result : null;
	}

	Task<MediaSegment> Build( int seq )
	{
		if ( building.TryGetValue( seq, out var existing ) ) return existing;

		// one at a time, in order - the network is the bottleneck, and the next one is needed first
		var previous = seq > 0 && building.TryGetValue( seq - 1, out var p ) ? p : Task.FromResult<MediaSegment>( null );
		var task = BuildAfter( previous, seq );
		building[seq] = task;
		return task;
	}

	async Task<MediaSegment> BuildAfter( Task previous, int seq )
	{
		try { await previous; } catch { /* reported by its own task */ }

		try
		{
			var segment = video is null ? await BuildAudioOnly() : await BuildMergedOrSwitch( seq );
			if ( disposed ) { Delete( segment.Path ); return null; }
			return segment;
		}
		catch ( OperationCanceledException )
		{
			return null;
		}
		catch ( Exception e )
		{
			Error ??= e.Message;
			Log.Warning( $"[bimp] Couldn't build segment {seq}: {e.Message}" );
			throw;
		}
	}

	async Task<MediaSegment> BuildMergedOrSwitch( int seq )
	{
		try
		{
			return await BuildMerged( seq );
		}
		catch ( FetchException e ) when ( SwitchFrom( seq ) )
		{
			Log.Trace( $"[bimp] segment {seq}: {e.Message}" );
			return await BuildMerged( seq );
		}
	}

	async Task<MediaSegment> BuildMerged( int seq )
	{
		var ct = cts.Token;
		var (video, first, end, endAt) = ranges[seq];
		var cues = video.Cues;

		var t0 = cues[first].Time;
		var t1 = endAt ?? (end < cues.Count ? cues[end].Time : long.MaxValue);
		var last = end >= cues.Count && endAt is null;

		// the video clusters, byte for byte, running on into the next segment (see VideoOverlapSeconds)
		var (kEnd, cut, vEnd) = VideoOverlap( video, first, end, t1, last );
		var vStart = video.ClusterStart( first );
		var videoTask = video.File.ReadAsync( vStart, vEnd, ct );

		// the audio clusters covering [t0, t1 + tail)
		var tail = t1 == long.MaxValue ? long.MaxValue : t1 + audio.TimeOf( AudioTailSeconds );
		var ai = audio.IndexAt( t0 );
		var aj = tail == long.MaxValue ? audio.Cues.Count : audio.FirstIndexFrom( tail );
		var aStart = audio.ClusterStart( ai );
		var aEnd = aj < audio.Cues.Count ? audio.ClusterStart( aj ) : audio.SegmentEnd;
		var audioTask = audio.File.ReadAsync( aStart, aEnd, ct );

		MediaProbe.Note( $"seg {seq}: downloading" );
		var videoBytes = await videoTask;
		var audioBytes = await audioTask;
		MediaProbe.Note( $"seg {seq}: downloaded {(videoBytes.Length + audioBytes.Length) / 1048576.0:0} MB" );

		// Parsing the audio and assembling the entries copies megabytes of clusters - done on a worker thread with
		// the write, so the main thread (and every player's Present()) never waits on it.
		var vtrack = video.TrackNumber;
		ulong atrack = vtrack != 1 ? 1UL : 2UL;
		var path = $"{directory}/seg_{seq}.webm";
		long firstBlockTime = t0;
		long lastVideo = t0;

		await GameTask.RunInThreadAsync( () =>
		{
			var blocks = WebmMux.ParseBlocks( audioBytes, 0, audioBytes.Length );
			if ( blocks.Count > 0 ) firstBlockTime = blocks[0].Time;

			// audio from before the very first video cue goes in front of it
			byte[] pre = null;
			if ( first == 0 )
			{
				var early = blocks.Where( b => b.Time < t0 ).ToList();
				if ( early.Count > 0 ) pre = Ebml.Concat( WebmMux.Clusters( early, atrack ).Select( c => c.cluster ) );
			}

			// Each entry: the video clusters from a cue point, with the audio for the same time woven in among their blocks.
			// The engine reads a file in order and only holds a few seconds of packets: audio written after a whole
			// keyframe interval of video (5-7 s, up to 18 MB at 4K) reached it too late, and it went silent for 1-3 s at a
			// time (measured). Clusters still start at keyframes - the engine's forward seek lands on any cluster.
			var entries = new List<MuxEntry>();
			var bi = 0;
			while ( bi < blocks.Count && blocks[bi].Time < t0 ) bi++;

			for ( int k = first; k < kEnd; k++ )
			{
				var entry = new MuxEntry { Time = cues[k].Time };
				var from = (int)(video.ClusterStart( k ) - vStart);
				var to = (int)Math.Min( video.ClusterEnd( k ) - vStart, videoBytes.Length );
				var clusters = videoBytes;
				if ( video.IsMp4 )
				{
					// AV1 from a fragmented MP4: its fragments become clusters first (the last may be cut off)
					clusters = video.ClustersFromMp4( videoBytes, from, to, vStart, partial: k == kEnd - 1 );
					from = 0;
					to = clusters.Length;
				}

				// the last entry's audio runs on past the end (see AudioTailSeconds)
				var next = k + 1 < cues.Count ? cues[k + 1].Time : long.MaxValue;
				var until = k + 1 < kEnd ? next : tail;
				var group = new List<MediaBlock>();
				while ( bi < blocks.Count && blocks[bi].Time < until ) group.Add( blocks[bi++] );
				var parts = WebmMux.Interleave( clusters, from, to, Math.Min( next, cut ), group, atrack, cut, ref lastVideo );
				if ( parts.Count == 0 ) continue;
				entry.Parts.AddRange( parts );

				entries.Add( entry );
			}

			var tracks = Ebml.Make( Ebml.Tracks, Ebml.Concat( video.TrackEntry, WebmMux.FixMonoOpus( WebmMux.RenumberTrack( audio.TrackEntry, atrack, 0x6A696D6200000000UL | atrack ) ) ) );
			using var file = FileSystem.Data.OpenWrite( path );
			WebmMux.Write( file, video.EbmlHeader, video.TimecodeScale, duration, tracks, vtrack, entries, pre );
		} );
		ct.ThrowIfCancellationRequested();
		MediaProbe.Note( $"seg {seq}: written" );

		return new MediaSegment
		{
			Seq = seq,
			Path = path,
			Start = (float)video.SecondsOf( first == 0 ? Math.Min( t0, firstBlockTime ) : t0 ),
			End = (float)(t1 == long.MaxValue ? video.SecondsOf( (long)duration ) : video.SecondsOf( t1 )),
			Last = last,
			Overlap = last ? 0 : (float)Math.Max( video.SecondsOf( lastVideo - t1 ), 0 ),
		};
	}

	async Task<MediaSegment> BuildAudioOnly()
	{
		var ct = cts.Token;
		var (_, first, _, _) = ranges[0];
		var start = audio.ClusterStart( first );
		var data = await audio.File.ReadAsync( start, audio.SegmentEnd, ct );

		var track = audio.TrackNumber;
		var path = $"{directory}/audio.webm";
		long firstTime = 0;

		await GameTask.RunInThreadAsync( () =>
		{
			var blocks = WebmMux.ParseBlocks( data, 0, data.Length );
			if ( blocks.Count == 0 ) throw new MergeException( "no audio blocks" );
			firstTime = blocks[0].Time;

			var entries = WebmMux.Clusters( blocks, track, AudioOnlyClusterMs )
				.Select( c => new MuxEntry { Time = c.time, Parts = { c.cluster } } )
				.ToList();

			var tracks = Ebml.Make( Ebml.Tracks, WebmMux.FixMonoOpus( audio.TrackEntry ) );
			using var file = FileSystem.Data.OpenWrite( path );
			WebmMux.Write( file, audio.EbmlHeader, audio.TimecodeScale, duration, tracks, track, entries, null );
		} );
		ct.ThrowIfCancellationRequested();

		return new MediaSegment
		{
			Seq = 0,
			Path = path,
			Start = (float)audio.SecondsOf( firstTime ),
			End = (float)audio.SecondsOf( (long)duration ),
			Last = true,
		};
	}

	/// <summary>
	/// Done with a segment - delete its file.
	/// </summary>
	public void Release( int seq )
	{
		var s = TryGet( seq );
		if ( s is not null ) Delete( s.Path );
	}

	static void Delete( string path )
	{
		try
		{
			if ( FileSystem.Data.FileExists( path ) ) FileSystem.Data.DeleteFile( path );
		}
		catch ( Exception e )
		{
			// still open in a player - the cache is wiped on the next start anyway
			Log.Trace( $"[bimp] couldn't delete {path}: {e.Message}" );
		}
	}

	public void Dispose()
	{
		if ( disposed ) return;
		disposed = true;
		cts.Cancel();

		foreach ( var t in building.Values )
			if ( t.IsCompletedSuccessfully && t.Result is not null ) Delete( t.Result.Path );

		try
		{
			if ( FileSystem.Data.DirectoryExists( directory ) ) FileSystem.Data.DeleteDirectory( directory, true );
		}
		catch ( Exception e )
		{
			Log.Trace( $"[bimp] couldn't delete {directory}: {e.Message}" );
		}
	}
}
