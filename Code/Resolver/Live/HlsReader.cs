using System.Globalization;
using System.Threading;
using Bimp.Resolver.Media;

namespace Bimp.Resolver.Live;

/// <summary>
/// HLS: picks a variant from a master playlist (the best up to maxHeight the engine can decode - AV1 over H.264 at the
/// same height, never H.265), then follows its media playlist - and its separate audio playlist, if it has one - and
/// feeds each segment to the <see cref="LiveSegmenter"/>: MPEG-TS through a <see cref="TsDemuxer"/>, fMP4
/// (<c>#EXT-X-MAP</c>) through <see cref="Fmp4Reader"/>. Live, or a finished playlist from a start time (VOD).
/// Encrypted HLS isn't supported.
/// </summary>
public sealed class HlsReader
{
	readonly Uri url;
	readonly LiveSegmenter sink;
	readonly int maxHeight;
	readonly Func<CancellationToken, Task> waitForPlayback;

	/// <summary> VOD: where to start (media seconds), and how far behind it the segment it starts with may begin. </summary>
	readonly double startTime;
	readonly double maxLag;

	public HlsReader( Uri url, LiveSegmenter sink, int maxHeight, Func<CancellationToken, Task> waitForPlayback, double startTime = 0, double maxLag = double.MaxValue )
	{
		this.url = url;
		this.sink = sink;
		this.maxHeight = maxHeight;
		this.waitForPlayback = waitForPlayback;
		this.startTime = startTime;
		this.maxLag = maxLag;
	}

	/// <summary>
	/// Host: is the playlist live, how long is it if not (seconds), and its best variant's height (0 if it doesn't say).
	/// </summary>
	public static async Task<(bool live, double duration, int height)> ProbeAsync( Uri url, CancellationToken ct )
	{
		var reader = new HlsReader( url, null, 4320, null );
		var (media, _) = await reader.ResolveVariant( url, ct );
		var list = Parse( media, await Http.RequestStringAsync( media.ToString(), cancellationToken: ct ) );
		var duration = list.Segments.Count > 0 ? list.Segments[^1].Start + list.Segments[^1].Duration : 0;
		return (!list.Ended, duration, reader.PickedHeight);
	}

	/// <summary> The height of the variant <see cref="ResolveVariant"/> picked. </summary>
	int PickedHeight { get; set; }

	bool Vod => sink is { IsLive: false };

	/// <summary> A VOD playlist's length (seconds), once read. </summary>
	public double Duration { get; private set; }

	sealed class Segment
	{
		public Uri Uri;
		public double Start, Duration;
		public long? RangeOffset, RangeLength;
		public bool Discontinuity;
		public Map Map;
	}

	sealed class Map
	{
		public Uri Uri;
		public long? RangeOffset, RangeLength;
		public string Key => $"{Uri}|{RangeOffset}|{RangeLength}";
	}

	sealed class MediaPlaylist
	{
		public long FirstSequence;
		public double TargetDuration = 6;
		public bool Ended;
		public readonly List<Segment> Segments = new();
	}

	/// <summary> One playlist being followed (the video/muxed one, or the separate audio), and its demuxing state. </summary>
	sealed class Follower
	{
		public Uri Playlist;
		public bool AudioOnly;
		public MediaPlaylist List;
		public long Next = -1;
		public TsDemuxer Ts;
		public string MapKey;
		public List<Fmp4Reader.Track> Tracks;
		public int NalLength = 4;
		public bool Warned;

		public Segment NextSegment => List is null || Next < List.FirstSequence || Next >= List.FirstSequence + List.Segments.Count ? null : List.Segments[(int)(Next - List.FirstSequence)];
	}

	public async Task Run( CancellationToken ct )
	{
		var (videoPlaylist, audioPlaylist) = await ResolveVariant( url, ct );
		var video = new Follower { Playlist = videoPlaylist };
		var audio = audioPlaylist is null ? null : new Follower { Playlist = audioPlaylist, AudioOnly = true };
		// the audio goes in first: without this the segmenter would take it for an audio only stream and cut by time
		if ( audio is not null ) sink.ExpectVideo();
		var failures = 0;
		var sameSequence = false;

		while ( !ct.IsCancellationRequested )
		{
			try
			{
				video.List = Parse( video.Playlist, await Http.RequestStringAsync( video.Playlist.ToString(), cancellationToken: ct ) );
				if ( audio is not null ) audio.List = Parse( audio.Playlist, await Http.RequestStringAsync( audio.Playlist.ToString(), cancellationToken: ct ) );
				failures = 0;
			}
			catch ( Exception e ) when ( e is not OperationCanceledException && e is not ResolveException )
			{
				if ( ++failures > 5 ) throw new ResolveException( $"Lost the HLS playlist: {e.Message}" );
				await Task.Delay( 2000, ct );
				continue;
			}

			// played as a VOD (an extractor said so): an EVENT playlist that's finished may not say so (Kick)
			if ( Vod )
			{
				video.List.Ended = true;
				if ( audio is not null ) audio.List.Ended = true;
			}
			var list = video.List;
			if ( list.Ended ) Duration = list.Segments.Count > 0 ? list.Segments[^1].Start + list.Segments[^1].Duration : 0;

			// segments arrive whole, a target duration apart: buffer two of them past the overlap before playing
			sink.StartBufferSeconds = sink.Overlap + 2 * list.TargetDuration + 1.0;

			if ( video.Next < 0 ) video.Next = FirstSegment( list );
			if ( video.Next < list.FirstSequence ) video.Next = list.FirstSequence; // fell out of the window
			if ( audio is not null && audio.Next < 0 && video.NextSegment is { } first )
			{
				// VOD: the audio segment covering the first video segment's start. Live: the same sequence number if the
				// playlists count alike (YouTube), else as far from the end.
				sameSequence = !list.Ended && video.Next >= audio.List.FirstSequence && video.Next < End( audio );
				audio.Next = list.Ended
					? audio.List.FirstSequence + Math.Max( 0, audio.List.Segments.FindLastIndex( s => s.Start <= first.Start + 0.001 ) )
					: sameSequence ? video.Next : End( audio ) - (End( video ) - video.Next);
			}
			if ( audio is not null && audio.Next < audio.List.FirstSequence ) audio.Next = audio.List.FirstSequence;

			var fetched = false;
			while ( video.NextSegment is { } segment && !ct.IsCancellationRequested )
			{
				await waitForPlayback( ct );

				// separate audio goes in first, up to the end of this video segment (the segmenter needs it by then)
				while ( audio?.NextSegment is { } a && (list.Ended ? a.Start < segment.Start + segment.Duration
					: sameSequence ? audio.Next <= video.Next : End( audio ) - audio.Next >= End( video ) - video.Next) )
				{
					await Feed( audio, a, ct );
					audio.Next++;
				}

				await Feed( video, segment, ct );
				video.Next++;
				fetched = true;
			}

			if ( list.Ended && video.NextSegment is null )
			{
				while ( audio?.NextSegment is { } a )
				{
					await Feed( audio, a, ct );
					audio.Next++;
				}
				video.Ts?.Flush();
				audio?.Ts?.Flush();
				sink.End();
				return;
			}

			// wait for the playlist to grow - poll at half the target duration so new segments are picked up quickly
			await Task.Delay( (int)(Math.Clamp( list.TargetDuration, 1, 10 ) * (fetched ? 250 : 500)), ct );
		}
	}

	static long End( Follower f ) => f.List.FirstSequence + f.List.Segments.Count;

	/// <summary>
	/// Live: far enough from the end to fill the start buffer right away (plus the segment being cut). VOD: the segment
	/// containing the start time - or the next one if that starts more than maxLag before it.
	/// </summary>
	long FirstSegment( MediaPlaylist list )
	{
		if ( !list.Ended )
		{
			var last = list.FirstSequence + list.Segments.Count - 1;
			var joinFromEnd = (int)Math.Ceiling( sink.StartBufferSeconds / Math.Max( 1, list.TargetDuration ) ) + 1;
			return Math.Max( list.FirstSequence, last - joinFromEnd + 1 );
		}

		var i = list.Segments.FindLastIndex( s => s.Start <= startTime + 0.001 );
		if ( i < 0 ) i = 0;
		if ( startTime - list.Segments[i].Start > maxLag && i + 1 < list.Segments.Count ) i++;
		sink.TimeBase = list.Segments[i].Start;
		return list.FirstSequence + i;
	}

	async Task Feed( Follower f, Segment segment, CancellationToken ct )
	{
		try
		{
			if ( segment.Discontinuity || f.Ts is null && segment.Map is null )
			{
				// new timestamps (and maybe new streams): start over
				f.Ts?.Flush();
				f.Ts = null;
			}

			if ( segment.Map is { } map )
			{
				if ( map.Key != f.MapKey )
				{
					var init = await Fetch( map.Uri, map.RangeOffset, map.RangeLength, ct );
					SetInit( f, init );
					f.MapKey = map.Key;
				}
				var data = await Fetch( segment.Uri, segment.RangeOffset, segment.RangeLength, ct );
				FeedMp4( f, data );
			}
			else
			{
				f.Ts ??= new TsDemuxer( sink, video: !f.AudioOnly, audio: f.AudioOnly || !HasSeparateAudio );
				var data = await Fetch( segment.Uri, segment.RangeOffset, segment.RangeLength, ct );
				if ( data.Length > 0 && data[0] != 0x47 && TsDemuxer.IsPackedAudio( data ) ) { f.Ts.FeedPackedAudio( data ); return; }
				f.Ts.Feed( data );
				f.Ts.Flush();
				if ( f.Ts.Problem is not null && !f.Ts.FoundProgram ) throw new ResolveException( f.Ts.Problem );
			}
		}
		catch ( Exception e ) when ( e is not OperationCanceledException && e is not ResolveException )
		{
			// the first failure of each playlist is worth seeing; a stream that keeps failing would flood the log
			if ( !f.Warned ) Log.Warning( $"[bimp] hls {(f.AudioOnly ? "audio" : "video")} segment {segment.Uri}: {e.Message}" );
			else Log.Trace( $"[bimp] hls segment {segment.Uri}: {e.Message}" );
			f.Warned = true;
		}
	}

	bool HasSeparateAudio { get; set; }

	static async Task<byte[]> Fetch( Uri uri, long? offset, long? length, CancellationToken ct )
	{
		if ( length is not { } n ) return await Http.RequestBytesAsync( uri.ToString(), cancellationToken: ct );
		var from = offset ?? 0;
		var headers = new Dictionary<string, string> { ["Range"] = $"bytes={from}-{from + n - 1}" };
		return await Http.RequestBytesAsync( uri.ToString(), headers: headers, cancellationToken: ct );
	}

	/// <summary> An fMP4 init segment: the tracks, and their decoder configuration handed to the segmenter. </summary>
	void SetInit( Follower f, byte[] init )
	{
		var tracks = Fmp4Reader.ReadTracks( init, 0, init.Length );
		f.Tracks = new List<Fmp4Reader.Track>();
		foreach ( var t in tracks )
		{
			if ( t.IsVideo && !f.AudioOnly && f.Tracks.All( x => !x.IsVideo ) )
			{
				if ( t.Codec is "hvc1" or "hev1" ) throw new ResolveException( "This stream's video is H.265 (HEVC), which the engine can't decode." );
				if ( t.Codec is not ("avc1" or "avc3" or "av01") ) { Log.Warning( $"[bimp] hls: video is {t.Codec}, which can't be played" ); continue; }
				sink.ExpectVideo();
				if ( t.Codec is "avc1" or "avc3" && t.Config is { Length: > 6 } avcC )
				{
					f.NalLength = (avcC[4] & 3) + 1;
					var (sps, pps) = ParameterSets( avcC );
					sink.SetParameterSets( sps, pps );
				}
				f.Tracks.Add( t );
			}
			else if ( t.IsAudio && (f.AudioOnly || !HasSeparateAudio) && f.Tracks.All( x => !x.IsAudio ) )
			{
				if ( t.Codec != "mp4a" || t.Config is null ) { Log.Warning( $"[bimp] hls: audio is {t.Codec}, only AAC is supported" ); continue; }
				sink.SetAacConfig( AacConfig.FromAsc( t.Config ) );
				f.Tracks.Add( t );
			}
		}
	}

	/// <summary> The first SPS and PPS in an avcC record. </summary>
	static (byte[] sps, byte[] pps) ParameterSets( byte[] c )
	{
		byte[] sps = null, pps = null;
		var i = 5;
		var count = c[i++] & 0x1F;
		for ( int k = 0; k < count && i + 2 <= c.Length; k++ )
		{
			var n = (c[i] << 8) | c[i + 1];
			if ( i + 2 + n > c.Length ) break;
			sps ??= c.AsSpan( i + 2, n ).ToArray();
			i += 2 + n;
		}
		if ( i < c.Length )
		{
			count = c[i++];
			for ( int k = 0; k < count && i + 2 <= c.Length; k++ )
			{
				var n = (c[i] << 8) | c[i + 1];
				if ( i + 2 + n > c.Length ) break;
				pps ??= c.AsSpan( i + 2, n ).ToArray();
				i += 2 + n;
			}
		}
		return (sps, pps);
	}

	void FeedMp4( Follower f, byte[] data )
	{
		if ( f.Tracks is not { Count: > 0 } ) return;
		foreach ( var s in Fmp4Reader.ReadSamples( data, 0, data.Length, f.Tracks ) )
		{
			var t = f.Tracks.FirstOrDefault( x => x.Id == s.Track );
			if ( t is null ) continue;
			var dts = s.DecodeTime * 90000 / t.Timescale;
			var pts = (s.DecodeTime + s.CompositionOffset) * 90000 / t.Timescale;
			var sample = data.AsSpan( s.Offset, s.Size );

			if ( t.Codec == "av01" ) sink.AddAv1( pts, sample.ToArray() );
			else if ( t.IsVideo ) sink.AddVideo( dts, pts, Nals( sample, f.NalLength ) );
			else sink.AddAudio( pts, sample.ToArray() );
		}
	}

	/// <summary> An AVCC sample's NAL units (length prefixed). </summary>
	static List<byte[]> Nals( ReadOnlySpan<byte> d, int lengthSize )
	{
		var nals = new List<byte[]>();
		var i = 0;
		while ( i + lengthSize <= d.Length )
		{
			var n = 0;
			for ( int k = 0; k < lengthSize; k++ ) n = (n << 8) | d[i + k];
			i += lengthSize;
			if ( n <= 0 || i + n > d.Length ) break;
			nals.Add( d.Slice( i, n ).ToArray() );
			i += n;
		}
		return nals;
	}

	/// <summary>
	/// Master playlist: the best variant up to maxHeight, and its separate audio playlist if it has one. Media
	/// playlist: itself.
	/// </summary>
	async Task<(Uri video, Uri audio)> ResolveVariant( Uri playlist, CancellationToken ct )
	{
		var text = await Http.RequestStringAsync( playlist.ToString(), cancellationToken: ct );
		if ( !text.TrimStart().StartsWith( "#EXTM3U" ) ) throw new ResolveException( "That isn't an HLS playlist." );
		if ( !text.Contains( "#EXT-X-STREAM-INF" ) ) return (playlist, null);

		var variants = new List<(Uri uri, int height, long bandwidth, string codecs, string audio)>();
		var audioGroups = new List<(string group, Uri uri, bool isDefault)>();
		var lines = text.Split( '\n' ).Select( l => l.Trim() ).ToArray();
		for ( int i = 0; i < lines.Length; i++ )
		{
			if ( lines[i].StartsWith( "#EXT-X-MEDIA:" ) )
			{
				var m = Attributes( lines[i] );
				if ( m.GetValueOrDefault( "TYPE" ) == "AUDIO" && m.TryGetValue( "URI", out var au ) )
					audioGroups.Add( (m.GetValueOrDefault( "GROUP-ID" ), new Uri( playlist, au ), m.GetValueOrDefault( "DEFAULT" ) == "YES") );
				continue;
			}
			if ( !lines[i].StartsWith( "#EXT-X-STREAM-INF:" ) ) continue;
			var attrs = Attributes( lines[i] );
			var height = 0;
			if ( attrs.TryGetValue( "RESOLUTION", out var res ) && res.Split( 'x' ) is { Length: 2 } wh )
				int.TryParse( wh[1], out height );
			long.TryParse( attrs.GetValueOrDefault( "BANDWIDTH" ), out var bw );
			var j = i + 1;
			while ( j < lines.Length && (lines[j].Length == 0 || lines[j].StartsWith( "#" )) ) j++;
			if ( j < lines.Length ) variants.Add( (new Uri( playlist, lines[j] ), height, bw, attrs.GetValueOrDefault( "CODECS" ) ?? "", attrs.GetValueOrDefault( "AUDIO" )) );
		}

		if ( variants.Count == 0 ) throw new ResolveException( "The HLS playlist has no streams." );

		// the engine can't decode H.265; AV1 plays more smoothly than H.264
		static bool Hevc( string c ) => c.Contains( "hvc1" ) || c.Contains( "hev1" );
		static bool Av1( string c ) => c.Contains( "av01" );
		var playable = variants.Where( v => !Hevc( v.codecs ) ).ToList();
		if ( playable.Count == 0 ) throw new ResolveException( "This stream is only offered as H.265 (HEVC), which the engine can't decode." );

		var fits = playable.Where( v => v.height == 0 || v.height <= maxHeight ).ToList();
		var pick = (fits.Count > 0 ? fits : playable.OrderBy( v => v.height ).Take( 1 ).ToList())
			.OrderByDescending( v => v.height ).ThenByDescending( v => Av1( v.codecs ) ).ThenByDescending( v => v.bandwidth ).First();

		Uri audio = null;
		if ( pick.audio is not null )
		{
			var group = audioGroups.Where( g => g.group == pick.audio ).ToList();
			audio = (group.FirstOrDefault( g => g.isDefault ).uri ?? group.FirstOrDefault().uri);
		}
		HasSeparateAudio = audio is not null;
		PickedHeight = pick.height;
		if ( sink is not null ) Log.Info( $"[bimp] hls: playing the {(pick.height > 0 ? $"{pick.height}p" : "audio")} variant ({pick.codecs}){(audio is not null ? " + separate audio" : "")}, limit {maxHeight}p" );

		var (video, _) = await ResolveVariant( pick.uri, ct );
		return (video, audio);
	}

	static MediaPlaylist Parse( Uri playlist, string text )
	{
		var list = new MediaPlaylist();
		double duration = 0, at = 0;
		long? rangeLength = null, rangeOffset = null;
		var nextOffset = new Dictionary<string, long>();
		var discontinuity = false;
		Map map = null;

		foreach ( var raw in text.Split( '\n' ) )
		{
			var line = raw.Trim();
			if ( line.Length == 0 ) continue;

			if ( line.StartsWith( "#EXT-X-MEDIA-SEQUENCE:" ) ) long.TryParse( line[22..], out list.FirstSequence );
			else if ( line.StartsWith( "#EXT-X-TARGETDURATION:" ) ) double.TryParse( line[22..], NumberStyles.Float, CultureInfo.InvariantCulture, out list.TargetDuration );
			else if ( line.StartsWith( "#EXT-X-ENDLIST" ) ) list.Ended = true;
			else if ( line.StartsWith( "#EXT-X-PLAYLIST-TYPE:VOD" ) ) list.Ended = true;
			else if ( line.StartsWith( "#EXTINF:" ) ) double.TryParse( line[8..].Split( ',' )[0], NumberStyles.Float, CultureInfo.InvariantCulture, out duration );
			else if ( line.StartsWith( "#EXT-X-DISCONTINUITY" ) && !line.StartsWith( "#EXT-X-DISCONTINUITY-SEQUENCE" ) ) discontinuity = true;
			else if ( line.StartsWith( "#EXT-X-BYTERANGE:" ) ) (rangeLength, rangeOffset) = ByteRange( line[17..] );
			else if ( line.StartsWith( "#EXT-X-MAP:" ) )
			{
				var a = Attributes( line );
				if ( !a.TryGetValue( "URI", out var mapUri ) ) continue;
				var (length, offset) = a.TryGetValue( "BYTERANGE", out var br ) ? ByteRange( br ) : (null, null);
				map = new Map { Uri = new Uri( playlist, mapUri ), RangeLength = length, RangeOffset = offset };
			}
			else if ( line.StartsWith( "#EXT-X-KEY" ) && !line.Contains( "METHOD=NONE" ) )
				throw new ResolveException( line.Contains( "SAMPLE-AES" ) || line.Contains( "KEYFORMAT" ) ? "This video is DRM protected, so it can't be played." : "This HLS stream is encrypted." );
			else if ( !line.StartsWith( "#" ) )
			{
				var uri = new Uri( playlist, line );
				var segment = new Segment { Uri = uri, Start = at, Duration = duration, Discontinuity = discontinuity, Map = map };
				if ( rangeLength is { } n )
				{
					// a byte range without an offset carries on from the last one of the same file
					var key = uri.ToString();
					segment.RangeOffset = rangeOffset ?? nextOffset.GetValueOrDefault( key );
					segment.RangeLength = n;
					nextOffset[key] = segment.RangeOffset.Value + n;
				}
				list.Segments.Add( segment );
				at += duration;
				duration = 0;
				discontinuity = false;
				rangeLength = rangeOffset = null;
			}
		}
		return list;
	}

	/// <summary> "length[@offset]". </summary>
	static (long? length, long? offset) ByteRange( string s )
	{
		var p = s.Trim().Split( '@' );
		long? length = long.TryParse( p[0], out var n ) ? n : null;
		long? offset = p.Length > 1 && long.TryParse( p[1], out var o ) ? o : null;
		return (length, offset);
	}

	static Dictionary<string, string> Attributes( string line )
	{
		var result = new Dictionary<string, string>();
		var body = line[(line.IndexOf( ':' ) + 1)..];
		foreach ( System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches( body, "([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)" ) )
			result[m.Groups[1].Value] = m.Groups[2].Value.Trim( '"' );
		return result;
	}
}
