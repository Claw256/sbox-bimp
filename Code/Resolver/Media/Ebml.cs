using System.Buffers.Binary;
using System.Text;

namespace Bimp.Resolver.Media;

/// <summary>
/// Minimal EBML / Matroska helpers - just enough to read WebM headers, cues and clusters, and to write
/// new elements. A port of the old resolver's ebml.py.
/// </summary>
public static class Ebml
{
	// Element IDs (with their length marker bits, as they appear in the file)
	public const uint EBML = 0x1A45DFA3;
	public const uint Segment = 0x18538067;
	public const uint SeekHead = 0x114D9B74;
	public const uint Seek = 0x4DBB;
	public const uint SeekId = 0x53AB;
	public const uint SeekPosition = 0x53AC;
	public const uint Info = 0x1549A966;
	public const uint TimecodeScale = 0x2AD7B1;
	public const uint Duration = 0x4489;
	public const uint MuxingApp = 0x4D80;
	public const uint WritingApp = 0x5741;
	public const uint Tracks = 0x1654AE6B;
	public const uint TrackEntry = 0xAE;
	public const uint TrackNumber = 0xD7;
	public const uint TrackUid = 0x73C5;
	public const uint Cues = 0x1C53BB6B;
	public const uint CuePoint = 0xBB;
	public const uint CueTime = 0xB3;
	public const uint CueTrackPositions = 0xB7;
	public const uint CueTrack = 0xF7;
	public const uint CueClusterPosition = 0xF1;
	public const uint Cluster = 0x1F43B675;
	public const uint Timecode = 0xE7;
	public const uint SimpleBlock = 0xA3;
	public const uint BlockGroup = 0xA0;
	public const uint Block = 0xA1;

	public const long UnknownSize = -1;

	public class NeedMoreDataException : Exception
	{
		public NeedMoreDataException() : base( "need more data" ) { }
	}

	public readonly struct Element
	{
		public readonly uint Id;
		/// <summary> Offset of the element's first byte (the ID). </summary>
		public readonly long Offset;
		/// <summary> ID + size bytes. </summary>
		public readonly int HeaderLength;
		/// <summary> Data size, <see cref="UnknownSize"/> if unknown. </summary>
		public readonly long Size;

		public Element( uint id, long offset, int headerLength, long size )
		{
			Id = id;
			Offset = offset;
			HeaderLength = headerLength;
			Size = size;
		}

		public long DataOffset => Offset + HeaderLength;
		public long End => DataOffset + Size;
	}

	public static (uint id, int length) ReadId( ReadOnlySpan<byte> buf, int pos )
	{
		if ( pos >= buf.Length ) throw new NeedMoreDataException();
		var first = buf[pos];
		int length = 1;
		int mask = 0x80;
		while ( length <= 4 && (first & mask) == 0 ) { mask >>= 1; length++; }
		if ( length > 4 ) throw new FormatException( $"bad element id at {pos}" );
		if ( pos + length > buf.Length ) throw new NeedMoreDataException();

		uint id = 0;
		for ( int i = 0; i < length; i++ ) id = (id << 8) | buf[pos + i];
		return (id, length);
	}

	public static (long size, int length) ReadSize( ReadOnlySpan<byte> buf, int pos )
	{
		if ( pos >= buf.Length ) throw new NeedMoreDataException();
		var first = buf[pos];
		int length = 1;
		int mask = 0x80;
		while ( length <= 8 && (first & mask) == 0 ) { mask >>= 1; length++; }
		if ( length > 8 ) throw new FormatException( $"bad vint at {pos}" );
		if ( pos + length > buf.Length ) throw new NeedMoreDataException();

		long value = first & (mask - 1);
		var allOnes = value == mask - 1;
		for ( int i = 1; i < length; i++ )
		{
			var b = buf[pos + i];
			value = (value << 8) | b;
			allOnes = allOnes && b == 0xFF;
		}
		return (allOnes ? UnknownSize : value, length);
	}

	/// <summary>
	/// Read an element header at buf[pos]. <paramref name="baseOffset"/> is added to the offset (for buffers that don't start at 0).
	/// </summary>
	public static Element ReadElement( ReadOnlySpan<byte> buf, int pos, long baseOffset = 0 )
	{
		var (id, idLen) = ReadId( buf, pos );
		var (size, sizeLen) = ReadSize( buf, pos + idLen );
		return new Element( id, baseOffset + pos, idLen + sizeLen, size );
	}

	/// <summary>
	/// Child elements of a master element whose data is buf[start..end] (buffer-relative positions).
	/// Offsets in the returned elements have <paramref name="baseOffset"/> added.
	/// </summary>
	public static List<Element> Children( byte[] buf, long start, long end, long baseOffset = 0 )
	{
		var list = new List<Element>();
		var pos = start;
		while ( pos < end )
		{
			var el = ReadElement( buf, (int)pos, baseOffset );
			list.Add( el );
			if ( el.Size == UnknownSize ) break;
			pos = el.End - baseOffset;
		}
		return list;
	}

	/// <summary>
	/// Like <see cref="Children"/>, but stops (without throwing) at the first element whose header isn't in the buffer.
	/// </summary>
	public static List<Element> ChildrenPartial( byte[] buf, long start, long end, long baseOffset = 0 )
	{
		var list = new List<Element>();
		var pos = start;
		while ( pos < end && pos < buf.Length )
		{
			Element el;
			try { el = ReadElement( buf, (int)pos, baseOffset ); }
			catch ( NeedMoreDataException ) { break; }
			list.Add( el );
			if ( el.Size == UnknownSize ) break;
			pos = el.End - baseOffset;
		}
		return list;
	}

	public static ReadOnlySpan<byte> Data( byte[] buf, Element el, long baseOffset = 0 )
		=> buf.AsSpan( (int)(el.DataOffset - baseOffset), (int)el.Size );

	public static ulong ReadUInt( ReadOnlySpan<byte> data )
	{
		ulong v = 0;
		foreach ( var b in data ) v = (v << 8) | b;
		return v;
	}

	public static double ReadFloat( ReadOnlySpan<byte> data ) => data.Length switch
	{
		4 => BinaryPrimitives.ReadSingleBigEndian( data ),
		8 => BinaryPrimitives.ReadDoubleBigEndian( data ),
		_ => 0,
	};

	//
	// Writing
	//

	public static byte[] EncodeId( uint id )
	{
		int length = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
		var b = new byte[length];
		for ( int i = 0; i < length; i++ ) b[i] = (byte)(id >> (8 * (length - 1 - i)));
		return b;
	}

	public static byte[] EncodeSize( long size, int length = 0 )
	{
		if ( length == 0 )
		{
			length = 1;
			while ( size >= (1L << (7 * length)) - 1 ) length++;
		}
		if ( size >= (1L << (7 * length)) - 1 ) throw new ArgumentException( "size doesn't fit" );

		var value = (1UL << (7 * length)) | (ulong)size;
		var b = new byte[length];
		for ( int i = 0; i < length; i++ ) b[i] = (byte)(value >> (8 * (length - 1 - i)));
		return b;
	}

	public static byte[] Concat( params byte[][] parts )
	{
		var total = 0;
		foreach ( var p in parts ) total += p.Length;
		var result = new byte[total];
		var o = 0;
		foreach ( var p in parts )
		{
			Buffer.BlockCopy( p, 0, result, o, p.Length );
			o += p.Length;
		}
		return result;
	}

	public static byte[] Concat( IEnumerable<byte[]> parts ) => Concat( parts.ToArray() );

	public static byte[] Make( uint id, byte[] data, int sizeLength = 0 )
		=> Concat( EncodeId( id ), EncodeSize( data.Length, sizeLength ), data );

	public static byte[] UIntElement( uint id, ulong value, int width = 0 )
	{
		if ( width == 0 )
		{
			width = 1;
			while ( width < 8 && (value >> (8 * width)) != 0 ) width++;
		}
		var b = new byte[width];
		for ( int i = 0; i < width; i++ ) b[i] = (byte)(value >> (8 * (width - 1 - i)));
		return Make( id, b );
	}

	public static byte[] FloatElement( uint id, double value )
	{
		var b = new byte[8];
		BinaryPrimitives.WriteDoubleBigEndian( b, value );
		return Make( id, b );
	}

	public static byte[] StringElement( uint id, string value ) => Make( id, Encoding.UTF8.GetBytes( value ) );
}
