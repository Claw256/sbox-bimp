namespace Bimp.Resolver.Media;

/// <summary>
/// AV1 bitstream helpers. Samples in MP4 and WebM are temporal units in the low-overhead format: OBUs that each
/// carry a size field, no temporal delimiters. The engine's AV1 decoder plays those with no frames held back at the
/// end of a file (measured: 30 of 30 shown, H.264 shows 8), so live AV1 needs no filler frames and no overlap.
/// </summary>
public static class Av1
{
	public const int ObuSequenceHeader = 1;
	public const int ObuTemporalDelimiter = 2;
	public const int ObuFrameHeader = 3;
	public const int ObuFrame = 6;
	public const int ObuTileList = 8;
	public const int ObuPadding = 15;

	/// <summary> The parts of a sequence header an av1C box and a sample entry need. </summary>
	public sealed class SequenceHeader
	{
		public int Profile, Level, Tier;
		public bool HighBitDepth, TwelveBit, Mono, SubsamplingX, SubsamplingY;
		public int ChromaSamplePosition;
		public int Width, Height;
		public bool ReducedStillPicture;

		/// <summary> The whole OBU (header, size, payload). </summary>
		public byte[] Obu;

		public bool SameAs( SequenceHeader o ) => o is not null && Obu.AsSpan().SequenceEqual( o.Obu );
	}

	/// <summary> One OBU in a buffer: where it starts, its header size, and its payload. </summary>
	public readonly record struct Obu( int Type, int Start, int HeaderLength, int PayloadStart, int PayloadLength, bool HasSize )
	{
		public int End => PayloadStart + PayloadLength;
	}

	/// <summary> The OBUs of a low-overhead buffer (the last may lack a size field and run to the end). </summary>
	public static List<Obu> Obus( ReadOnlySpan<byte> d )
	{
		var list = new List<Obu>();
		var i = 0;
		while ( i < d.Length )
		{
			var header = d[i];
			var type = (header >> 3) & 0xF;
			var ext = (header & 0x04) != 0;
			var hasSize = (header & 0x02) != 0;
			var headerLength = ext ? 2 : 1;
			if ( i + headerLength > d.Length ) break;

			int payloadStart, payloadLength;
			if ( hasSize )
			{
				var (size, n) = Leb128( d[(i + headerLength)..] );
				if ( n == 0 ) break;
				payloadStart = i + headerLength + n;
				payloadLength = (int)Math.Min( size, (ulong)(d.Length - payloadStart) );
			}
			else
			{
				payloadStart = i + headerLength;
				payloadLength = d.Length - payloadStart;
			}
			list.Add( new Obu( type, i, headerLength, payloadStart, payloadLength, hasSize ) );
			i = payloadStart + payloadLength;
		}
		return list;
	}

	public static (ulong value, int length) Leb128( ReadOnlySpan<byte> d )
	{
		ulong value = 0;
		for ( int i = 0; i < 8 && i < d.Length; i++ )
		{
			value |= (ulong)(d[i] & 0x7F) << (i * 7);
			if ( (d[i] & 0x80) == 0 ) return (value, i + 1);
		}
		return (0, 0);
	}

	public static void WriteLeb128( List<byte> output, ulong value )
	{
		do
		{
			var b = (byte)(value & 0x7F);
			value >>= 7;
			if ( value != 0 ) b |= 0x80;
			output.Add( b );
		} while ( value != 0 );
	}

	/// <summary> An OBU with a size field, from its header byte(s) and payload. </summary>
	public static void AppendSized( List<byte> output, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload )
	{
		output.Add( (byte)(header[0] | 0x02) );
		if ( (header[0] & 0x04) != 0 && header.Length > 1 ) output.Add( header[1] );
		WriteLeb128( output, (ulong)payload.Length );
		foreach ( var b in payload ) output.Add( b );
	}

	/// <summary>
	/// A temporal unit as MP4/WebM store it: every OBU sized; temporal delimiters, tile lists and padding dropped.
	/// Returns the input when it's already like that.
	/// </summary>
	public static byte[] ForStorage( byte[] tu )
	{
		var obus = Obus( tu );
		if ( obus.All( o => o.HasSize && o.Type is not ObuTemporalDelimiter and not ObuTileList and not ObuPadding ) ) return tu;
		var output = new List<byte>( tu.Length + 8 );
		foreach ( var o in obus )
		{
			if ( o.Type is ObuTemporalDelimiter or ObuTileList or ObuPadding ) continue;
			AppendSized( output, tu.AsSpan( o.Start, o.HeaderLength ), tu.AsSpan( o.PayloadStart, o.PayloadLength ) );
		}
		return output.ToArray();
	}

	/// <summary> The first sequence header in a temporal unit, null if it has none. </summary>
	public static SequenceHeader FindSequenceHeader( byte[] tu )
	{
		foreach ( var o in Obus( tu ) )
		{
			if ( o.Type != ObuSequenceHeader ) continue;
			try
			{
				var s = ParseSequenceHeader( tu.AsSpan( o.PayloadStart, o.PayloadLength ) );
				var obu = new List<byte>();
				AppendSized( obu, tu.AsSpan( o.Start, o.HeaderLength ), tu.AsSpan( o.PayloadStart, o.PayloadLength ) );
				s.Obu = obu.ToArray();
				return s;
			}
			catch ( Exception e ) when ( e is IndexOutOfRangeException or ArgumentOutOfRangeException )
			{
				return null;
			}
		}
		return null;
	}

	/// <summary> A key frame: the first frame header in the unit says KEY_FRAME (and isn't a show-existing-frame). </summary>
	public static bool IsKeyframe( byte[] tu, SequenceHeader seq )
	{
		foreach ( var o in Obus( tu ) )
		{
			if ( o.Type is not ObuFrame and not ObuFrameHeader ) continue;
			if ( seq?.ReducedStillPicture ?? false ) return true;
			if ( o.PayloadLength < 1 ) return false;
			var b = tu[o.PayloadStart];
			var showExisting = (b & 0x80) != 0;
			var frameType = (b >> 5) & 3;
			return !showExisting && frameType == 0;
		}
		return false;
	}

	/// <summary> The av1C box body (AV1CodecConfigurationRecord), with the sequence header as its config OBU. </summary>
	public static byte[] ConfigRecord( SequenceHeader s )
	{
		var head = new byte[]
		{
			0x81,
			(byte)((s.Profile << 5) | (s.Level & 0x1F)),
			(byte)((s.Tier << 7) | (s.HighBitDepth ? 0x40 : 0) | (s.TwelveBit ? 0x20 : 0) | (s.Mono ? 0x10 : 0)
				| (s.SubsamplingX ? 0x08 : 0) | (s.SubsamplingY ? 0x04 : 0) | (s.ChromaSamplePosition & 3)),
			0,
		};
		return Ebml.Concat( head, s.Obu );
	}

	/// <summary> The sequence header OBU inside an av1C record (its config OBUs), null if it has none. </summary>
	public static SequenceHeader FromConfigRecord( byte[] av1C )
	{
		if ( av1C is null || av1C.Length <= 4 ) return null;
		return FindSequenceHeader( av1C[4..] );
	}

	/// <summary> AV1 spec 5.5, up to color_config - the rest (tools, film grain) isn't needed here. </summary>
	public static SequenceHeader ParseSequenceHeader( ReadOnlySpan<byte> payload )
	{
		var r = new BitReader( payload.ToArray() );
		var s = new SequenceHeader();
		s.Profile = r.Bits( 3 );
		r.Bits( 1 ); // still_picture
		s.ReducedStillPicture = r.Bit();

		if ( s.ReducedStillPicture )
		{
			s.Level = r.Bits( 5 );
		}
		else
		{
			var decoderModel = false;
			var bufferDelayLength = 0;
			if ( r.Bit() ) // timing_info_present_flag
			{
				r.Bits( 32 ); r.Bits( 32 ); // num_units_in_display_tick, time_scale
				if ( r.Bit() ) r.Uvlc(); // equal_picture_interval: num_ticks_per_picture_minus_1
				decoderModel = r.Bit();
				if ( decoderModel )
				{
					bufferDelayLength = r.Bits( 5 ) + 1;
					r.Bits( 32 ); // num_units_in_decoding_tick
					r.Bits( 5 ); r.Bits( 5 ); // buffer_removal_time_length_minus_1, frame_presentation_time_length_minus_1
				}
			}
			var initialDisplayDelay = r.Bit();
			var operatingPoints = r.Bits( 5 ) + 1;
			for ( int i = 0; i < operatingPoints; i++ )
			{
				r.Bits( 12 ); // operating_point_idc
				var level = r.Bits( 5 );
				var tier = level > 7 ? r.Bits( 1 ) : 0;
				if ( i == 0 ) { s.Level = level; s.Tier = tier; }
				if ( decoderModel && r.Bit() )
				{
					r.Bits( bufferDelayLength ); r.Bits( bufferDelayLength ); r.Bits( 1 );
				}
				if ( initialDisplayDelay && r.Bit() ) r.Bits( 4 );
			}
		}

		var widthBits = r.Bits( 4 ) + 1;
		var heightBits = r.Bits( 4 ) + 1;
		s.Width = r.Bits( widthBits ) + 1;
		s.Height = r.Bits( heightBits ) + 1;

		if ( !s.ReducedStillPicture && r.Bit() ) // frame_id_numbers_present_flag
		{
			r.Bits( 4 ); r.Bits( 3 );
		}
		r.Bits( 3 ); // use_128x128_superblock, enable_filter_intra, enable_intra_edge_filter
		if ( !s.ReducedStillPicture )
		{
			r.Bits( 4 ); // interintra, masked compound, warped motion, dual filter
			var orderHint = r.Bit();
			if ( orderHint ) r.Bits( 2 ); // jnt_comp, ref_frame_mvs
			var forceScreenContent = r.Bit() ? 2 : r.Bits( 1 ); // seq_choose_screen_content_tools
			if ( forceScreenContent > 0 && !r.Bit() ) r.Bits( 1 ); // seq_choose_integer_mv, else seq_force_integer_mv
			if ( orderHint ) r.Bits( 3 );
		}
		r.Bits( 3 ); // superres, cdef, restoration

		// color_config
		s.HighBitDepth = r.Bit();
		if ( s.Profile == 2 && s.HighBitDepth ) s.TwelveBit = r.Bit();
		s.Mono = s.Profile != 1 && r.Bit();
		int primaries = 2, transfer = 2, matrix = 2;
		if ( r.Bit() ) // color_description_present_flag
		{
			primaries = r.Bits( 8 ); transfer = r.Bits( 8 ); matrix = r.Bits( 8 );
		}
		if ( s.Mono )
		{
			s.SubsamplingX = s.SubsamplingY = true;
			return s;
		}
		if ( primaries == 1 && transfer == 13 && matrix == 0 ) return s; // sRGB: 4:4:4

		r.Bits( 1 ); // color_range
		if ( s.Profile == 0 ) s.SubsamplingX = s.SubsamplingY = true;
		else if ( s.Profile == 2 )
		{
			if ( s.TwelveBit )
			{
				s.SubsamplingX = r.Bit();
				s.SubsamplingY = s.SubsamplingX && r.Bit();
			}
			else s.SubsamplingX = true;
		}
		if ( s.SubsamplingX && s.SubsamplingY ) s.ChromaSamplePosition = r.Bits( 2 );
		return s;
	}

	sealed class BitReader
	{
		readonly byte[] data;
		int bit;

		public BitReader( byte[] data ) => this.data = data;

		public bool Bit()
		{
			var v = (data[bit >> 3] >> (7 - (bit & 7))) & 1;
			bit++;
			return v == 1;
		}

		public int Bits( int n )
		{
			long v = 0;
			for ( int i = 0; i < n; i++ ) v = (v << 1) | (Bit() ? 1L : 0L);
			return (int)v;
		}

		public long Uvlc()
		{
			var zeros = 0;
			while ( !Bit() ) { zeros++; if ( zeros >= 32 ) return uint.MaxValue; }
			return Bits( zeros ) + (1L << zeros) - 1;
		}
	}
}
