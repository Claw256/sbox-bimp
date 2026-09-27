using System.Buffers.Binary;
using System.Threading;

namespace Bimp.Resolver.Media;

/// <summary>
/// A fragmented MP4 (DASH) video file, read as if it were a single track WebM - how YouTube serves AV1. Its fragment
/// index (sidx) becomes the cues, and the fragments are turned into WebM clusters as they're read, so the segmenter
/// merges it exactly like a VP9 WebM. The samples are copied as they are: AV1 in MP4 and in Matroska is the same
/// low-overhead OBU stream.
/// <para>
/// Why: the engine's VP9 decoder can't keep up with 4K at 60 fps (19-45 frames a second measured), while its AV1
/// decoder plays the same 4K60 video at a steady 60.
/// </para>
/// </summary>
public sealed partial class WebmSource
{
	/// <summary> The file is fragmented MP4: clusters are made from its fragments (<see cref="ClustersFromMp4"/>). </summary>
	public bool IsMp4 { get; private set; }

	/// <summary> The MP4 track's time units per second. </summary>
	long mp4Timescale;

	/// <summary> The MP4's one track (codec, config, trex defaults). </summary>
	Fmp4Reader.Track mp4Track;

	const ulong Mp4TimecodeScale = 1000000; // ms, like YouTube's WebM files - the audio has to match

	/// <summary>
	/// Read the header (moov) and fragment index (sidx) of a fragmented MP4 AV1 file with range requests.
	/// </summary>
	public static async Task<WebmSource> ProbeMp4Async( RemoteFile file, CancellationToken ct )
	{
		var src = new WebmSource( file ) { IsMp4 = true, TimecodeScale = Mp4TimecodeScale, TrackNumber = 1 };
		var buf = await file.ProbeAsync( ProbeBytes, ct );

		int moov = -1, moovEnd = 0;
		long sidx = -1, sidxEnd = 0;
		for ( long at = 0; at + 8 <= buf.Length; )
		{
			var (size, type, _) = Fmp4Reader.Box( buf, (int)at, file.Size - at );
			if ( size <= 0 ) break;
			if ( type == "moov" ) { moov = (int)at; moovEnd = (int)Math.Min( at + size, buf.Length ); }
			else if ( type == "sidx" ) { sidx = at; sidxEnd = at + size; }
			else if ( type is "moof" or "mdat" ) break;
			at += size;
		}
		if ( moov < 0 || moovEnd <= moov ) throw new MergeException( "no moov in the mp4 header" );
		if ( sidx < 0 ) throw new MergeException( "no sidx (not a DASH mp4)" );

		src.ReadMoov( buf, moov, moovEnd );

		// the index may run past what we probed
		var index = sidxEnd <= buf.Length ? buf : await file.ReadAsync( sidx, sidxEnd, ct );
		var indexAt = sidxEnd <= buf.Length ? (int)sidx : 0;
		src.ReadSidx( index, indexAt, sidxEnd );

		src.EbmlHeader = Ebml.Make( Ebml.EBML, Ebml.Concat(
			Ebml.UIntElement( 0x4286, 1 ), // EBMLVersion
			Ebml.UIntElement( 0x42F7, 1 ), // EBMLReadVersion
			Ebml.UIntElement( 0x42F2, 4 ), // EBMLMaxIDLength
			Ebml.UIntElement( 0x42F3, 8 ), // EBMLMaxSizeLength
			Ebml.StringElement( 0x4282, "webm" ), // DocType
			Ebml.UIntElement( 0x4287, 4 ), // DocTypeVersion
			Ebml.UIntElement( 0x4285, 2 ) ) ); // DocTypeReadVersion
		return src;
	}

	void ReadMoov( byte[] d, int moov, int end )
	{
		var tracks = Fmp4Reader.ReadTracks( d, moov, end );
		if ( tracks.Count != 1 ) throw new MergeException( $"expected 1 track in the mp4, found {tracks.Count}" );
		mp4Track = tracks[0];
		if ( mp4Track.Codec != "av01" ) throw new MergeException( $"unsupported mp4 video codec '{mp4Track.Codec}'" );
		if ( mp4Track.Config is null ) throw new MergeException( "no av1C" );
		mp4Timescale = mp4Track.Timescale;
		Duration = mp4Track.DurationSeconds * 1000.0;

		TrackEntry = Ebml.Make( Ebml.TrackEntry, Ebml.Concat(
			Ebml.UIntElement( Ebml.TrackNumber, 1 ),
			Ebml.UIntElement( Ebml.TrackUid, 0x61763031UL, 8 ),
			Ebml.UIntElement( 0x83, 1 ), // TrackType: video
			Ebml.UIntElement( 0x9C, 0 ), // FlagLacing
			Ebml.StringElement( 0x86, "V_AV1" ), // CodecID
			Ebml.Make( 0x63A2, mp4Track.Config ), // CodecPrivate: the AV1CodecConfigurationRecord, as in av1C
			Ebml.Make( 0xE0, Ebml.Concat( Ebml.UIntElement( 0xB0, (ulong)mp4Track.Width ), Ebml.UIntElement( 0xBA, (ulong)mp4Track.Height ) ) ) ) ); // Video
	}

	/// <summary> The fragment index: one cue per referenced fragment (each starts at a keyframe on YouTube). </summary>
	void ReadSidx( byte[] d, int at, long fileEnd )
	{
		var (size, _, h) = Fmp4Reader.Box( d, at, d.Length - at );
		var p = at + h;
		var version = d[p];
		p += 4;
		var timescale = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p + 4 ) );
		p += 8;
		long time, offset;
		if ( version == 0 )
		{
			time = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) );
			offset = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p + 4 ) );
			p += 8;
		}
		else
		{
			time = (long)BinaryPrimitives.ReadUInt64BigEndian( d.AsSpan( p ) );
			offset = (long)BinaryPrimitives.ReadUInt64BigEndian( d.AsSpan( p + 8 ) );
			p += 16;
		}
		var count = BinaryPrimitives.ReadUInt16BigEndian( d.AsSpan( p + 2 ) );
		p += 4;

		// references start right after the sidx box (plus its first_offset)
		var position = fileEnd + offset;
		var cues = new List<CuePoint>();
		for ( int i = 0; i < count; i++, p += 12 )
		{
			var reference = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) );
			if ( (reference & 0x80000000) != 0 ) throw new MergeException( "nested sidx isn't supported" );
			cues.Add( new CuePoint( time * 1000 / timescale, position ) );
			position += reference & 0x7FFFFFFF;
			time += BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p + 4 ) );
		}
		if ( cues.Count == 0 ) throw new MergeException( "empty sidx" );

		Cues = cues;
		SegmentDataOffset = 0;
		SegmentEnd = Math.Min( File.Size, position );
		if ( Duration <= 0 ) Duration = time * 1000.0 / timescale;
	}

	/// <summary>
	/// The fragments (moof + mdat) in raw[start..end] as WebM clusters of SimpleBlocks: one cluster per keyframe
	/// interval, so every cluster starts with a keyframe. <paramref name="fileOffset"/> is where raw[0] is in the file.
	/// </summary>
	public byte[] ClustersFromMp4( byte[] raw, int start, int end, long fileOffset, bool partial = false )
	{
		var clusters = new List<byte[]>();
		var parts = new List<byte[]>();
		long clusterTime = 0;

		void Flush()
		{
			if ( parts.Count > 1 ) clusters.Add( Ebml.Make( Ebml.Cluster, Ebml.Concat( parts ) ) );
			parts.Clear();
		}

		foreach ( var s in Fmp4Reader.ReadSamples( raw, start, end, new[] { mp4Track }, fileOffset, partial ) )
		{
			var time = (s.DecodeTime + s.CompositionOffset) * 1000 / mp4Timescale;

			// a new cluster at each keyframe (and before the int16 relative time would overflow)
			if ( parts.Count == 0 || s.Key || time - clusterTime > 30000 )
			{
				Flush();
				clusterTime = time;
				parts.Add( Ebml.UIntElement( Ebml.Timecode, (ulong)Math.Max( time, 0 ) ) );
			}

			var block = new byte[4 + s.Size];
			block[0] = 0x81; // track 1
			BinaryPrimitives.WriteInt16BigEndian( block.AsSpan( 1 ), (short)(time - clusterTime) );
			block[3] = s.Key ? (byte)0x80 : (byte)0;
			Buffer.BlockCopy( raw, s.Offset, block, 4, s.Size );
			parts.Add( Ebml.Make( Ebml.SimpleBlock, block ) );
		}

		Flush();
		return Ebml.Concat( clusters );
	}
}
