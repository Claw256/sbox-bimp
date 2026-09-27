namespace Bimp.Resolver.Live;

/// <summary>
/// Just enough H.264 to remux it: splitting Annex B byte streams into NAL units, reading the SPS for the picture
/// size, and building the avcC decoder configuration an MP4 needs.
/// </summary>
public static class H264
{
	public const int NalSlice = 1;
	public const int NalIdr = 5;
	public const int NalSei = 6;
	public const int NalSps = 7;
	public const int NalPps = 8;
	public const int NalAud = 9;

	public static int NalType( byte[] nal ) => nal.Length > 0 ? nal[0] & 0x1F : 0;

	/// <summary>
	/// Split an Annex B byte stream (00 00 01 / 00 00 00 01 start codes) into NAL units.
	/// </summary>
	public static List<byte[]> SplitAnnexB( byte[] data, int start, int end )
	{
		var result = new List<byte[]>();
		var i = FindStartCode( data, start, end, out var codeLength );
		while ( i >= 0 )
		{
			var nalStart = i + codeLength;
			var next = FindStartCode( data, nalStart, end, out var nextLength );
			var nalEnd = next >= 0 ? next : end;

			// a trailing zero before a 4 byte start code belongs to the start code
			while ( nalEnd > nalStart && data[nalEnd - 1] == 0 ) nalEnd--;

			if ( nalEnd > nalStart )
				result.Add( data.AsSpan( nalStart, nalEnd - nalStart ).ToArray() );

			i = next;
			codeLength = nextLength;
		}
		return result;
	}

	static int FindStartCode( byte[] d, int start, int end, out int length )
	{
		for ( int i = start; i + 2 < end; i++ )
		{
			if ( d[i] != 0 || d[i + 1] != 0 ) continue;
			if ( d[i + 2] == 1 ) { length = 3; return i; }
			if ( d[i + 2] == 0 && i + 3 < end && d[i + 3] == 1 ) { length = 4; return i; }
		}
		length = 0;
		return -1;
	}

	/// <summary>
	/// Where the NAL units of an Annex B byte stream are, without copying them.
	/// </summary>
	public static List<(int Offset, int Length)> NalRanges( byte[] data, int start, int end )
	{
		var result = new List<(int, int)>();
		var i = FindStartCode( data, start, end, out var codeLength );
		while ( i >= 0 )
		{
			var nalStart = i + codeLength;
			var next = FindStartCode( data, nalStart, end, out var nextLength );
			var nalEnd = next >= 0 ? next : end;
			while ( nalEnd > nalStart && data[nalEnd - 1] == 0 ) nalEnd--;
			if ( nalEnd > nalStart ) result.Add( (nalStart, nalEnd - nalStart) );
			i = next;
			codeLength = nextLength;
		}
		return result;
	}

	/// <summary>
	/// MP4 sample data straight from an Annex B buffer, in one allocation (AUD, SPS and PPS left out).
	/// </summary>
	public static byte[] ToAvcc( byte[] data, List<(int Offset, int Length)> nals )
	{
		var total = 0;
		foreach ( var (o, l) in nals )
			if ( (data[o] & 0x1F) is not (NalAud or NalSps or NalPps) ) total += l + 4;

		var result = new byte[total];
		var at = 0;
		foreach ( var (o, l) in nals )
		{
			if ( (data[o] & 0x1F) is NalAud or NalSps or NalPps ) continue;
			result[at] = (byte)(l >> 24);
			result[at + 1] = (byte)(l >> 16);
			result[at + 2] = (byte)(l >> 8);
			result[at + 3] = (byte)l;
			Buffer.BlockCopy( data, o, result, at + 4, l );
			at += l + 4;
		}
		return result;
	}

	/// <summary>
	/// NAL units as MP4 sample data: each prefixed with its 4 byte big endian length. Access unit delimiters and
	/// parameter sets are left out (they live in the avcC).
	/// </summary>
	public static byte[] ToAvcc( IEnumerable<byte[]> nals )
	{
		var keep = nals.Where( n => NalType( n ) is not (NalAud or NalSps or NalPps) ).ToList();
		var result = new byte[keep.Sum( n => n.Length + 4 )];
		var o = 0;
		foreach ( var n in keep )
		{
			result[o] = (byte)(n.Length >> 24);
			result[o + 1] = (byte)(n.Length >> 16);
			result[o + 2] = (byte)(n.Length >> 8);
			result[o + 3] = (byte)n.Length;
			Buffer.BlockCopy( n, 0, result, o + 4, n.Length );
			o += n.Length + 4;
		}
		return result;
	}

	public sealed class Sps
	{
		public int Profile, Compatibility, Level;
		public int ChromaFormat = 1, BitDepthLuma = 8, BitDepthChroma = 8;
		public int Width, Height;

		// what a slice header needs (see SkipFrame)
		public int Id, Log2MaxFrameNum, PocType, Log2MaxPocLsb, MaxNumRefFrames, WidthMbs, HeightMbs;
		public bool FrameMbsOnly, DeltaPicOrderAlwaysZero, SeparateColourPlane;
	}

	/// <summary>
	/// Parse the fields of a sequence parameter set we need (profile, chroma, bit depth, cropped size).
	/// </summary>
	public static Sps ParseSps( byte[] nal )
	{
		var r = new BitReader( RemoveEmulationPrevention( nal, 1 ) );
		var s = new Sps
		{
			Profile = r.Bits( 8 ),
			Compatibility = r.Bits( 8 ),
			Level = r.Bits( 8 ),
		};
		s.Id = r.Ue();

		if ( s.Profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135 )
		{
			s.ChromaFormat = r.Ue();
			if ( s.ChromaFormat == 3 ) s.SeparateColourPlane = r.Bits( 1 ) == 1;
			s.BitDepthLuma = r.Ue() + 8;
			s.BitDepthChroma = r.Ue() + 8;
			r.Bits( 1 ); // qpprime_y_zero_transform_bypass_flag
			if ( r.Bits( 1 ) == 1 ) // seq_scaling_matrix_present_flag
			{
				for ( int i = 0; i < (s.ChromaFormat != 3 ? 8 : 12); i++ )
				{
					if ( r.Bits( 1 ) == 0 ) continue;
					int last = 8, next = 8, size = i < 6 ? 16 : 64;
					for ( int j = 0; j < size; j++ )
					{
						if ( next != 0 ) next = (last + r.Se() + 256) % 256;
						last = next == 0 ? last : next;
					}
				}
			}
		}

		s.Log2MaxFrameNum = r.Ue() + 4;
		var pocType = s.PocType = r.Ue();
		if ( pocType == 0 ) s.Log2MaxPocLsb = r.Ue() + 4;
		else if ( pocType == 1 )
		{
			s.DeltaPicOrderAlwaysZero = r.Bits( 1 ) == 1;
			r.Se(); r.Se();
			var n = r.Ue();
			for ( int i = 0; i < n; i++ ) r.Se();
		}
		s.MaxNumRefFrames = r.Ue();
		r.Bits( 1 ); // gaps_in_frame_num_value_allowed_flag
		var widthMbs = r.Ue() + 1;
		var heightMapUnits = r.Ue() + 1;
		var frameMbsOnly = r.Bits( 1 );
		s.FrameMbsOnly = frameMbsOnly == 1;
		s.WidthMbs = widthMbs;
		s.HeightMbs = (2 - frameMbsOnly) * heightMapUnits;
		if ( frameMbsOnly == 0 ) r.Bits( 1 ); // mb_adaptive_frame_field_flag
		r.Bits( 1 ); // direct_8x8_inference_flag

		int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
		if ( r.Bits( 1 ) == 1 )
		{
			cropLeft = r.Ue(); cropRight = r.Ue(); cropTop = r.Ue(); cropBottom = r.Ue();
		}

		var subWidth = s.ChromaFormat is 1 or 2 ? 2 : 1;
		var subHeight = s.ChromaFormat == 1 ? 2 : 1;
		var cropUnitX = s.ChromaFormat == 0 ? 1 : subWidth;
		var cropUnitY = (s.ChromaFormat == 0 ? 1 : subHeight) * (2 - frameMbsOnly);

		s.Width = widthMbs * 16 - cropUnitX * (cropLeft + cropRight);
		s.Height = (2 - frameMbsOnly) * heightMapUnits * 16 - cropUnitY * (cropTop + cropBottom);
		return s;
	}

	/// <summary>
	/// AVCDecoderConfigurationRecord (the avcC box payload) with 4 byte NAL lengths.
	/// </summary>
	public static byte[] AvcConfig( byte[] sps, byte[] pps )
	{
		var info = ParseSps( sps );
		var list = new List<byte>
		{
			1, sps[1], sps[2], sps[3],
			0xFF, // 6 bits reserved + lengthSizeMinusOne = 3
			0xE1, // 3 bits reserved + 1 SPS
			(byte)(sps.Length >> 8), (byte)sps.Length,
		};
		list.AddRange( sps );
		list.Add( 1 );
		list.Add( (byte)(pps.Length >> 8) );
		list.Add( (byte)pps.Length );
		list.AddRange( pps );

		if ( info.Profile is 100 or 110 or 122 or 144 )
		{
			list.Add( (byte)(0xFC | info.ChromaFormat) );
			list.Add( (byte)(0xF8 | (info.BitDepthLuma - 8)) );
			list.Add( (byte)(0xF8 | (info.BitDepthChroma - 8)) );
			list.Add( 0 );
		}

		return list.ToArray();
	}

	/// <summary> What the next generated frame has to carry on from: a slice's frame_num, POC and reference flag. </summary>
	public struct SliceInfo
	{
		public int FrameNum, PocLsb;
		public bool Reference, Idr;
	}

	/// <summary> The start of a slice header (up to the POC), or null if this isn't a slice. </summary>
	public static SliceInfo? ParseSlice( ReadOnlySpan<byte> nal, Sps sps )
	{
		var type = nal.Length > 1 ? nal[0] & 0x1F : 0;
		if ( type is not (NalSlice or NalIdr) ) return null;
		var head = nal[..Math.Min( nal.Length, 32 )].ToArray();
		var r = new BitReader( RemoveEmulationPrevention( head, 1 ) );
		r.Ue(); // first_mb_in_slice
		r.Ue(); // slice_type
		r.Ue(); // pic_parameter_set_id
		if ( sps.SeparateColourPlane ) r.Bits( 2 );
		var info = new SliceInfo { FrameNum = r.Bits( sps.Log2MaxFrameNum ), Reference = (nal[0] & 0x60) != 0, Idr = type == NalIdr };
		if ( !sps.FrameMbsOnly && r.Bits( 1 ) == 1 ) r.Bits( 1 ); // field_pic_flag, bottom_field_flag
		if ( info.Idr ) r.Ue(); // idr_pic_id
		if ( sps.PocType == 0 ) info.PocLsb = r.Bits( sps.Log2MaxPocLsb );
		return info;
	}

	/// <summary> A picture parameter set's id. </summary>
	public static int PpsId( byte[] nal ) => new BitReader( RemoveEmulationPrevention( nal[..Math.Min( nal.Length, 8 )], 1 ) ).Ue();

	/// <summary>
	/// A minimal CAVLC picture parameter set for <see cref="SkipFrame"/> - its own id, so the stream's own PPS
	/// (often CABAC) is untouched.
	/// </summary>
	public static byte[] SkipPps( Sps sps, int ppsId )
	{
		var w = new BitWriter();
		w.Ue( ppsId );
		w.Ue( sps.Id );
		w.Bit( 0 ); // entropy_coding_mode_flag: CAVLC
		w.Bit( 0 ); // bottom_field_pic_order_in_frame_present_flag
		w.Ue( 0 );  // num_slice_groups_minus1
		w.Ue( 0 );  // num_ref_idx_l0_default_active_minus1
		w.Ue( 0 );  // num_ref_idx_l1_default_active_minus1
		w.Bit( 0 ); // weighted_pred_flag
		w.Bits( 0, 2 ); // weighted_bipred_idc
		w.Se( 0 );  // pic_init_qp_minus26
		w.Se( 0 );  // pic_init_qs_minus26
		w.Se( 0 );  // chroma_qp_index_offset
		w.Bit( 1 ); // deblocking_filter_control_present_flag
		w.Bit( 0 ); // constrained_intra_pred_flag
		w.Bit( 0 ); // redundant_pic_cnt_present_flag
		return w.Nal( 0x68 ); // nal_ref_idc 3, PPS
	}

	/// <summary>
	/// A P picture where every macroblock is skipped: a copy of the last reference picture, a few bytes long. Made a
	/// reference itself so a run of them carries frame_num on (<paramref name="prev"/> is updated).
	/// </summary>
	public static byte[] SkipFrame( Sps sps, int ppsId, ref SliceInfo prev, int pocStep = 2 )
	{
		var maxFrameNum = 1 << sps.Log2MaxFrameNum;
		var frameNum = prev.Reference || prev.Idr ? (prev.FrameNum + 1) % maxFrameNum : prev.FrameNum;
		var poc = sps.PocType == 0 ? (prev.PocLsb + pocStep) & ((1 << sps.Log2MaxPocLsb) - 1) : 0;

		var w = new BitWriter();
		w.Ue( 0 );     // first_mb_in_slice
		w.Ue( 5 );     // slice_type: P, every slice
		w.Ue( ppsId );
		if ( sps.SeparateColourPlane ) w.Bits( 0, 2 );
		w.Bits( frameNum, sps.Log2MaxFrameNum );
		if ( !sps.FrameMbsOnly ) w.Bit( 0 ); // field_pic_flag
		if ( sps.PocType == 0 ) w.Bits( poc, sps.Log2MaxPocLsb );
		if ( sps.PocType == 1 && !sps.DeltaPicOrderAlwaysZero ) w.Se( 0 );
		w.Bit( 0 );    // num_ref_idx_active_override_flag
		w.Bit( 0 );    // ref_pic_list_modification_flag_l0
		w.Bit( 0 );    // adaptive_ref_pic_marking_mode_flag
		w.Se( 0 );     // slice_qp_delta
		w.Ue( 1 );     // disable_deblocking_filter_idc: off, a copy needs none
		w.Ue( sps.WidthMbs * sps.HeightMbs ); // mb_skip_run: all of them

		prev = new SliceInfo { FrameNum = frameNum, PocLsb = poc, Reference = true };
		return w.Nal( 0x41 ); // nal_ref_idc 2, non-IDR slice
	}

	sealed class BitWriter
	{
		readonly List<byte> bytes = new();
		int current, count;

		public void Bit( int b )
		{
			current = (current << 1) | (b & 1);
			if ( ++count == 8 ) { bytes.Add( (byte)current ); current = count = 0; }
		}

		public void Bits( int v, int n )
		{
			for ( int i = n - 1; i >= 0; i-- ) Bit( (v >> i) & 1 );
		}

		public void Ue( int v )
		{
			var x = v + 1;
			var len = 0;
			while ( (x >> len) > 1 ) len++;
			Bits( 0, len );
			Bits( x, len + 1 );
		}

		public void Se( int v ) => Ue( v <= 0 ? -2 * v : 2 * v - 1 );

		/// <summary> The NAL unit: header byte, the bits with rbsp trailing bits, emulation prevention added. </summary>
		public byte[] Nal( byte header )
		{
			Bit( 1 );
			while ( count != 0 ) Bit( 0 );
			var result = new List<byte>( bytes.Count + 8 ) { header };
			var zeros = 0;
			foreach ( var b in bytes )
			{
				if ( zeros >= 2 && b <= 3 ) { result.Add( 3 ); zeros = 0; }
				result.Add( b );
				zeros = b == 0 ? zeros + 1 : 0;
			}
			return result.ToArray();
		}
	}

	static byte[] RemoveEmulationPrevention( byte[] nal, int start )
	{
		var result = new List<byte>( nal.Length );
		var zeros = 0;
		for ( int i = start; i < nal.Length; i++ )
		{
			if ( zeros >= 2 && nal[i] == 3 ) { zeros = 0; continue; }
			zeros = nal[i] == 0 ? zeros + 1 : 0;
			result.Add( nal[i] );
		}
		return result.ToArray();
	}

	sealed class BitReader
	{
		readonly byte[] data;
		int bit;

		public BitReader( byte[] data ) { this.data = data; }

		public int Bits( int n )
		{
			var v = 0;
			for ( int i = 0; i < n; i++ )
			{
				var b = bit >> 3 < data.Length ? (data[bit >> 3] >> (7 - (bit & 7))) & 1 : 0;
				v = (v << 1) | b;
				bit++;
			}
			return v;
		}

		public int Ue()
		{
			var zeros = 0;
			while ( Bits( 1 ) == 0 && zeros < 31 ) zeros++;
			return (1 << zeros) - 1 + (zeros > 0 ? Bits( zeros ) : 0);
		}

		public int Se()
		{
			var v = Ue();
			return (v & 1) == 1 ? (v + 1) / 2 : -(v / 2);
		}
	}
}
