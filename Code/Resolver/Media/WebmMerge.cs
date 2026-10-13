using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Bimp.Resolver.Media;

/// <summary> A cue: a cluster starting at <see cref="Time"/> (timecode units), at <see cref="Position"/> relative to the segment data. </summary>
public readonly record struct CuePoint( long Time, long Position );

/// <summary>
/// A single track WebM file on a server (how YouTube serves HD: separate video and audio files), read with
/// range requests. Only the header, tracks and cues are read up front.
/// </summary>
public sealed partial class WebmSource
{
	public const int ProbeBytes = 256 * 1024;

	public RemoteFile File { get; }
	public byte[] EbmlHeader { get; private set; }
	public long SegmentDataOffset { get; private set; }
	public long SegmentEnd { get; private set; }
	public ulong TimecodeScale { get; private set; } = 1000000;

	/// <summary> In timecode units. </summary>
	public double Duration { get; private set; }

	/// <summary> The raw TrackEntry element. </summary>
	public byte[] TrackEntry { get; private set; }
	public ulong TrackNumber { get; private set; } = 1;

	/// <summary> One per cluster, in file order. </summary>
	public List<CuePoint> Cues { get; private set; }

	WebmSource( RemoteFile file ) { File = file; }

	public long ClusterStart( int index ) => SegmentDataOffset + Cues[index].Position;
	public long ClusterEnd( int index ) => index + 1 < Cues.Count ? SegmentDataOffset + Cues[index + 1].Position : SegmentEnd;

	/// <summary> The last cue starting at or before this time (timecode units). </summary>
	public int IndexAt( long time )
	{
		var best = 0;
		for ( int i = 0; i < Cues.Count; i++ )
		{
			if ( Cues[i].Time <= time ) best = i;
			else break;
		}
		return best;
	}

	/// <summary> The first cue starting at or after this time, or Cues.Count. </summary>
	public int FirstIndexFrom( long time )
	{
		for ( int i = 0; i < Cues.Count; i++ )
			if ( Cues[i].Time >= time ) return i;
		return Cues.Count;
	}

	public double SecondsOf( long time ) => time * (double)TimecodeScale / 1e9;
	public long TimeOf( double seconds ) => (long)(seconds * 1e9 / TimecodeScale);

	/// <summary>
	/// Read the header, tracks and cues of a WebM file with range requests.
	/// </summary>
	public static async Task<WebmSource> ProbeAsync( RemoteFile file, CancellationToken ct )
	{
		var src = new WebmSource( file );
		var buf = await file.ProbeAsync( ProbeBytes, ct );

		var top = Ebml.ReadElement( buf, 0 );
		if ( top.Id != Ebml.EBML ) throw new MergeException( "not a webm file" );
		src.EbmlHeader = buf.AsSpan( 0, (int)top.End ).ToArray();

		var seg = Ebml.ReadElement( buf, (int)top.End );
		if ( seg.Id != Ebml.Segment ) throw new MergeException( "no segment" );
		src.SegmentDataOffset = seg.DataOffset;
		src.SegmentEnd = seg.Size == Ebml.UnknownSize ? file.Size : Math.Min( file.Size, seg.End );

		Ebml.Element? info = null, tracks = null, cues = null;
		foreach ( var el in Ebml.ChildrenPartial( buf, seg.DataOffset, buf.Length ) )
		{
			if ( el.Id == Ebml.Info ) info = el;
			else if ( el.Id == Ebml.Tracks ) tracks = el;
			else if ( el.Id == Ebml.Cues ) cues = el;
			else if ( el.Id == Ebml.Cluster ) break;
			if ( el.End > buf.Length ) break;
		}

		if ( info is not { } infoEl || tracks is not { } tracksEl || infoEl.End > buf.Length || tracksEl.End > buf.Length )
			throw new MergeException( "couldn't find info/tracks in the header" );
		if ( cues is not { } cuesEl )
			throw new MergeException( "no cues before the first cluster" );

		foreach ( var c in Ebml.Children( buf, infoEl.DataOffset, infoEl.End ) )
		{
			if ( c.Id == Ebml.TimecodeScale ) src.TimecodeScale = Ebml.ReadUInt( Ebml.Data( buf, c ) );
			else if ( c.Id == Ebml.Duration ) src.Duration = Ebml.ReadFloat( Ebml.Data( buf, c ) );
		}

		// Tracks - we only handle single track files
		var entries = Ebml.Children( buf, tracksEl.DataOffset, tracksEl.End ).Where( c => c.Id == Ebml.TrackEntry ).ToList();
		if ( entries.Count != 1 ) throw new MergeException( $"expected 1 track, found {entries.Count}" );
		var entry = entries[0];
		src.TrackEntry = buf.AsSpan( (int)entry.Offset, (int)(entry.End - entry.Offset) ).ToArray();
		foreach ( var c in Ebml.Children( buf, entry.DataOffset, entry.End ) )
			if ( c.Id == Ebml.TrackNumber ) src.TrackNumber = Ebml.ReadUInt( Ebml.Data( buf, c ) );

		// Cues - may extend past what we probed
		var cuesBuf = buf;
		long cbase = 0;
		if ( cuesEl.End > buf.Length )
		{
			cuesBuf = await file.ReadAsync( cuesEl.Offset, cuesEl.End, ct );
			cbase = cuesEl.Offset;
		}

		var list = new List<CuePoint>();
		foreach ( var cp in Ebml.Children( cuesBuf, cuesEl.DataOffset - cbase, cuesEl.End - cbase, cbase ) )
		{
			if ( cp.Id != Ebml.CuePoint ) continue;
			long? t = null, pos = null;
			foreach ( var c in Ebml.Children( cuesBuf, cp.DataOffset - cbase, cp.End - cbase, cbase ) )
			{
				if ( c.Id == Ebml.CueTime ) t = (long)Ebml.ReadUInt( Ebml.Data( cuesBuf, c, cbase ) );
				else if ( c.Id == Ebml.CueTrackPositions )
				{
					foreach ( var cc in Ebml.Children( cuesBuf, c.DataOffset - cbase, c.End - cbase, cbase ) )
						if ( cc.Id == Ebml.CueClusterPosition ) pos = (long)Ebml.ReadUInt( Ebml.Data( cuesBuf, cc, cbase ) );
				}
			}
			if ( t.HasValue && pos.HasValue ) list.Add( new CuePoint( t.Value, pos.Value ) );
		}

		// one entry per cluster, in order
		src.Cues = list.OrderBy( c => c.Position ).DistinctBy( c => c.Position ).ToList();
		if ( src.Cues.Count == 0 ) throw new MergeException( "empty cues" );
		return src;
	}
}

public class MergeException : Exception
{
	public MergeException( string message ) : base( message ) { }
}

/// <summary> One block of a single track file. </summary>
public sealed class MediaBlock
{
	/// <summary> Absolute, in timecode units. </summary>
	public long Time;
	/// <summary> <see cref="Ebml.SimpleBlock"/> or <see cref="Ebml.BlockGroup"/>. </summary>
	public uint Kind;
	/// <summary> Element data (without the element header). </summary>
	public byte[] Body;
}

/// <summary> A seek point of an output file: starts with a cluster, one cue points at it. </summary>
public sealed class MuxEntry
{
	public long Time;

	/// <summary> Literal bytes (a ready made cluster). Written first, then <see cref="Clusters"/>. </summary>
	public List<byte[]> Parts = new();

	/// <summary> Clusters described by slices of other buffers, never copied into one array (see <see cref="MuxCluster"/>). Written after <see cref="Parts"/>. </summary>
	public List<MuxCluster> Clusters = new();

	public long Length
	{
		get
		{
			long total = 0;
			foreach ( var p in Parts ) total += p.Length;
			foreach ( var c in Clusters ) total += c.Length;
			return total;
		}
	}
}

/// <summary> A range of a byte array. </summary>
public readonly struct MuxSlice
{
	public readonly byte[] Array;
	public readonly int Offset;
	public readonly int Length;

	public MuxSlice( byte[] array, int offset, int length )
	{
		Array = array;
		Offset = offset;
		Length = length;
	}
}

/// <summary>
/// A Cluster element that isn't built in memory: its header is computed from the total length of its pieces, and
/// the pieces (the video blocks are slices of the downloaded file) go straight to the output in
/// <see cref="WebmMux.Write"/>. Add every piece before asking for <see cref="Length"/> or writing it.
/// </summary>
public sealed class MuxCluster
{
	readonly List<MuxSlice> slices = new();
	long dataLength;
	byte[] header;

	/// <summary> A finished element (header included) as a cluster of its own. </summary>
	public static MuxCluster FromElement( byte[] element )
	{
		var cluster = new MuxCluster { header = Array.Empty<byte>() };
		cluster.Add( element );
		return cluster;
	}

	public void Add( byte[] array ) => Add( array, 0, array.Length );

	public void Add( byte[] array, int offset, int length )
	{
		if ( length == 0 ) return;
		slices.Add( new MuxSlice( array, offset, length ) );
		dataLength += length;
	}

	/// <summary> Element ID and size, as <see cref="Ebml.Make"/> would have written them for the whole data. </summary>
	byte[] Header => header ??= Ebml.Header( Ebml.Cluster, dataLength );

	/// <summary> Header and data. </summary>
	public long Length => Header.Length + dataLength;

	internal void WriteTo( MuxWriter writer )
	{
		writer.Write( Header );
		for ( int i = 0; i < slices.Count; i++ )
			writer.Write( slices[i].Array, slices[i].Offset, slices[i].Length );
	}

	/// <summary> The whole element in one array (copies it - for callers that need bytes). </summary>
	public byte[] ToArray()
	{
		var result = new byte[Length];
		var h = Header;
		Buffer.BlockCopy( h, 0, result, 0, h.Length );
		var o = h.Length;
		for ( int i = 0; i < slices.Count; i++ )
		{
			Buffer.BlockCopy( slices[i].Array, slices[i].Offset, result, o, slices[i].Length );
			o += slices[i].Length;
		}
		return result;
	}
}

/// <summary>
/// Buffers small writes (block headers, timecodes) so the output stream sees few, large writes - and passes big ones
/// (video blocks) straight through from where they are.
/// </summary>
public sealed class MuxWriter : IDisposable
{
	const int BufferSize = 64 * 1024;

	readonly Stream output;
	byte[] buffer = ArrayPool<byte>.Shared.Rent( BufferSize );
	int used;

	public MuxWriter( Stream output ) { this.output = output; }

	public void Write( byte[] data ) => Write( data, 0, data.Length );

	public void Write( byte[] data, int offset, int length )
	{
		if ( length >= buffer.Length / 2 )
		{
			Flush();
			output.Write( data.AsSpan( offset, length ) );
			return;
		}
		if ( used + length > buffer.Length ) Flush();
		Buffer.BlockCopy( data, offset, buffer, used, length );
		used += length;
	}

	public void Flush()
	{
		if ( used > 0 ) output.Write( buffer.AsSpan( 0, used ) );
		used = 0;
	}

	public void Dispose()
	{
		if ( buffer is null ) return;
		ArrayPool<byte>.Shared.Return( buffer );
		buffer = null;
	}
}

/// <summary> A video block as it goes into an output cluster: <see cref="Head"/> (if any), then a slice of <see cref="Array"/>. </summary>
public readonly struct VideoBlock
{
	/// <summary> Absolute, in timecode units. </summary>
	public readonly long Time;
	/// <summary> Bytes in front of the slice (an MP4 sample's block element header), or null. </summary>
	public readonly byte[] Head;
	public readonly byte[] Array;
	public readonly int Offset;
	public readonly int Length;

	public VideoBlock( long time, byte[] head, byte[] array, int offset, int length )
	{
		Time = time;
		Head = head;
		Array = array;
		Offset = offset;
		Length = length;
	}
}

/// <summary> A video cluster's timecode and its block elements, by reference. </summary>
public sealed class VideoCluster
{
	public long Tc;
	public List<VideoBlock> Blocks = new();
}

/// <summary>
/// Builds standalone WebM files out of a separate video WebM and audio WebM, without re-encoding.
/// A port of the old resolver's webmmerge.py. Video clusters keep their timecodes and blocks byte for byte; the
/// audio's blocks are woven in among them by time.
/// <para>
/// Layout: EBML header | Segment [ SeekHead Info Tracks | pre | V0+A0 | V1+A1 | ... | Cues ], each cluster's audio woven in among its video.
/// The Cues must be at the END - the s&amp;box reader fetches the last 256KB of a file to find them.
/// Timestamps stay absolute, so a file that starts at a later cue point plays at the right media time.
/// </para>
/// </summary>
public static class WebmMux
{
	/// <summary> Block timecodes are int16 relative to their cluster. </summary>
	public const long MaxAudioClusterMs = 30000;

	/// <summary>
	/// Parse every block of a run of consecutive clusters in buf[start..end].
	/// </summary>
	public static List<MediaBlock> ParseBlocks( byte[] buf, int start, int end )
	{
		var blocks = new List<MediaBlock>();

		foreach ( var cl in Ebml.ChildrenPartial( buf, start, end ) )
		{
			if ( cl.Id != Ebml.Cluster ) continue;
			var clEnd = cl.Size == Ebml.UnknownSize ? end : Math.Min( cl.End, end );
			long tc = 0;

			foreach ( var c in Ebml.ChildrenPartial( buf, cl.DataOffset, clEnd ) )
			{
				if ( c.End > clEnd ) break; // cut off
				if ( c.Id == Ebml.Timecode )
				{
					tc = (long)Ebml.ReadUInt( Ebml.Data( buf, c ) );
				}
				else if ( c.Id == Ebml.SimpleBlock )
				{
					var body = Ebml.Data( buf, c ).ToArray();
					blocks.Add( new MediaBlock { Time = tc + BlockRelativeTime( body ), Kind = Ebml.SimpleBlock, Body = body } );
				}
				else if ( c.Id == Ebml.BlockGroup )
				{
					var body = Ebml.Data( buf, c ).ToArray();
					foreach ( var g in Ebml.Children( body, 0, body.Length ) )
					{
						if ( g.Id != Ebml.Block ) continue;
						var rel = BlockRelativeTime( Ebml.Data( body, g ) );
						blocks.Add( new MediaBlock { Time = tc + rel, Kind = Ebml.BlockGroup, Body = body } );
						break;
					}
				}
			}
		}

		blocks.Sort( ( a, b ) => a.Time.CompareTo( b.Time ) );
		return blocks;
	}

	static short BlockRelativeTime( ReadOnlySpan<byte> payload )
	{
		var (_, n) = Ebml.ReadSize( payload, 0 ); // track number vint
		return BinaryPrimitives.ReadInt16BigEndian( payload.Slice( n, 2 ) );
	}

	/// <summary> A Block / SimpleBlock element (header included) with its track number and relative time rewritten, in one array. </summary>
	static byte[] RewrittenBlock( uint id, ReadOnlySpan<byte> payload, ulong track, long rel )
	{
		var (_, n) = Ebml.ReadSize( payload, 0 );
		var head = Ebml.EncodeSize( (long)track, 1 );
		var dataLength = head.Length + 2 + payload.Length - n - 2;
		var result = Ebml.Header( id, dataLength, dataLength );
		var o = result.Length - dataLength;
		head.CopyTo( result, o );
		BinaryPrimitives.WriteInt16BigEndian( result.AsSpan( o + head.Length, 2 ), checked((short)rel) );
		payload[(n + 2)..].CopyTo( result.AsSpan( o + head.Length + 2 ) );
		return result;
	}

	static byte[] Cluster( List<MediaBlock> blocks, ulong track )
	{
		var tc = blocks[0].Time;
		var parts = new List<byte[]> { Ebml.UIntElement( Ebml.Timecode, (ulong)tc ) };

		foreach ( var b in blocks )
			parts.Add( BlockElement( b, track, b.Time - tc ) );

		return Ebml.Make( Ebml.Cluster, Ebml.Concat( parts ) );
	}

	/// <summary> A block as an element for a cluster: its track number and cluster-relative time rewritten. </summary>
	static byte[] BlockElement( MediaBlock b, ulong track, long rel )
	{
		if ( b.Kind == Ebml.SimpleBlock )
			return RewrittenBlock( Ebml.SimpleBlock, b.Body, track, rel );

		var group = new List<byte[]>();
		foreach ( var g in Ebml.Children( b.Body, 0, b.Body.Length ) )
		{
			if ( g.Id == Ebml.Block )
				group.Add( RewrittenBlock( Ebml.Block, Ebml.Data( b.Body, g ), track, rel ) );
			else
				group.Add( b.Body.AsSpan( (int)g.Offset, (int)(g.End - g.Offset) ).ToArray() );
		}
		return Ebml.Make( Ebml.BlockGroup, Ebml.Concat( group ) );
	}

	/// <summary>
	/// Group blocks into clusters of at most <paramref name="maxTime"/> timecode units. Returns (start time, cluster bytes).
	/// </summary>
	public static List<(long time, byte[] cluster)> Clusters( List<MediaBlock> blocks, ulong track, long maxTime = MaxAudioClusterMs )
	{
		var result = new List<(long, byte[])>();
		var group = new List<MediaBlock>();
		foreach ( var b in blocks )
		{
			if ( group.Count > 0 && b.Time - group[0].Time >= maxTime )
			{
				result.Add( (group[0].Time, Cluster( group, track )) );
				group = new List<MediaBlock>();
			}
			group.Add( b );
		}
		if ( group.Count > 0 ) result.Add( (group[0].Time, Cluster( group, track )) );
		return result;
	}

	/// <summary>
	/// The clusters in buf[start..end] with the audio blocks for the same time woven in among their video blocks, by
	/// time - each cluster keeps its timecode and starts with the same (key)frame, and its video block elements are
	/// copied as they are. Audio past the last cluster (up to the end of <paramref name="audio"/>) follows in clusters of
	/// its own. <paramref name="videoEnd"/> is where the last cluster's time span ends. Video blocks from
	/// <paramref name="videoCut"/> on are left out; <paramref name="lastVideo"/> gets the time of the last one kept.
	/// </summary>
	public static List<byte[]> Interleave( byte[] buf, int start, int end, long videoEnd, List<MediaBlock> audio, ulong audioTrack, long videoCut, ref long lastVideo )
	{
		// the materialising form: every cluster in an array of its own
		var result = new List<byte[]>();
		foreach ( var cluster in InterleaveStreamed( buf, start, end, videoEnd, audio, audioTrack, videoCut, ref lastVideo ) )
			result.Add( cluster.ToArray() );
		return result;
	}

	/// <summary>
	/// Like <see cref="Interleave"/>, but the clusters stay descriptions (slices of <paramref name="buf"/> for the video
	/// blocks, small arrays for the rest): add them to <see cref="MuxEntry.Clusters"/> and <see cref="Write"/> streams
	/// them out. Nothing the size of a cluster is allocated. The output bytes are the same.
	/// </summary>
	public static List<MuxCluster> InterleaveStreamed( byte[] buf, int start, int end, long videoEnd, List<MediaBlock> audio, ulong audioTrack, long videoCut, ref long lastVideo )
		=> InterleaveStreamed( ParseVideoClusters( buf, start, end ), videoEnd, audio, audioTrack, videoCut, ref lastVideo );

	/// <summary> The video clusters of buf[start..end] with every block element as a slice of <paramref name="buf"/>. </summary>
	public static List<VideoCluster> ParseVideoClusters( byte[] buf, int start, int end )
	{
		var clusters = new List<VideoCluster>();
		foreach ( var cl in Ebml.ChildrenPartial( buf, start, end ) )
		{
			if ( cl.Id != Ebml.Cluster ) continue;
			var clEnd = cl.Size == Ebml.UnknownSize ? end : Math.Min( cl.End, end );
			var cluster = new VideoCluster();
			foreach ( var c in Ebml.ChildrenPartial( buf, cl.DataOffset, clEnd ) )
			{
				if ( c.End > clEnd ) break; // cut off
				if ( c.Id == Ebml.Timecode ) cluster.Tc = (long)Ebml.ReadUInt( Ebml.Data( buf, c ) );
				else if ( c.Id == Ebml.SimpleBlock || c.Id == Ebml.BlockGroup )
				{
					var time = cluster.Tc + BlockRelativeTime( buf, c );
					cluster.Blocks.Add( new VideoBlock( time, null, buf, (int)c.Offset, (int)(c.End - c.Offset) ) );
				}
			}
			clusters.Add( cluster );
		}
		return clusters;
	}

	/// <summary>
	/// <see cref="Interleave"/> over video clusters that are already parsed (the AV1 path builds them from MP4 samples).
	/// </summary>
	public static List<MuxCluster> InterleaveStreamed( List<VideoCluster> video, long videoEnd, List<MediaBlock> audio, ulong audioTrack, long videoCut, ref long lastVideo )
	{
		// drop the blocks from videoCut on, and the clusters left empty
		var clusters = new List<(long tc, List<VideoBlock> blocks)>();
		foreach ( var cl in video )
		{
			var blocks = cl.Blocks;
			foreach ( var vb in cl.Blocks )
			{
				if ( vb.Time < videoCut ) continue;
				blocks = cl.Blocks.FindAll( x => x.Time < videoCut );
				break;
			}
			if ( blocks.Count == 0 ) continue;
			clusters.Add( (cl.Tc, blocks) );
			foreach ( var vb in blocks ) lastVideo = Math.Max( lastVideo, vb.Time );
		}

		var result = new List<MuxCluster>();
		var ai = 0;
		for ( int i = 0; i < clusters.Count; i++ )
		{
			var (tc, blocks) = clusters[i];
			var spanEnd = i + 1 < clusters.Count ? clusters[i + 1].tc : videoEnd;
			var cluster = new MuxCluster();
			cluster.Add( Ebml.UIntElement( Ebml.Timecode, (ulong)tc ) );

			// Each audio block goes just ahead of the first video block after its time - but never ahead of the cluster's
			// first (key)frame: the engine didn't show a segment whose first cluster started with an audio block.
			bool AudioFits( MediaBlock a ) => a.Time < spanEnd && a.Time - tc <= short.MaxValue;
			for ( int b = 0; b < blocks.Count; b++ )
			{
				var v = blocks[b];
				while ( b > 0 && ai < audio.Count && audio[ai].Time < v.Time && AudioFits( audio[ai] ) )
					cluster.Add( BlockElement( audio[ai], audioTrack, audio[ai++].Time - tc ) );
				if ( v.Head is not null ) cluster.Add( v.Head );
				cluster.Add( v.Array, v.Offset, v.Length ); // straight from the source buffer
			}
			while ( ai < audio.Count && AudioFits( audio[ai] ) )
				cluster.Add( BlockElement( audio[ai], audioTrack, audio[ai++].Time - tc ) );

			result.Add( cluster );
		}

		if ( ai < audio.Count )
		{
			foreach ( var c in Clusters( audio.GetRange( ai, audio.Count - ai ), audioTrack ) )
				result.Add( MuxCluster.FromElement( c.cluster ) );
		}
		return result;
	}

	/// <summary> A block element's timecode, relative to its cluster: after its track number, in a SimpleBlock or a BlockGroup's Block. </summary>
	static short BlockRelativeTime( byte[] buf, Ebml.Element el )
	{
		if ( el.Id == Ebml.SimpleBlock ) return BlockRelativeTime( Ebml.Data( buf, el ) );
		foreach ( var g in Ebml.Children( buf, el.DataOffset, el.End ) )
			if ( g.Id == Ebml.Block ) return BlockRelativeTime( Ebml.Data( buf, g ) );
		return 0;
	}

	public static byte[] RenumberTrack( byte[] entry, ulong number, ulong uid )
	{
		var el = Ebml.ReadElement( entry, 0 );
		var parts = new List<byte[]>();
		foreach ( var c in Ebml.Children( entry, el.DataOffset, el.End ) )
		{
			if ( c.Id == Ebml.TrackNumber ) parts.Add( Ebml.UIntElement( Ebml.TrackNumber, number ) );
			else if ( c.Id == Ebml.TrackUid ) parts.Add( Ebml.UIntElement( Ebml.TrackUid, uid, 8 ) );
			else parts.Add( entry.AsSpan( (int)c.Offset, (int)(c.End - c.Offset) ).ToArray() );
		}
		return Ebml.Make( Ebml.TrackEntry, Ebml.Concat( parts ) );
	}

	const uint AudioSettings = 0xE1;
	const uint Channels = 0x9F;
	const uint CodecPrivate = 0x63A2;

	/// <summary>
	/// Declare a mono Opus track as stereo (TrackEntry Audio/Channels and the OpusHead channel count).
	/// <para>
	/// The engine's Opus decoder always outputs stereo, but it sizes the audio stream from the track's channel
	/// count - so a mono track is played as stereo samples squeezed into a mono stream: garbled, crackling audio
	/// (measured with a sine: clean as "2 channels", broken as "1"). Declaring it stereo is valid Opus: a stereo
	/// decoder upmixes mono packets.
	/// </para>
	/// </summary>
	public static byte[] FixMonoOpus( byte[] entry )
	{
		var el = Ebml.ReadElement( entry, 0 );
		var children = Ebml.Children( entry, el.DataOffset, el.End );

		var isOpus = children.Any( c => c.Id == CodecPrivate && Ebml.Data( entry, c ).StartsWith( "OpusHead"u8 ) );
		if ( !isOpus ) return entry;

		var parts = new List<byte[]>();
		var changed = false;
		foreach ( var c in children )
		{
			var raw = entry.AsSpan( (int)c.Offset, (int)(c.End - c.Offset) ).ToArray();

			if ( c.Id == AudioSettings )
			{
				var audio = new List<byte[]>();
				foreach ( var a in Ebml.Children( entry, c.DataOffset, c.End ) )
				{
					if ( a.Id == Channels && Ebml.ReadUInt( Ebml.Data( entry, a ) ) == 1 )
					{
						audio.Add( Ebml.UIntElement( Channels, 2 ) );
						changed = true;
					}
					else audio.Add( entry.AsSpan( (int)a.Offset, (int)(a.End - a.Offset) ).ToArray() );
				}
				raw = Ebml.Make( AudioSettings, Ebml.Concat( audio ) );
			}
			else if ( c.Id == CodecPrivate )
			{
				var head = Ebml.Data( entry, c ).ToArray();
				// OpusHead: magic(8) version(1) channels(1) ... - only mapping family 0 (mono/stereo) can change
				if ( head.Length >= 19 && head[9] == 1 && head[18] == 0 )
				{
					head[9] = 2;
					raw = Ebml.Make( CodecPrivate, head );
				}
			}

			parts.Add( raw );
		}

		return changed ? Ebml.Make( Ebml.TrackEntry, Ebml.Concat( parts ) ) : entry;
	}

	/// <summary> entries: (element id, position), positions written as fixed 8 byte uints so the size never changes. </summary>
	static byte[] SeekHead( params (uint id, long pos)[] entries )
	{
		var seeks = entries.Select( e => Ebml.Make( Ebml.Seek,
			Ebml.Concat( Ebml.Make( Ebml.SeekId, Ebml.EncodeId( e.id ) ), Ebml.UIntElement( Ebml.SeekPosition, (ulong)e.pos, 8 ) ) ) );
		return Ebml.Make( Ebml.SeekHead, Ebml.Concat( seeks ) );
	}

	/// <summary> Fixed width, so the size doesn't depend on the values. </summary>
	static byte[] CuesElement( List<(long time, long pos)> points, ulong track )
	{
		var cps = points.Select( p => Ebml.Make( Ebml.CuePoint, Ebml.Concat(
			Ebml.UIntElement( Ebml.CueTime, (ulong)p.time, 8 ),
			Ebml.Make( Ebml.CueTrackPositions, Ebml.Concat(
				Ebml.UIntElement( Ebml.CueTrack, track, 1 ),
				Ebml.UIntElement( Ebml.CueClusterPosition, (ulong)p.pos, 8 ) ) ) ) ) );
		return Ebml.Make( Ebml.Cues, Ebml.Concat( cps ), 8 );
	}

	/// <summary>
	/// Write a complete WebM file.
	/// </summary>
	/// <param name="ebmlHeader">The source's EBML header element, copied as-is</param>
	/// <param name="timecodeScale">Timecode scale shared by every track</param>
	/// <param name="duration">Duration of the whole media, in timecode units</param>
	/// <param name="tracks">Complete Tracks element</param>
	/// <param name="cueTrack">Track the cues refer to (the video track, or the audio track for audio only)</param>
	/// <param name="entries">Seek points, in order</param>
	/// <param name="pre">Literal bytes before the first entry (audio from before the first video cue), may be empty</param>
	public static void Write( Stream output, byte[] ebmlHeader, ulong timecodeScale, double duration, byte[] tracks, ulong cueTrack, List<MuxEntry> entries, byte[] pre )
	{
		pre ??= Array.Empty<byte>();

		var info = Ebml.Make( Ebml.Info, Ebml.Concat(
			Ebml.UIntElement( Ebml.TimecodeScale, timecodeScale ),
			Ebml.FloatElement( Ebml.Duration, duration ),
			Ebml.StringElement( Ebml.MuxingApp, "bimp" ),
			Ebml.StringElement( Ebml.WritingApp, "bimp" ) ) );

		var seekHeadLength = SeekHead( (Ebml.Info, 0), (Ebml.Tracks, 0), (Ebml.Cues, 0) ).Length;
		long infoPos = seekHeadLength;
		long tracksPos = infoPos + info.Length;
		long pos = tracksPos + tracks.Length + pre.Length; // relative to the segment data

		var points = new List<(long, long)>();
		foreach ( var e in entries )
		{
			points.Add( (e.Time, pos) );
			pos += e.Length;
		}

		var cuesPos = pos;
		var cues = CuesElement( points, cueTrack );
		var segmentDataLength = cuesPos + cues.Length;
		var seekHead = SeekHead( (Ebml.Info, infoPos), (Ebml.Tracks, tracksPos), (Ebml.Cues, cuesPos) );

		// second pass: the sizes above came from lengths alone, the pieces (video blocks) are written from where they are
		using var writer = new MuxWriter( output );
		writer.Write( ebmlHeader );
		writer.Write( Ebml.EncodeId( Ebml.Segment ) );
		writer.Write( Ebml.EncodeSize( segmentDataLength, 8 ) );
		writer.Write( seekHead );
		writer.Write( info );
		writer.Write( tracks );
		writer.Write( pre );
		foreach ( var e in entries )
		{
			foreach ( var p in e.Parts )
				writer.Write( p );
			foreach ( var c in e.Clusters )
				c.WriteTo( writer );
		}
		writer.Write( cues );
		writer.Flush();
	}
}
