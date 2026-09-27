using System.IO;
using Bimp.Resolver.Media;

namespace Bimp.Resolver.Live;

/// <summary>
/// Receives a live stream's video access units (H.264 or AV1) and AAC frames from any source (MPEG-TS, HLS, RTSP)
/// and cuts them into short, finished, moov-first MP4 files in <see cref="FileSystem.Data"/>, at keyframes - what
/// the <see cref="SegmentPlayer"/> plays back to back. No re-encoding: the engine decodes whichever codec the
/// source sends (its AV1 decoder is the smoother one - see <see cref="VideoFormat.HoldsBackFrames"/>).
/// <para>
/// The engine's H.264 decoder (Media Foundation) holds back the last 22 frames of a file and never drains them.
/// Normal mode: each file carries <see cref="OverlapSeconds"/> of the next segment after its real end, and the
/// player swaps at the real end, so the extra part is never shown. AV1 holds nothing back: its files only run
/// <see cref="LowOverlapSeconds"/> on.
/// </para>
/// <para>
/// Low latency mode (<c>bimp_live_latency low</c>): a file per keyframe interval. H.264 is written the moment the
/// next keyframe arrives, ending in <see cref="FlushFrames"/> generated all-skip P frames (copies of the last
/// picture, a few bytes each) that push the real frames out of the decoder; AV1 once <see cref="LowOverlapSeconds"/>
/// of the next one are in.
/// AAC is decoded here (<see cref="AacDecoder"/>) and played beside the video like G.711, so the files are video only.
/// </para>
/// </summary>
public sealed class LiveSegmenter : ISegmentFeed, IDisposable
{
	/// <summary>
	/// Minimum segment length (cut at the first keyframe after it) - see bimp_live_segment. VOD: a short first file,
	/// then long ones (each file costs a decoder start).
	/// </summary>
	double TargetSeconds => !vod ? Math.Clamp( MediaSettings.LiveSegmentSeconds, 1, 20 ) : nextSeq == 0 ? VodFirstSeconds : VodSeconds;
	const double VodFirstSeconds = 4, VodSeconds = 20;
	public const double OverlapSeconds = 1.5;

	/// <summary> Low latency: keyframes closer together than this go in the same file (each file costs a decoder start). </summary>
	const double MinLowSeconds = 0.4;

	/// <summary> Low latency: skip frames after a file's real end - the decoder holds back 22 (measured), plus a margin. </summary>
	const int FlushFrames = 25;

	const int KeepFiles = 8;
	const long Clock = 90000;

	readonly string directory;
	readonly bool lowRequested;
	readonly List<VideoSample> video = new();
	readonly List<AudioSample> audio = new();
	readonly Dictionary<int, MediaSegment> finished = new();
	readonly List<(int seq, long start, long end, VideoFormat format)> pending = new();

	byte[] sps, pps;
	H264.Sps spsInfo;
	Media.Av1.SequenceHeader av1Sequence;

	/// <summary> The video's codec and configuration, and the one the segment being cut started with. </summary>
	VideoFormat format, segmentFormat;
	AacConfig aac;
	AacDecoder aacDecoder;
	bool hasVideoTrack;
	long segmentStart = long.MinValue;
	int nextSeq;
	long streamZero = long.MinValue;
	bool disposed;

	// timestamps made continuous across discontinuities (HLS, reconnects)
	long offset;
	long lastVideoDts = long.MinValue;
	long lastAudioPts = long.MinValue;

	// low latency: where the skip frames carry on from
	H264.SliceInfo lastSlice;
	int lastReferencePoc;
	long maxPts = long.MinValue;
	long frameDuration = 3000;

	/// <summary>
	/// A finished stream played from a start time (HLS VOD): seekable by reopening, files carry media time from
	/// <see cref="TimeBase"/>.
	/// </summary>
	readonly bool vod;

	/// <summary> VOD: the media time (seconds) the first file starts at. </summary>
	public double TimeBase { get; set; }

	public bool IsLive => !vod;

	/// <summary> Low latency mode: short files with no overlap, AAC decoded here. Needs a video track. </summary>
	public bool LowLatency => lowRequested && hasVideoTrack;

	/// <summary> See <see cref="ISegmentFeed.StartBufferSeconds"/>. Continuous sources (TS, RTSP) need little. </summary>
	public double StartBufferSeconds
	{
		get => LowLatency ? 0 : startBuffer ?? Overlap + 1.0;
		set => startBuffer = value;
	}
	double? startBuffer;

	/// <summary>
	/// How far each file runs past its real end: H.264 <see cref="OverlapSeconds"/> in normal mode and none in low
	/// latency (skip frames instead - with the next keyframe in the file Media Foundation dropped 3x the frames);
	/// AV1 <see cref="LowOverlapSeconds"/>.
	/// </summary>
	public double Overlap => !(format?.HoldsBackFrames ?? true) ? LowOverlapSeconds : LowLatency ? 0 : OverlapSeconds;

	/// <summary>
	/// AV1: each file also runs this far into the next one. A new player hands over its first frame
	/// a few tens of ms after the boundary it opened for, so a file that ended at the boundary had to either hold its
	/// last frame or have the next player skip its first ones - one or the other at every swap (measured: a held
	/// frame every second, 60 a minute). With a little of the next segment in it, the current player plays on while
	/// the next one comes up in step, like normal mode's overlap. Costs this much latency.
	/// </summary>
	public const double LowOverlapSeconds = 0.15;

	// diagnostics
	public int VideoFrames { get; private set; }
	public int AudioFrames { get; private set; }
	public int DroppedBeforeKeyframe { get; private set; }
	public double WriteMilliseconds { get; private set; }
	public int Newest { get; private set; } = -1;
	public string Error { get; set; }

	/// <summary> The source finished (a stream that ended, like an HLS event with ENDLIST). </summary>
	public bool Ended { get; private set; }

	/// <summary> Width x height of the video, once known. </summary>
	public string VideoInfo { get; private set; }

	/// <summary> Decoded audio (G.711, or AAC in low latency mode) played alongside the video - see <see cref="LivePcmAudio"/>. </summary>
	public LivePcmAudio Pcm { get; private set; }

	/// <summary> Motion JPEG video, played frame by frame without segments (see <see cref="LiveMjpeg"/>). </summary>
	public LiveMjpeg Mjpeg { get; private set; }

	/// <summary> Stream time (seconds) of the newest video frame received. </summary>
	public double NewestVideoTime => lastVideoDts == long.MinValue || streamZero == long.MinValue ? 0 : (lastVideoDts - streamZero) / (double)Clock;

	/// <summary>
	/// The sender's wall clock (unix seconds) at stream time 0, from RTCP sender reports - null if the source sends
	/// none. Stream time + this = when a frame was sent, which is what the latency readout compares against.
	/// </summary>
	public double? WallClockZero { get; private set; }

	/// <summary> Keyframe interval of the source (seconds), measured. </summary>
	public double KeyframeInterval { get; private set; }
	long lastKeyDts = long.MinValue;

	public LiveSegmenter( string directory, bool lowLatency = false, bool vod = false )
	{
		this.directory = directory;
		this.vod = vod;
		lowRequested = lowLatency && !vod;
		FileSystem.Data.CreateDirectory( directory );
	}

	/// <summary> The source has a video track (so segments are cut at keyframes, not by time). </summary>
	public void ExpectVideo() => hasVideoTrack = true;

	/// <summary> The video is Motion JPEG: no segments, and any AAC is decoded here like in low latency mode. </summary>
	public void ExpectMjpeg()
	{
		Mjpeg ??= new LiveMjpeg();
		VideoInfo ??= "Motion JPEG";
	}

	/// <summary> One complete JPEG frame, 90 kHz timestamp. </summary>
	public void AddJpeg( long pts, byte[] jpeg )
	{
		if ( disposed || jpeg is null ) return;
		ExpectMjpeg();
		VideoFrames++;
		pts = Continuous( pts, ref lastVideoDts, 3000 );
		if ( streamZero == long.MinValue ) streamZero = pts;
		Mjpeg.Add( (pts - streamZero) / (double)Clock, jpeg );
		if ( Mjpeg.Size is { } size ) VideoInfo = $"Motion JPEG {size}";
	}

	public void SetParameterSets( byte[] newSps, byte[] newPps )
	{
		// cameras repeat them before every keyframe - only a real change makes a new format
		var changed = false;
		if ( newSps is not null && (sps is null || !newSps.AsSpan().SequenceEqual( sps )) )
		{
			sps = newSps;
			changed = true;
			try { spsInfo = H264.ParseSps( sps ); } catch { spsInfo = null; }
		}
		if ( newPps is not null && (pps is null || !newPps.AsSpan().SequenceEqual( pps )) )
		{
			pps = newPps;
			changed = true;
		}
		if ( spsInfo is not null && pps is not null && (changed || format is null) )
		{
			try { format = VideoFormat.H264( sps, pps ); } catch { }
		}
		if ( spsInfo is not null && VideoInfo is null ) VideoInfo = $"H.264 {spsInfo.Width}x{spsInfo.Height}";
	}

	public void SetAacConfig( AacConfig config )
	{
		if ( config is null || config.SameAs( aac ) ) return;
		// a config change mid-segment can't be expressed in one track - start a new segment
		if ( aac is not null && !LowLatency ) CutNow();
		aac = config;
		aacDecoder = null;
	}

	/// <summary>
	/// A sender report: this stream time (90 kHz, as passed to AddVideo / AddPcm) of the video or audio track was
	/// sent at this wall clock time.
	/// </summary>
	public void SetWallClock( long time, double unixSeconds, bool video = true )
	{
		if ( streamZero == long.MinValue ) return;
		var zero = unixSeconds - (time + offset - streamZero) / (double)Clock;
		if ( video ) WallClockZero = zero;
		else audioWallClockZero = zero;
	}
	double? audioWallClockZero;

	/// <summary>
	/// Seconds to add to audio stream times to line them up with the video's. Without sender reports for both,
	/// each track's timeline starts when its first packet arrived, which can be a few hundred ms apart.
	/// </summary>
	public double AudioShift => WallClockZero is { } v && audioWallClockZero is { } a ? a - v : 0;

	/// <summary>
	/// One H.264 access unit as separate NAL units (RTSP), 90 kHz timestamps.
	/// </summary>
	public void AddVideo( long dts, long pts, List<byte[]> nals )
	{
		if ( disposed ) return;
		H264.SliceInfo? slice = null;
		foreach ( var n in nals )
		{
			var t = H264.NalType( n );
			if ( t == H264.NalSps ) SetParameterSets( n, null );
			else if ( t == H264.NalPps ) SetParameterSets( null, n );
			else if ( slice is null && LowLatency && spsInfo is not null && t is H264.NalSlice or H264.NalIdr ) slice = H264.ParseSlice( n, spsInfo );
		}
		var key = nals.Any( n => H264.NalType( n ) == H264.NalIdr );
		AddVideoSample( dts, pts, key, slice, () => H264.ToAvcc( nals ) );
	}

	/// <summary>
	/// One H.264 access unit in an Annex B buffer (MPEG-TS), 90 kHz timestamps. Copied once, straight into the
	/// MP4 sample - splitting it into NAL arrays first doubled the garbage (keyframes land on the large object heap).
	/// </summary>
	public void AddVideo( long dts, long pts, byte[] annexB, int length )
	{
		if ( disposed ) return;
		var nals = H264.NalRanges( annexB, 0, length );
		if ( nals.Count == 0 ) return;
		var key = false;
		H264.SliceInfo? slice = null;
		foreach ( var (o, l) in nals )
		{
			var t = annexB[o] & 0x1F;
			if ( t == H264.NalSps ) SetParameterSets( annexB.AsSpan( o, l ).ToArray(), null );
			else if ( t == H264.NalPps ) SetParameterSets( null, annexB.AsSpan( o, l ).ToArray() );
			else if ( t == H264.NalIdr ) key = true;
			if ( slice is null && LowLatency && spsInfo is not null && t is H264.NalSlice or H264.NalIdr ) slice = H264.ParseSlice( annexB.AsSpan( o, l ), spsInfo );
		}
		AddVideoSample( dts, pts, key, slice, () => H264.ToAvcc( annexB, nals ) );
	}

	/// <summary>
	/// One AV1 temporal unit (RTSP, fMP4), 90 kHz timestamp. AV1 has no frame reordering in the container, so decode
	/// time = presentation time. A new sequence header (resolution change) starts a new file at its keyframe.
	/// </summary>
	public void AddAv1( long pts, byte[] temporalUnit )
	{
		if ( disposed || temporalUnit is not { Length: > 0 } ) return;
		var tu = Media.Av1.ForStorage( temporalUnit );
		var changed = false;
		if ( Media.Av1.FindSequenceHeader( tu ) is { } seq && !seq.SameAs( av1Sequence ) )
		{
			changed = av1Sequence is not null;
			av1Sequence = seq;
			format = VideoFormat.Av1( seq );
			VideoInfo = format.ToString();
		}
		var key = av1Sequence is not null && Media.Av1.IsKeyframe( tu, av1Sequence );
		AddVideoSample( pts, pts, key, null, () => tu, changed );
	}

	void AddVideoSample( long dts, long pts, bool key, H264.SliceInfo? slice, Func<byte[]> sample, bool formatChanged = false )
	{
		hasVideoTrack = true;

		// can't decode anything before the first keyframe with its configuration
		if ( segmentStart == long.MinValue && (!key || format is null) ) { DroppedBeforeKeyframe++; return; }
		VideoFrames++;

		var previousDts = lastVideoDts;
		dts = Continuous( dts, ref lastVideoDts, 3000 );
		pts = pts + offset;
		if ( pts < dts ) pts = dts;
		if ( previousDts != long.MinValue && dts > previousDts ) frameDuration = Math.Clamp( dts - previousDts, 900, 9000 );

		if ( key )
		{
			if ( lastKeyDts != long.MinValue && dts > lastKeyDts ) KeyframeInterval = (dts - lastKeyDts) / (double)Clock;
			lastKeyDts = dts;
		}

		List<VideoSample> flush = null;
		if ( segmentStart == long.MinValue )
		{
			segmentStart = dts;
			streamZero = dts;
			segmentFormat = format;
		}
		else if ( key && (formatChanged || LowLatency && dts - segmentStart >= MinLowSeconds * Clock) && dts > segmentStart )
		{
			// cut here: H.264 is written now, ending in skip frames; AV1 once a little of the next one is in (see Overlap)
			flush = LowLatency && Overlap == 0 ? FlushSamples( dts ) : null;
			pending.Add( (nextSeq++, segmentStart, dts, segmentFormat) );
			segmentStart = dts;
			segmentFormat = format;
		}
		else if ( key && !LowLatency && dts - segmentStart >= TargetSeconds * 0.9 * Clock ) // a keyframe a hair early still counts
		{
			pending.Add( (nextSeq++, segmentStart, dts, segmentFormat) );
			segmentStart = dts;
			segmentFormat = format;
		}

		if ( slice is { } s )
		{
			lastSlice = s;
			if ( s.Reference || s.Idr ) lastReferencePoc = s.PocLsb;
		}
		if ( key ) maxPts = long.MinValue;
		maxPts = Math.Max( maxPts, pts );

		var data = sample();
		if ( data.Length == 0 ) return;
		video.Add( new VideoSample { Dts = dts, Pts = pts, Key = key, Data = data } );

		FlushPending( dts, flush );
	}

	/// <summary>
	/// All-skip P frames carrying on from the last frame received (not the one at <paramref name="end"/>, which
	/// starts the next file) - see <see cref="H264.SkipFrame"/>. The first carries their own PPS.
	/// </summary>
	List<VideoSample> FlushSamples( long end )
	{
		if ( segmentFormat is not { IsH264: true } || spsInfo is null || pps is null ) return null;
		var ppsId = Math.Min( 255, H264.PpsId( pps ) + 1 );
		var prev = lastSlice;
		prev.PocLsb = lastReferencePoc;
		var list = new List<VideoSample>( FlushFrames );
		var ptsBase = Math.Max( end, maxPts + frameDuration );
		for ( int i = 0; i < FlushFrames; i++ )
		{
			var frame = H264.SkipFrame( spsInfo, ppsId, ref prev );
			var nals = i == 0 ? new[] { H264.SkipPps( spsInfo, ppsId ), frame } : new[] { frame };
			list.Add( new VideoSample { Dts = end + i * frameDuration, Pts = ptsBase + i * frameDuration, Data = Avcc( nals ) } );
		}
		return list;
	}

	/// <summary> NAL units as MP4 sample data, keeping parameter sets (<see cref="H264.ToAvcc(IEnumerable{byte[]})"/> drops them). </summary>
	static byte[] Avcc( byte[][] nals )
	{
		var result = new byte[nals.Sum( n => n.Length + 4 )];
		var o = 0;
		foreach ( var n in nals )
		{
			System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian( result.AsSpan( o ), n.Length );
			n.CopyTo( result, o + 4 );
			o += n.Length + 4;
		}
		return result;
	}

	/// <summary> One raw AAC frame (no ADTS header), 90 kHz timestamp. </summary>
	public void AddAudio( long pts, byte[] frame )
	{
		if ( disposed || aac is null ) return;
		AudioFrames++;
		pts = Continuous( pts, ref lastAudioPts, 1024 * Clock / Math.Max( 1, aac.SampleRate ), video: false );

		if ( LowLatency || Mjpeg is not null )
		{
			// decoded here and played through a SoundStream, lined up with the picture like G.711
			if ( streamZero == long.MinValue ) return;
			try
			{
				aacDecoder ??= new AacDecoder( aac );
				var pcm = aacDecoder.Decode( frame );
				if ( pcm is null ) return;
				Pcm ??= new LivePcmAudio( aacDecoder.SampleRate, aacDecoder.Channels );
				Pcm.Add( (pts - streamZero) / (double)Clock + AudioShift, pcm );
			}
			catch ( Exception e )
			{
				Log.Trace( $"[bimp] aac: {e.Message}" );
			}
			return;
		}

		if ( !hasVideoTrack )
		{
			// audio only: cut by time
			if ( segmentStart == long.MinValue ) { segmentStart = pts; streamZero = pts; }
			else if ( pts - segmentStart >= TargetSeconds * Clock )
			{
				pending.Add( (nextSeq++, segmentStart, pts, null) );
				segmentStart = pts;
			}
			audio.Add( new AudioSample { Pts = pts, Data = frame } );
			FlushPending( pts );
			return;
		}

		if ( segmentStart == long.MinValue ) return; // wait for the first keyframe
		audio.Add( new AudioSample { Pts = pts, Data = frame } );
	}

	/// <summary> Decoded PCM audio (G.711), 90 kHz timestamp of the first sample. </summary>
	public void AddPcm( long pts, short[] samples, int sampleRate )
	{
		if ( disposed ) return;
		Pcm ??= new LivePcmAudio( sampleRate );
		if ( streamZero == long.MinValue ) return; // nothing to line up with yet
		Pcm.Add( (pts + offset - streamZero) / (double)Clock + AudioShift, samples );
	}

	/// <summary>
	/// Keep timestamps moving forward: a jump back, or more than 10 s forward, is a discontinuity - shift
	/// everything after it so it carries on from where we were.
	/// </summary>
	long Continuous( long t, ref long last, long step, bool video = true )
	{
		var adjusted = t + offset;
		if ( last != long.MinValue && (adjusted < last - Clock || adjusted > last + 10 * Clock) )
		{
			// video leads the correction; audio-only streams use audio
			if ( video || !hasVideoTrack )
			{
				offset += last + step - adjusted;
				adjusted = last + step;
			}
		}
		last = adjusted;
		return adjusted;
	}

	/// <summary> Cut the current segment at its last sample (config change, end of stream). </summary>
	void CutNow()
	{
		if ( segmentStart == long.MinValue ) return;
		var end = video.Count > 0 ? video[^1].Dts + 1 : audio.Count > 0 ? audio[^1].Pts + 1 : segmentStart;
		if ( end <= segmentStart ) return;
		var flush = LowLatency && video.Count > 0 ? FlushSamples( end ) : null;
		pending.Add( (nextSeq++, segmentStart, end, segmentFormat) );
		segmentStart = end;
		FlushPending( long.MaxValue, flush );
	}

	/// <summary> Source finished: write what's left, without overlap. </summary>
	public void End()
	{
		if ( Ended ) return;
		CutNow();
		Ended = true;
		if ( finished.TryGetValue( Newest, out var last ) ) last.Last = true;
	}

	void FlushPending( long now, List<VideoSample> flush = null )
	{
		var overlap = (long)(Overlap * Clock);
		while ( pending.Count > 0 && (now == long.MaxValue || now >= pending[0].end + overlap) )
		{
			var (seq, start, end, segFormat) = pending[0];
			pending.RemoveAt( 0 );
			// a file can't run on into one with another format
			var until = now == long.MaxValue || segFormat != format ? end : end + overlap;
			if ( LowLatency && until > end ) until = now + 1; // up to the frame just received
			WriteSegment( seq, start, end, until, segFormat, pending.Count == 0 ? flush : null );
		}

		// drop samples nothing will need again
		var keepFrom = pending.Count > 0 ? pending[0].start : segmentStart;
		video.RemoveAll( s => s.Dts < keepFrom );
		audio.RemoveAll( s => s.Pts < keepFrom );
	}

	void WriteSegment( int seq, long start, long end, long until, VideoFormat segFormat, List<VideoSample> flush )
	{
		var snapTimer = System.Diagnostics.Stopwatch.StartNew();
		var v = video.Where( s => s.Dts >= start && s.Dts < until ).ToList();
		var a = LowLatency ? new List<AudioSample>() : audio.Where( s => s.Pts >= start && s.Pts < until ).ToList();
		if ( v.Count == 0 && a.Count == 0 ) return;
		// real frames past the end (not skip frames)
		var overlap = (float)(Math.Max( 0, (v.Count > 0 ? v[^1].Dts : a.Count > 0 ? a[^1].Pts : end) - end ) / (double)Clock);
		if ( flush is not null && v.Count > 0 ) v.AddRange( flush );

		MediaProbe.Note( $"segment {seq} cut, snapshot {snapTimer.Elapsed.TotalMilliseconds:0.0}ms" );
		_ = WriteSegmentAsync( seq, start, end, overlap, v, segFormat, a, aac );
	}

	/// <summary>
	/// Build and write the file on a worker thread, streamed straight to disk. Building a multi-megabyte segment
	/// in a MemoryStream and copying it out (as this used to) ran on the main thread and churned large-object
	/// heap garbage; when the main thread stalls, nothing calls the players' Present() and the picture skips
	/// (measured: a 72 ms main-thread stall was the only hitch left in 40 s of playback).
	/// </summary>
	async Task WriteSegmentAsync( int seq, long start, long end, float overlap, List<VideoSample> v, VideoFormat segFormat, List<AudioSample> a, AacConfig segAac )
	{
		var path = $"{directory}/live_{seq}.mp4";
		var timer = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			await GameTask.RunInThreadAsync( () =>
			{
				using var file = FileSystem.Data.OpenWrite( path );
				Mp4Writer.Write( file, start, v, segFormat, a, segAac );
			} );
		}
		catch ( Exception e )
		{
			if ( disposed ) return;
			Error ??= $"Couldn't write a live segment: {e.Message}";
			Log.Warning( $"[bimp] live segment {seq}: {e}" );
			return;
		}

		if ( disposed )
		{
			try { FileSystem.Data.DeleteFile( path ); } catch { }
			return;
		}

		WriteMilliseconds = timer.Elapsed.TotalMilliseconds;
		MediaProbe.Note( $"segment {seq} written in {WriteMilliseconds:0}ms" );

		finished[seq] = new MediaSegment
		{
			Seq = seq,
			Path = path,
			Start = 0,
			End = (float)((end - start) / (double)Clock),
			Overlap = overlap,
			StreamStart = TimeBase + (start - streamZero) / (double)Clock,
			ReadyAt = RealTime.NowDouble,
		};
		Newest = Math.Max( Newest, seq );
		if ( Ended && seq == Newest ) finished[seq].Last = true;

		// keep the last few only
		foreach ( var old in finished.Keys.Where( k => k <= seq - KeepFiles ).ToList() )
			Release( old );
	}

	public MediaSegment TryGet( int seq ) => finished.TryGetValue( seq, out var s ) ? s : null;

	public void Want( int seq ) { }

	public void Release( int seq )
	{
		if ( !finished.Remove( seq, out var s ) ) return;
		try
		{
			if ( FileSystem.Data.FileExists( s.Path ) ) FileSystem.Data.DeleteFile( s.Path );
		}
		catch ( Exception e )
		{
			Log.Trace( $"[bimp] couldn't delete {s.Path}: {e.Message}" );
		}
	}

	public void Dispose()
	{
		disposed = true;
		Pcm?.Dispose();
		Mjpeg?.Dispose();
		foreach ( var seq in finished.Keys.ToList() ) Release( seq );
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
