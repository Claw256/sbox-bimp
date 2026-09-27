using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Bimp.Resolver.Live;

/// <summary> One video access unit (timestamps in 90 kHz units). </summary>
public sealed class VideoSample
{
	public long Dts;
	public long Pts;
	public bool Key;
	/// <summary> H.264: AVCC (NAL units with 4 byte lengths). AV1: a temporal unit of sized OBUs. </summary>
	public byte[] Data;
}

/// <summary> A video track's codec and decoder configuration: what its MP4 sample entry says. </summary>
public sealed class VideoFormat
{
	public string Codec { get; private init; }
	public int Width { get; private init; }
	public int Height { get; private init; }

	/// <summary> The sample entry's four character code and its configuration box. </summary>
	internal string Fourcc { get; private init; }
	internal string ConfigBox { get; private init; }
	internal byte[] Config { get; private init; }

	/// <summary>
	/// The engine's H.264 decoder (Media Foundation) holds back the last 22 frames of a file; its AV1 decoder shows
	/// every frame (see <see cref="Media.Av1"/>).
	/// </summary>
	public bool HoldsBackFrames => Fourcc == "avc1";

	public bool IsH264 => Fourcc == "avc1";

	public static VideoFormat H264( byte[] sps, byte[] pps )
	{
		var info = Live.H264.ParseSps( sps );
		return new VideoFormat { Codec = "H.264", Width = info.Width, Height = info.Height, Fourcc = "avc1", ConfigBox = "avcC", Config = Live.H264.AvcConfig( sps, pps ) };
	}

	public static VideoFormat Av1( Media.Av1.SequenceHeader seq )
		=> new() { Codec = "AV1", Width = seq.Width, Height = seq.Height, Fourcc = "av01", ConfigBox = "av1C", Config = Media.Av1.ConfigRecord( seq ) };

	public override string ToString() => $"{Codec} {Width}x{Height}";
}

/// <summary> One raw AAC frame (no ADTS header), 1024 samples. Pts in 90 kHz units. </summary>
public sealed class AudioSample
{
	public long Pts;
	public byte[] Data;
}

/// <summary> An AAC track's decoder configuration. </summary>
public sealed class AacConfig
{
	/// <summary> AudioSpecificConfig. </summary>
	public byte[] Asc;
	public int SampleRate;
	public int Channels;

	static readonly int[] Rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

	public static int RateIndex( int rate ) => Array.IndexOf( Rates, rate );
	public static int RateOf( int index ) => index >= 0 && index < Rates.Length ? Rates[index] : 0;

	/// <summary> Build from ADTS header fields. </summary>
	public static AacConfig FromAdts( int profile, int rateIndex, int channelConfig )
	{
		var objectType = profile + 1;
		var asc = new byte[]
		{
			(byte)((objectType << 3) | (rateIndex >> 1)),
			(byte)(((rateIndex & 1) << 7) | (channelConfig << 3)),
		};
		return new AacConfig { Asc = asc, SampleRate = RateOf( rateIndex ), Channels = channelConfig == 7 ? 8 : channelConfig };
	}

	/// <summary> Parse an AudioSpecificConfig (from SDP "config=" or an esds). </summary>
	public static AacConfig FromAsc( byte[] asc )
	{
		if ( asc is null || asc.Length < 2 ) return null;
		var v = (asc[0] << 8) | asc[1];
		var rateIndex = (v >> 7) & 0xF;
		var channelConfig = (v >> 3) & 0xF;
		return new AacConfig { Asc = asc, SampleRate = RateOf( rateIndex ), Channels = channelConfig == 7 ? 8 : Math.Max( 1, channelConfig ) };
	}

	public bool SameAs( AacConfig o ) => o is not null && Asc.AsSpan().SequenceEqual( o.Asc );
}

/// <summary>
/// Writes a finished, moov-first MP4 (H.264 and/or AAC) - the only kind of file the engine opens instantly
/// (it walks every fragment of a fragmented MP4 on open, and needs the index before the data).
/// The file's timeline starts at <see cref="Write"/>'s <c>zero</c>.
/// </summary>
public static class Mp4Writer
{
	const uint MovieTimescale = 90000;

	public static void Write( Stream output, long zero,
		List<VideoSample> video, byte[] sps, byte[] pps,
		List<AudioSample> audio, AacConfig aac )
		=> Write( output, zero, video, sps is null || pps is null ? null : VideoFormat.H264( sps, pps ), audio, aac );

	public static void Write( Stream output, long zero,
		List<VideoSample> video, VideoFormat format,
		List<AudioSample> audio, AacConfig aac )
	{
		var hasVideo = video is { Count: > 0 } && format is not null;
		var hasAudio = audio is { Count: > 0 } && aac is not null && aac.SampleRate > 0;

		var ftyp = Box( "ftyp", Concat( Ascii( "isom" ), U32( 512 ), Ascii( "isom" ), Ascii( "iso2" ), Ascii( format?.Fourcc ?? "avc1" ), Ascii( "mp41" ) ) );

		// sample data: all video, then all audio (one chunk each - it's a local file, no need to interleave)
		var videoData = hasVideo ? video.Select( s => s.Data ).ToList() : new List<byte[]>();
		var audioData = hasAudio ? audio.Select( s => s.Data ).ToList() : new List<byte[]>();
		long videoBytes = videoData.Sum( d => (long)d.Length );

		// moov has a fixed size whatever the offsets are, so build it once to measure, then for real
		byte[] Moov( uint videoOffset, uint audioOffset )
		{
			var traks = new List<byte[]>();
			uint id = 1;
			if ( hasVideo ) traks.Add( VideoTrak( id++, zero, video, format, videoOffset ) );
			if ( hasAudio ) traks.Add( AudioTrak( id++, zero, audio, aac, audioOffset ) );
			var duration = Math.Max( hasVideo ? VideoDuration( video, zero ) : 0, hasAudio ? AudioMovieDuration( audio, aac, zero ) : 0 );
			return Box( "moov", Concat( new[] { Mvhd( duration, id ) }.Concat( traks ).ToArray() ) );
		}

		var moovSize = Moov( 0, 0 ).Length;
		var dataStart = (uint)(ftyp.Length + moovSize + 8);
		var moov = Moov( dataStart, (uint)(dataStart + videoBytes) );

		var mdatSize = 8 + videoBytes + audioData.Sum( d => (long)d.Length );
		output.Write( ftyp );
		output.Write( moov );
		output.Write( U32( (uint)mdatSize ) );
		output.Write( Ascii( "mdat" ) );
		foreach ( var d in videoData ) output.Write( d );
		foreach ( var d in audioData ) output.Write( d );
	}

	static long VideoDuration( List<VideoSample> v, long zero )
	{
		var last = v[^1];
		var frame = v.Count > 1 ? Math.Max( 1, last.Dts - v[^2].Dts ) : 3000;
		return last.Dts + frame - zero;
	}

	static long AudioMovieDuration( List<AudioSample> a, AacConfig aac, long zero )
		=> Math.Max( 0, a[0].Pts - zero ) + (long)a.Count * 1024 * MovieTimescale / aac.SampleRate;

	static byte[] VideoTrak( uint id, long zero, List<VideoSample> v, VideoFormat info, uint offset )
	{
		// durations between decode times; the last frame lasts as long as the one before it
		var durations = new List<uint>();
		for ( int i = 0; i < v.Count; i++ )
		{
			var d = i + 1 < v.Count ? v[i + 1].Dts - v[i].Dts : (v.Count > 1 ? v[i].Dts - v[i - 1].Dts : 3000);
			durations.Add( (uint)Math.Clamp( d, 1, 90000 ) );
		}
		var mediaDuration = durations.Sum( d => (long)d );
		var offsets = v.Select( s => s.Pts - s.Dts ).ToList();
		var hasCtts = offsets.Any( o => o != 0 );

		var stbl = new List<byte[]>
		{
			Box( "stsd", Concat( U32( 0 ), U32( 1 ), VisualEntry( info ) ) ),
			Stts( durations ),
		};
		if ( hasCtts ) stbl.Add( Ctts( offsets ) );
		stbl.Add( Stss( v ) );
		stbl.Add( Stsc( v.Count ) );
		stbl.Add( Stsz( v.Select( s => s.Data.Length ) ) );
		stbl.Add( Box( "stco", Concat( U32( 0 ), U32( 1 ), U32( offset ) ) ) );

		// the first frame shows at its presentation time; start the track there
		var elst = Elst( Math.Max( 0, v[0].Dts - zero ), mediaDuration, Math.Max( 0, offsets[0] ) );

		return Box( "trak", Concat(
			Tkhd( id, mediaDuration + Math.Max( 0, v[0].Dts - zero ), false, info.Width, info.Height ),
			Box( "edts", elst ),
			Box( "mdia", Concat(
				Mdhd( 90000, mediaDuration ),
				Hdlr( "vide" ),
				Box( "minf", Concat(
					FullBox( "vmhd", 0, 1, new byte[8] ),
					Dinf(),
					Box( "stbl", Concat( stbl.ToArray() ) ) ) ) ) ) ) );
	}

	static byte[] AudioTrak( uint id, long zero, List<AudioSample> a, AacConfig aac, uint offset )
	{
		var mediaDuration = (long)a.Count * 1024;
		var startDelay = Math.Max( 0, a[0].Pts - zero ); // movie timescale

		var stbl = Concat(
			Box( "stsd", Concat( U32( 0 ), U32( 1 ), Mp4a( aac ) ) ),
			Stts( Enumerable.Repeat( 1024u, a.Count ).ToList() ),
			Stsc( a.Count ),
			Stsz( a.Select( s => s.Data.Length ) ),
			Box( "stco", Concat( U32( 0 ), U32( 1 ), U32( offset ) ) ) );

		var movieDuration = mediaDuration * MovieTimescale / aac.SampleRate;

		return Box( "trak", Concat(
			Tkhd( id, startDelay + movieDuration, true, 0, 0 ),
			Box( "edts", Elst( startDelay, movieDuration, 0 ) ),
			Box( "mdia", Concat(
				Mdhd( (uint)aac.SampleRate, mediaDuration ),
				Hdlr( "soun" ),
				Box( "minf", Concat(
					FullBox( "smhd", 0, 0, new byte[4] ),
					Dinf(),
					Box( "stbl", stbl ) ) ) ) ) ) );
	}

	/// <summary> An edit list: an optional empty edit (delay), then the media from mediaTime. </summary>
	static byte[] Elst( long delay, long movieDuration, long mediaTime )
	{
		var entries = new List<byte[]>();
		if ( delay > 0 ) entries.Add( Concat( U32( (uint)delay ), U32( 0xFFFFFFFF ), U32( 0x00010000 ) ) );
		entries.Add( Concat( U32( (uint)movieDuration ), U32( (uint)mediaTime ), U32( 0x00010000 ) ) );
		return FullBox( "elst", 0, 0, Concat( new[] { U32( (uint)entries.Count ) }.Concat( entries ).ToArray() ) );
	}

	static byte[] Mvhd( long duration, uint nextTrack )
	{
		return FullBox( "mvhd", 0, 0, Concat(
			U32( 0 ), U32( 0 ), U32( MovieTimescale ), U32( (uint)duration ),
			U32( 0x00010000 ), U16( 0x0100 ), new byte[10],
			Matrix(), new byte[24], U32( nextTrack ) ) );
	}

	static byte[] Tkhd( uint id, long duration, bool audio, int width, int height )
	{
		return FullBox( "tkhd", 0, 3, Concat(
			U32( 0 ), U32( 0 ), U32( id ), U32( 0 ), U32( (uint)duration ),
			new byte[8], U16( 0 ), U16( 0 ), U16( (ushort)(audio ? 0x0100 : 0) ), U16( 0 ),
			Matrix(), U32( (uint)width << 16 ), U32( (uint)height << 16 ) ) );
	}

	static byte[] Mdhd( uint timescale, long duration )
		=> FullBox( "mdhd", 0, 0, Concat( U32( 0 ), U32( 0 ), U32( timescale ), U32( (uint)duration ), U16( 0x55C4 ), U16( 0 ) ) );

	static byte[] Hdlr( string type )
		=> FullBox( "hdlr", 0, 0, Concat( U32( 0 ), Ascii( type ), new byte[12], Encoding.ASCII.GetBytes( "bimp\0" ) ) );

	static byte[] Dinf() => Box( "dinf", FullBox( "dref", 0, 0, Concat( U32( 1 ), FullBox( "url ", 0, 1, Array.Empty<byte>() ) ) ) );

	/// <summary> avc1 + avcC, or av01 + av1C. </summary>
	static byte[] VisualEntry( VideoFormat f )
	{
		return Box( f.Fourcc, Concat(
			new byte[6], U16( 1 ), // reserved, data_reference_index
			new byte[16],           // pre_defined, reserved, pre_defined[3]
			U16( (ushort)f.Width ), U16( (ushort)f.Height ),
			U32( 0x00480000 ), U32( 0x00480000 ), U32( 0 ), U16( 1 ),
			new byte[32],           // compressorname
			U16( 0x0018 ), U16( 0xFFFF ),
			Box( f.ConfigBox, f.Config ) ) );
	}

	static byte[] Mp4a( AacConfig aac )
	{
		// ES_Descriptor > DecoderConfigDescriptor (AAC, audio stream) > DecoderSpecificInfo (the ASC), SLConfig
		var dsi = Descriptor( 5, aac.Asc );
		var dcd = Descriptor( 4, Concat( new byte[] { 0x40, 0x15, 0, 0, 0 }, U32( 0 ), U32( 0 ), dsi ) );
		var es = Descriptor( 3, Concat( U16( 1 ), new byte[] { 0 }, dcd, Descriptor( 6, new byte[] { 2 } ) ) );

		// channelcount must match the ASC - the engine trusts this field (see Mp4ChannelFix)
		return Box( "mp4a", Concat(
			new byte[6], U16( 1 ), new byte[8],
			U16( (ushort)aac.Channels ), U16( 16 ), U16( 0 ), U16( 0 ),
			U32( (uint)Math.Min( aac.SampleRate, 65535 ) << 16 ),
			FullBox( "esds", 0, 0, es ) ) );
	}

	static byte[] Descriptor( byte tag, byte[] body )
	{
		// 4 byte length form, like most muxers write it
		var len = body.Length;
		return Concat( new byte[] { tag, (byte)(0x80 | (len >> 21) & 0x7F), (byte)(0x80 | (len >> 14) & 0x7F), (byte)(0x80 | (len >> 7) & 0x7F), (byte)(len & 0x7F) }, body );
	}

	static byte[] Stts( List<uint> durations )
	{
		var runs = new List<(uint count, uint delta)>();
		foreach ( var d in durations )
		{
			if ( runs.Count > 0 && runs[^1].delta == d ) runs[^1] = (runs[^1].count + 1, d);
			else runs.Add( (1, d) );
		}
		return FullBox( "stts", 0, 0, Concat( new[] { U32( (uint)runs.Count ) }.Concat( runs.Select( r => Concat( U32( r.count ), U32( r.delta ) ) ) ).ToArray() ) );
	}

	static byte[] Ctts( List<long> offsets )
	{
		// version 0 (unsigned) like normal muxers write it - B-frame offsets are never negative when decode time is the
		// earliest presentation; version 1 (signed) only if we really need it
		var signed = offsets.Any( o => o < 0 );
		var runs = new List<(uint count, long offset)>();
		foreach ( var o in offsets )
		{
			if ( runs.Count > 0 && runs[^1].offset == o ) runs[^1] = (runs[^1].count + 1, o);
			else runs.Add( (1, o) );
		}
		return FullBox( "ctts", (byte)(signed ? 1 : 0), 0, Concat( new[] { U32( (uint)runs.Count ) }.Concat( runs.Select( r => Concat( U32( r.count ), U32( unchecked((uint)(int)r.offset) ) ) ) ).ToArray() ) );
	}

	static byte[] Stss( List<VideoSample> v )
	{
		var keys = v.Select( ( s, i ) => (s, i) ).Where( x => x.s.Key ).Select( x => U32( (uint)x.i + 1 ) ).ToList();
		return FullBox( "stss", 0, 0, Concat( new[] { U32( (uint)keys.Count ) }.Concat( keys ).ToArray() ) );
	}

	static byte[] Stsc( int samples ) => FullBox( "stsc", 0, 0, Concat( U32( 1 ), U32( 1 ), U32( (uint)samples ), U32( 1 ) ) );

	static byte[] Stsz( IEnumerable<int> sizes )
	{
		var list = sizes.ToList();
		return FullBox( "stsz", 0, 0, Concat( new[] { U32( 0 ), U32( (uint)list.Count ) }.Concat( list.Select( s => U32( (uint)s ) ) ).ToArray() ) );
	}

	static byte[] Matrix() => Concat( U32( 0x00010000 ), U32( 0 ), U32( 0 ), U32( 0 ), U32( 0x00010000 ), U32( 0 ), U32( 0 ), U32( 0 ), U32( 0x40000000 ) );

	static byte[] Box( string type, byte[] body ) => Concat( U32( (uint)(8 + body.Length) ), Ascii( type ), body );

	static byte[] FullBox( string type, byte version, uint flags, byte[] body )
		=> Box( type, Concat( new[] { version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags }, body ) );

	static byte[] Ascii( string s ) => Encoding.ASCII.GetBytes( s );

	static byte[] U32( uint v ) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian( b, v ); return b; }
	static byte[] U16( ushort v ) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian( b, v ); return b; }

	static byte[] Concat( params byte[][] parts ) => Media.Ebml.Concat( parts );
}
