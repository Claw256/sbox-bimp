using System.Buffers.Binary;
using System.Text;

namespace Bimp.Resolver.Media;

/// <summary>
/// Reads fragmented MP4 (the CMAF / DASH / HLS fMP4 layout): the tracks in an init segment's moov, and the samples in
/// moof + mdat fragments. Shared by YouTube's AV1 files (<see cref="WebmSource.ProbeMp4Async"/>) and fMP4 HLS.
/// </summary>
public static class Fmp4Reader
{
	/// <summary> One track of an init segment. </summary>
	public sealed class Track
	{
		public uint Id;

		/// <summary> "vide" or "soun". </summary>
		public string Handler;
		public long Timescale;
		public double DurationSeconds;

		/// <summary> The sample entry's four character code: avc1, avc3, hvc1, hev1, av01, mp4a, Opus... </summary>
		public string Codec;

		/// <summary> avcC / av1C body, or the AAC AudioSpecificConfig from the esds. </summary>
		public byte[] Config;
		public int Width, Height;
		public int Channels, SampleRate;

		// trex defaults, for fragments that don't set their own
		public uint DefaultDuration, DefaultSize, DefaultFlags;

		public bool IsVideo => Handler == "vide";
		public bool IsAudio => Handler == "soun";
	}

	/// <summary> One sample of a fragment: where its data is in the buffer, and its times in the track's timescale. </summary>
	public readonly record struct Sample( uint Track, long DecodeTime, int CompositionOffset, uint Duration, bool Key, int Offset, int Size );

	/// <summary> The tracks of the moov in d[start..end] (an init segment, or the head of a DASH file). </summary>
	public static List<Track> ReadTracks( byte[] d, int start, int end )
	{
		var moov = Find( d, start, end, "moov" ) ?? throw new MergeException( "no moov in the mp4" );
		var tracks = new List<Track>();
		var trex = new Dictionary<uint, (uint duration, uint size, uint flags)>();
		foreach ( var (at, size, type, h) in Children( d, moov.at + moov.h, moov.at + moov.size ) )
		{
			if ( type == "trak" && ReadTrak( d, at, at + size ) is { } t ) tracks.Add( t );
			else if ( type == "mvex" )
			{
				foreach ( var (x, xs, xt, xh) in Children( d, at + h, at + size ) )
				{
					if ( xt != "trex" ) continue;
					// full box: version/flags, track_ID, description index, duration, size, flags
					var p = x + xh + 4;
					trex[BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) )] = (
						BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p + 8 ) ),
						BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p + 12 ) ),
						BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p + 16 ) ));
				}
			}
		}
		foreach ( var t in tracks )
		{
			if ( trex.TryGetValue( t.Id, out var x ) ) (t.DefaultDuration, t.DefaultSize, t.DefaultFlags) = x;
		}
		return tracks;
	}

	static Track ReadTrak( byte[] d, int trak, int end )
	{
		var t = new Track();
		if ( Path( d, trak, end, "tkhd" ) is { } tkhd )
		{
			var p = tkhd.at + tkhd.h;
			t.Id = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p + (d[p] == 1 ? 20 : 12) ) );
		}

		var mdhd = Path( d, trak, end, "mdia", "mdhd" );
		if ( mdhd is null ) return null;
		var mp = mdhd.Value.at + mdhd.Value.h;
		long duration;
		if ( d[mp] == 1 )
		{
			t.Timescale = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( mp + 20 ) );
			duration = (long)BinaryPrimitives.ReadUInt64BigEndian( d.AsSpan( mp + 24 ) );
		}
		else
		{
			t.Timescale = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( mp + 12 ) );
			duration = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( mp + 16 ) );
		}
		if ( t.Timescale <= 0 ) return null;
		t.DurationSeconds = duration / (double)t.Timescale;

		if ( Path( d, trak, end, "mdia", "hdlr" ) is { } hdlr ) t.Handler = Encoding.ASCII.GetString( d, hdlr.at + hdlr.h + 8, 4 );

		var stsd = Path( d, trak, end, "mdia", "minf", "stbl", "stsd" );
		if ( stsd is null ) return t;
		var entry = stsd.Value.at + stsd.Value.h + 8; // version/flags, entry count
		var (entrySize, codec, entryHead) = Box( d, entry, stsd.Value.at + stsd.Value.size - entry );
		t.Codec = codec;
		var entryEnd = entry + (int)entrySize;

		if ( t.IsVideo )
		{
			// VisualSampleEntry: 6 reserved, data reference index, 16 bytes of pre-defined/reserved, then width and height
			t.Width = BinaryPrimitives.ReadUInt16BigEndian( d.AsSpan( entry + entryHead + 24 ) );
			t.Height = BinaryPrimitives.ReadUInt16BigEndian( d.AsSpan( entry + entryHead + 26 ) );
			var configType = codec switch { "avc1" or "avc3" => "avcC", "av01" => "av1C", "hvc1" or "hev1" => "hvcC", _ => null };
			if ( configType is not null && Find( d, entry + entryHead + 78, entryEnd, configType ) is { } c )
				t.Config = d.AsSpan( c.at + c.h, c.size - c.h ).ToArray();
		}
		else if ( t.IsAudio )
		{
			// AudioSampleEntry: 6 reserved, data reference index, 8 reserved, channels, sample size, 4 reserved, rate 16.16
			t.Channels = BinaryPrimitives.ReadUInt16BigEndian( d.AsSpan( entry + entryHead + 16 ) );
			t.SampleRate = BinaryPrimitives.ReadUInt16BigEndian( d.AsSpan( entry + entryHead + 24 ) );
			if ( codec == "mp4a" && Find( d, entry + entryHead + 28, entryEnd, "esds" ) is { } esds )
				t.Config = AudioSpecificConfig( d.AsSpan( esds.at + esds.h + 4, esds.size - esds.h - 4 ) );
		}
		return t;
	}

	/// <summary> The DecoderSpecificInfo (tag 5) inside an esds's descriptors: an AAC AudioSpecificConfig. </summary>
	static byte[] AudioSpecificConfig( ReadOnlySpan<byte> d )
	{
		var i = 0;
		while ( i + 2 <= d.Length )
		{
			var tag = d[i++];
			var length = 0;
			for ( int k = 0; k < 4 && i < d.Length; k++ )
			{
				var b = d[i++];
				length = (length << 7) | (b & 0x7F);
				if ( (b & 0x80) == 0 ) break;
			}
			if ( tag == 5 ) return d.Slice( i, Math.Min( length, d.Length - i ) ).ToArray();
			if ( tag == 3 )
			{
				// ES_Descriptor: ES_ID, flags, then optional fields - its children follow
				var flags = d[i + 2];
				i += 3;
				if ( (flags & 0x80) != 0 ) i += 2;
				if ( (flags & 0x40) != 0 ) i += 1 + d[i];
				if ( (flags & 0x20) != 0 ) i += 2;
			}
			else if ( tag == 4 ) i += 13; // DecoderConfigDescriptor: object type .. average bitrate, then children
			else i += length;
		}
		return null;
	}

	/// <summary>
	/// The samples of every fragment (moof + mdat) in d[start..end], in order. <paramref name="fileOffset"/> is where
	/// d[0] is in the file (for base data offsets); offsets in the result are into d. <paramref name="partial"/>: the
	/// last fragment may be cut off - its samples stop where the data does.
	/// </summary>
	public static List<Sample> ReadSamples( byte[] d, int start, int end, IReadOnlyList<Track> tracks, long fileOffset = 0, bool partial = false )
	{
		var samples = new List<Sample>();
		foreach ( var (moof, moofSize, type, moofHead) in Children( d, start, end ) )
		{
			if ( type != "moof" ) continue;
			foreach ( var (traf, trafSize, trafType, trafHead) in Children( d, moof + moofHead, moof + moofSize ) )
			{
				if ( trafType != "traf" ) continue;
				long baseOffset = moof, decodeTime = 0;
				uint trackId = 0, duration = 0, size = 0, flags = 0;
				Track track = null;

				foreach ( var (box, _, boxType, boxHead) in Children( d, traf + trafHead, traf + trafSize ) )
				{
					var p = box + boxHead;
					if ( boxType == "tfhd" )
					{
						var f = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) ) & 0xFFFFFF;
						trackId = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p + 4 ) );
						track = tracks.FirstOrDefault( t => t.Id == trackId ) ?? (tracks.Count == 1 ? tracks[0] : null);
						duration = track?.DefaultDuration ?? 0;
						size = track?.DefaultSize ?? 0;
						flags = track?.DefaultFlags ?? 0;
						p += 8; // version/flags, track_ID
						if ( (f & 0x01) != 0 ) { baseOffset = (long)BinaryPrimitives.ReadUInt64BigEndian( d.AsSpan( p ) ) - fileOffset; p += 8; }
						if ( (f & 0x02) != 0 ) p += 4;
						if ( (f & 0x08) != 0 ) { duration = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) ); p += 4; }
						if ( (f & 0x10) != 0 ) { size = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) ); p += 4; }
						if ( (f & 0x20) != 0 ) flags = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) );
					}
					else if ( boxType == "tfdt" )
					{
						decodeTime = d[p] == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian( d.AsSpan( p + 4 ) ) : BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p + 4 ) );
					}
					else if ( boxType == "trun" )
					{
						var version = d[p];
						var f = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) ) & 0xFFFFFF;
						var count = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p + 4 ) );
						p += 8;
						long data = baseOffset;
						if ( (f & 0x01) != 0 ) { data = baseOffset + BinaryPrimitives.ReadInt32BigEndian( d.AsSpan( p ) ); p += 4; }
						uint? firstFlags = null;
						if ( (f & 0x04) != 0 ) { firstFlags = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) ); p += 4; }

						for ( uint i = 0; i < count; i++ )
						{
							uint sDuration = duration, sSize = size, sFlags = i == 0 && firstFlags is { } ff ? ff : flags;
							var offset = 0;
							if ( (f & 0x100) != 0 ) { sDuration = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) ); p += 4; }
							if ( (f & 0x200) != 0 ) { sSize = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) ); p += 4; }
							if ( (f & 0x400) != 0 ) { sFlags = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( p ) ); p += 4; }
							if ( (f & 0x800) != 0 ) { offset = BinaryPrimitives.ReadInt32BigEndian( d.AsSpan( p ) ); if ( version == 0 ) offset = (int)Math.Min( (uint)offset, int.MaxValue ); p += 4; }

							if ( partial && data >= 0 && data + sSize > Math.Min( end, d.Length ) ) return samples;
							if ( data < 0 || data + sSize > d.Length ) throw new MergeException( "mp4 sample outside the fragment" );
							// audio samples are all sync samples, whatever the flags say
							var key = (sFlags & 0x10000) == 0 || (track?.IsAudio ?? false);
							samples.Add( new Sample( track?.Id ?? trackId, decodeTime, offset, sDuration, key, (int)data, (int)sSize ) );

							data += sSize;
							decodeTime += sDuration;
						}
					}
				}
			}
		}
		return samples;
	}

	public static (long size, string type, int headerLength) Box( byte[] d, int at, long remaining )
	{
		if ( at + 8 > d.Length ) return (0, null, 0);
		long size = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( at ) );
		var type = Encoding.ASCII.GetString( d, at + 4, 4 );
		var h = 8;
		if ( size == 1 )
		{
			if ( at + 16 > d.Length ) return (0, null, 0);
			size = (long)BinaryPrimitives.ReadUInt64BigEndian( d.AsSpan( at + 8 ) );
			h = 16;
		}
		else if ( size == 0 ) size = remaining;
		return (size < h ? 0 : size, type, h);
	}

	public static IEnumerable<(int at, int size, string type, int head)> Children( byte[] d, int start, int end )
	{
		for ( var at = start; at + 8 <= end; )
		{
			var (size, type, h) = Box( d, at, end - at );
			if ( size <= 0 || at + size > end ) yield break;
			yield return (at, (int)size, type, h);
			at += (int)size;
		}
	}

	public static (int at, int size, int h)? Find( byte[] d, int start, int end, string type )
	{
		foreach ( var c in Children( d, start, end ) )
			if ( c.type == type ) return (c.at, c.size, c.head);
		return null;
	}

	/// <summary> A box nested under a box at <paramref name="at"/>, by the types of its ancestors below it. </summary>
	public static (int at, int size, int h)? Path( byte[] d, int at, int end, params string[] types )
	{
		var head = Box( d, at, end - at ).headerLength;
		(int at, int size, int h)? found = null;
		int s = at + head, e = end;
		foreach ( var t in types )
		{
			found = Find( d, s, e, t );
			if ( found is not { } f ) return null;
			s = f.at + f.h;
			e = f.at + f.size;
		}
		return found;
	}
}
