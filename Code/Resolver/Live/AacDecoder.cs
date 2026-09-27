using System;

namespace Bimp.Resolver.Live;

/// <summary>
/// AAC-LC decoder (ISO/IEC 14496-3 GA, 1024-sample frames): one raw access unit in, interleaved 16-bit PCM out.
/// The engine only decodes AAC inside finished MP4 files, which ties live audio to the segment latency - this
/// lets low-latency live streams play their AAC beside the video, like G.711 (see <see cref="LivePcmAudio"/>).
/// <para>
/// Supports SCE/CPE/LFE, section and scalefactor data, pulse data, M/S and intensity stereo, PNS, TNS, and the
/// long / start / eight-short / stop windows with sine and KBD shapes. HE-AAC decodes its AAC-LC core (half
/// band, SBR ignored). Main/LTP prediction and coupling channels aren't supported. Output is mono or stereo
/// (anything with more channels is folded down to its front three).
/// </para>
/// </summary>
public sealed class AacDecoder
{
	const int ZeroHcb = 0, EscHcb = 11, NoiseHcb = 13, IntensityHcb2 = 14, IntensityHcb = 15;
	const int OnlyLong = 0, LongStart = 1, EightShort = 2, LongStop = 3;

	public int SampleRate { get; }

	/// <summary> Output channels: 1 or 2. </summary>
	public int Channels { get; }

	readonly int rateIndex;
	readonly short[] swbLong, swbShort;
	readonly int numSwbLong, numSwbShort;

	// per channel state: the second half of the last frame, and its window shape
	readonly float[][] overlap = { new float[1024], new float[1024], new float[1024], new float[1024] };
	readonly int[] previousShape = new int[4];

	// scratch
	readonly float[][] spectrum = { new float[1024], new float[1024] };
	readonly float[] timeBuffer = new float[2048];
	readonly float[][] pcm = { new float[1024], new float[1024], new float[1024], new float[1024] };
	uint noiseState = 0x1f2e3d4c;
	readonly Ics icsA = new(), icsB = new();

	public AacDecoder( AacConfig config )
	{
		var asc = config.Asc;
		var r = new BitReader( asc );
		var objectType = ReadObjectType( r );
		var index = r.Read( 4 );
		var rate = index == 15 ? r.Read( 24 ) : AacConfig.RateOf( index );
		var channelConfig = r.Read( 4 );

		// HE-AAC signalled explicitly: the core is described after the SBR rate
		if ( objectType is 5 or 29 )
		{
			var extIndex = r.Read( 4 );
			if ( extIndex == 15 ) r.Read( 24 );
			objectType = ReadObjectType( r );
		}
		if ( objectType != 2 ) throw new NotSupportedException( $"AAC object type {objectType} (only AAC-LC)" );
		if ( r.Read( 1 ) == 1 ) throw new NotSupportedException( "960-sample AAC frames" );

		rateIndex = index == 15 ? NearestRateIndex( rate ) : index;
		SampleRate = rate > 0 ? rate : AacConfig.RateOf( rateIndex );
		Channels = channelConfig == 1 ? 1 : 2;

		(swbLong, swbShort) = rateIndex switch
		{
			0 or 1 => (AacTables.Sfb96_1024, AacTables.Sfb96_128),
			2 => (AacTables.Sfb64_1024, AacTables.Sfb64_128),
			3 or 4 => (AacTables.Sfb48_1024, AacTables.Sfb48_128),
			5 => (AacTables.Sfb32_1024, AacTables.Sfb48_128),
			6 or 7 => (AacTables.Sfb24_1024, AacTables.Sfb24_128),
			8 or 9 or 10 => (AacTables.Sfb16_1024, AacTables.Sfb16_128),
			_ => (AacTables.Sfb8_1024, AacTables.Sfb8_128),
		};
		numSwbLong = swbLong.Length - 1;
		numSwbShort = swbShort.Length - 1;
	}

	static int ReadObjectType( BitReader r )
	{
		var t = r.Read( 5 );
		return t == 31 ? 32 + r.Read( 6 ) : t;
	}

	static int NearestRateIndex( int rate )
	{
		int best = 3, bestDiff = int.MaxValue;
		for ( int i = 0; i < 12; i++ )
		{
			var d = Math.Abs( AacConfig.RateOf( i ) - rate );
			if ( d < bestDiff ) { best = i; bestDiff = d; }
		}
		return best;
	}

	/// <summary>
	/// Decode one access unit (a raw_data_block) to 1024 interleaved samples per channel. Null if the frame can't be
	/// decoded (it's skipped, the next one starts clean enough).
	/// </summary>
	public short[] Decode( byte[] frame )
	{
		var r = new BitReader( frame );
		var outputs = 0; // decoded channels, in pcm[]
		int centre = -1, left = -1, right = -1;

		try
		{
			while ( r.BitsLeft >= 3 )
			{
				var id = r.Read( 3 );
				if ( id == 7 ) break; // END

				switch ( id )
				{
					case 0: // SCE
					case 3: // LFE
						r.Read( 4 );
						if ( outputs < 4 )
						{
							var ics = icsA;
							DecodeIcs( r, ics, false );
							Reconstruct( ics, spectrum[0] );
							if ( id == 0 && centre < 0 && left < 0 ) centre = outputs;
							Synthesize( ics, spectrum[0], outputs++ );
						}
						else SkipIcs( r );
						break;

					case 1: // CPE
						r.Read( 4 );
						DecodeCpe( r, outputs );
						if ( left < 0 ) { left = outputs; right = outputs + 1; }
						outputs += 2;
						break;

					case 4: // DSE
						{
							r.Read( 4 );
							var align = r.Read( 1 );
							var count = r.Read( 8 );
							if ( count == 255 ) count += r.Read( 8 );
							if ( align == 1 ) r.ByteAlign();
							r.Skip( count * 8 );
							break;
						}

					case 5: // PCE
						SkipPce( r );
						break;

					case 6: // FIL (SBR and other extensions: ignored)
						{
							var count = r.Read( 4 );
							if ( count == 15 ) count += r.Read( 8 ) - 1;
							r.Skip( count * 8 );
							break;
						}

					default: // CCE: coupling isn't supported
						return null;
				}
			}
		}
		catch ( IndexOutOfRangeException )
		{
			return null; // truncated or corrupt frame
		}

		if ( outputs == 0 ) return null;

		var result = new short[1024 * Channels];
		if ( Channels == 1 )
		{
			var src = pcm[centre >= 0 ? centre : 0];
			for ( int i = 0; i < 1024; i++ ) result[i] = Clip( src[i] );
			return result;
		}

		if ( left < 0 )
		{
			// a mono stream without channel config 1: both sides the same
			var src = pcm[centre >= 0 ? centre : 0];
			for ( int i = 0; i < 1024; i++ ) result[2 * i] = result[2 * i + 1] = Clip( src[i] );
			return result;
		}

		var l = pcm[left];
		var rr = pcm[right];
		var c = centre >= 0 ? pcm[centre] : null;
		for ( int i = 0; i < 1024; i++ )
		{
			var cc = c is null ? 0 : c[i] * 0.7071f;
			result[2 * i] = Clip( l[i] + cc );
			result[2 * i + 1] = Clip( rr[i] + cc );
		}
		return result;
	}

	static short Clip( float v ) => (short)Math.Clamp( (int)MathF.Round( v ), short.MinValue, short.MaxValue );

	//
	// bitstream
	//

	sealed class Ics
	{
		public int GlobalGain;
		public int WindowSequence, WindowShape, MaxSfb;
		public int NumWindows, NumWindowGroups;
		public readonly int[] WindowGroupLength = new int[8];
		public readonly int[] SfbCb = new int[8 * 64];
		public readonly int[] ScaleFactors = new int[8 * 64];
		public bool PulsePresent;
		public int PulseCount, PulseStartSfb;
		public readonly int[] PulseOffset = new int[4], PulseAmp = new int[4];
		public bool TnsPresent;
		public readonly int[] TnsFilters = new int[8];
		public readonly int[] TnsCoefRes = new int[8];
		public readonly int[,] TnsLength = new int[8, 4], TnsOrder = new int[8, 4], TnsDirection = new int[8, 4];
		public readonly float[,,] TnsLpc = new float[8, 4, 32];
		public readonly int[] Quant = new int[1024];
		public bool IsLong => WindowSequence != EightShort;
	}

	void ReadIcsInfo( BitReader r, Ics ics )
	{
		r.Read( 1 ); // ics_reserved_bit
		ics.WindowSequence = r.Read( 2 );
		ics.WindowShape = r.Read( 1 );
		if ( ics.WindowSequence == EightShort )
		{
			ics.MaxSfb = r.Read( 4 );
			var grouping = r.Read( 7 );
			ics.NumWindows = 8;
			ics.NumWindowGroups = 1;
			ics.WindowGroupLength[0] = 1;
			for ( int i = 0; i < 7; i++ )
			{
				if ( (grouping & (1 << (6 - i))) != 0 ) ics.WindowGroupLength[ics.NumWindowGroups - 1]++;
				else ics.WindowGroupLength[ics.NumWindowGroups++] = 1;
			}
		}
		else
		{
			ics.MaxSfb = r.Read( 6 );
			ics.NumWindows = 1;
			ics.NumWindowGroups = 1;
			ics.WindowGroupLength[0] = 1;
			if ( r.Read( 1 ) == 1 ) throw new IndexOutOfRangeException( "prediction" ); // AAC Main only
		}
		var numSwb = ics.IsLong ? numSwbLong : numSwbShort;
		if ( ics.MaxSfb > numSwb ) throw new IndexOutOfRangeException( "max_sfb" );
	}

	void DecodeIcs( BitReader r, Ics ics, bool commonWindow )
	{
		ics.GlobalGain = r.Read( 8 );
		if ( !commonWindow ) ReadIcsInfo( r, ics );
		ReadSections( r, ics );
		ReadScaleFactors( r, ics );

		ics.PulsePresent = r.Read( 1 ) == 1;
		if ( ics.PulsePresent )
		{
			if ( !ics.IsLong ) throw new IndexOutOfRangeException( "pulse in short window" );
			ics.PulseCount = r.Read( 2 ) + 1;
			ics.PulseStartSfb = r.Read( 6 );
			for ( int i = 0; i < ics.PulseCount; i++ )
			{
				ics.PulseOffset[i] = r.Read( 5 );
				ics.PulseAmp[i] = r.Read( 4 );
			}
		}

		ics.TnsPresent = r.Read( 1 ) == 1;
		if ( ics.TnsPresent ) ReadTns( r, ics );

		if ( r.Read( 1 ) == 1 ) throw new IndexOutOfRangeException( "gain control" ); // AAC SSR only

		ReadSpectrum( r, ics );
	}

	void ReadSections( BitReader r, Ics ics )
	{
		var bits = ics.IsLong ? 5 : 3;
		var escape = (1 << bits) - 1;
		for ( int g = 0; g < ics.NumWindowGroups; g++ )
		{
			var k = 0;
			while ( k < ics.MaxSfb )
			{
				var cb = r.Read( 4 );
				if ( cb == 12 ) throw new IndexOutOfRangeException( "reserved codebook" );
				var length = 0;
				int incr;
				while ( (incr = r.Read( bits )) == escape ) length += escape;
				length += incr;
				if ( k + length > ics.MaxSfb ) throw new IndexOutOfRangeException( "section" );
				for ( int s = k; s < k + length; s++ ) ics.SfbCb[g * 64 + s] = cb;
				k += length;
			}
		}
	}

	void ReadScaleFactors( BitReader r, Ics ics )
	{
		var scaleFactor = ics.GlobalGain;
		var isPosition = 0;
		var noiseEnergy = ics.GlobalGain - 90;
		var noisePcm = true;
		for ( int g = 0; g < ics.NumWindowGroups; g++ )
		{
			for ( int sfb = 0; sfb < ics.MaxSfb; sfb++ )
			{
				var i = g * 64 + sfb;
				switch ( ics.SfbCb[i] )
				{
					case ZeroHcb:
						ics.ScaleFactors[i] = 0;
						break;
					case IntensityHcb:
					case IntensityHcb2:
						isPosition += Huffman.ScaleFactor.Decode( r ) - 60;
						ics.ScaleFactors[i] = isPosition;
						break;
					case NoiseHcb:
						if ( noisePcm ) { noisePcm = false; noiseEnergy += r.Read( 9 ) - 256; }
						else noiseEnergy += Huffman.ScaleFactor.Decode( r ) - 60;
						ics.ScaleFactors[i] = noiseEnergy;
						break;
					default:
						scaleFactor += Huffman.ScaleFactor.Decode( r ) - 60;
						ics.ScaleFactors[i] = scaleFactor;
						break;
				}
			}
		}
	}

	void ReadTns( BitReader r, Ics ics )
	{
		var isLong = ics.IsLong;
		Span<float> parcor = stackalloc float[32];
		Span<float> a = stackalloc float[33];
		Span<float> b = stackalloc float[33];
		for ( int w = 0; w < ics.NumWindows; w++ )
		{
			var filters = ics.TnsFilters[w] = r.Read( isLong ? 2 : 1 );
			if ( filters == 0 ) continue;
			var coefRes = ics.TnsCoefRes[w] = r.Read( 1 );
			for ( int f = 0; f < filters; f++ )
			{
				ics.TnsLength[w, f] = r.Read( isLong ? 6 : 4 );
				var order = ics.TnsOrder[w, f] = r.Read( isLong ? 5 : 3 );
				if ( order == 0 ) continue;
				ics.TnsDirection[w, f] = r.Read( 1 );
				var compress = r.Read( 1 );
				var coefBits = coefRes + 3 - compress;

				// the coefficients are quantized reflection coefficients: back to a direct form LPC
				var iqfac = ((1 << (coefRes + 3 - 1)) - 0.5f) / (MathF.PI / 2);
				var iqfacM = ((1 << (coefRes + 3 - 1)) + 0.5f) / (MathF.PI / 2);
				for ( int i = 0; i < order; i++ )
				{
					var v = r.Read( coefBits );
					if ( (v & (1 << (coefBits - 1))) != 0 ) v -= 1 << coefBits;
					parcor[i] = MathF.Sin( v / (v >= 0 ? iqfac : iqfacM) );
				}

				a.Clear();
				a[0] = 1;
				for ( int m = 1; m <= order; m++ )
				{
					for ( int i = 1; i < m; i++ ) b[i] = a[i] + parcor[m - 1] * a[m - i];
					for ( int i = 1; i < m; i++ ) a[i] = b[i];
					a[m] = parcor[m - 1];
				}
				for ( int i = 0; i <= order; i++ ) ics.TnsLpc[w, f, i] = a[i];
			}
		}
	}

	void ReadSpectrum( BitReader r, Ics ics )
	{
		Array.Clear( ics.Quant );
		var swb = ics.IsLong ? swbLong : swbShort;
		var groupStart = 0; // first window of the group

		for ( int g = 0; g < ics.NumWindowGroups; g++ )
		{
			var groupLength = ics.WindowGroupLength[g];
			for ( int sfb = 0; sfb < ics.MaxSfb; sfb++ )
			{
				var cb = ics.SfbCb[g * 64 + sfb];
				if ( cb is ZeroHcb or NoiseHcb or IntensityHcb or IntensityHcb2 ) continue;
				var book = Huffman.Spectral[cb];
				var width = swb[sfb + 1] - swb[sfb];

				// within a group, a band's coefficients come window by window
				for ( int w = 0; w < groupLength; w++ )
				{
					var at = (groupStart + w) * 128 + swb[sfb];
					if ( ics.IsLong ) at = swb[sfb];
					for ( int k = 0; k < width; k += book.Dimension )
						book.DecodeValues( r, ics.Quant, at + k );
				}
			}
			groupStart += groupLength;
		}

		if ( ics.PulsePresent )
		{
			int k = swbLong[Math.Min( ics.PulseStartSfb, numSwbLong )];
			for ( int i = 0; i < ics.PulseCount; i++ )
			{
				k += ics.PulseOffset[i];
				if ( k >= 1024 ) break;
				ics.Quant[k] += ics.Quant[k] > 0 ? ics.PulseAmp[i] : -ics.PulseAmp[i];
			}
		}
	}

	void SkipIcs( BitReader r )
	{
		// an extra channel we won't play still has to be read through
		DecodeIcs( r, icsB, false );
	}

	void DecodeCpe( BitReader r, int outputs )
	{
		var a = icsA;
		var b = icsB;
		var commonWindow = r.Read( 1 ) == 1;
		var msMask = 0;
		Span<bool> msUsed = stackalloc bool[8 * 64];
		if ( commonWindow )
		{
			ReadIcsInfo( r, a );
			CopyInfo( a, b );
			msMask = r.Read( 2 );
			if ( msMask == 1 )
				for ( int g = 0; g < a.NumWindowGroups; g++ )
					for ( int sfb = 0; sfb < a.MaxSfb; sfb++ )
						msUsed[g * 64 + sfb] = r.Read( 1 ) == 1;
			else if ( msMask == 2 ) msUsed.Fill( true );
		}
		DecodeIcs( r, a, commonWindow );
		DecodeIcs( r, b, commonWindow );
		if ( outputs + 1 >= 4 ) return;

		var l = spectrum[0];
		var rr = spectrum[1];
		Dequantize( a, l );
		Dequantize( b, rr );

		// PNS: a band that's noise in both channels with M/S on gets the same noise in both
		Noise( a, l, null, Span<bool>.Empty );
		Noise( b, rr, commonWindow && msMask > 0 ? a : null, commonWindow ? msUsed : Span<bool>.Empty, l );

		if ( commonWindow && msMask > 0 ) MidSide( a, b, l, rr, msUsed );
		Intensity( b, l, rr, msUsed, msMask );

		Tns( a, l );
		Tns( b, rr );
		Synthesize( a, l, outputs );
		Synthesize( b, rr, outputs + 1 );
	}

	static void CopyInfo( Ics from, Ics to )
	{
		to.WindowSequence = from.WindowSequence;
		to.WindowShape = from.WindowShape;
		to.MaxSfb = from.MaxSfb;
		to.NumWindows = from.NumWindows;
		to.NumWindowGroups = from.NumWindowGroups;
		Array.Copy( from.WindowGroupLength, to.WindowGroupLength, 8 );
	}

	void SkipPce( BitReader r )
	{
		r.Read( 4 + 2 + 4 ); // tag, object type, sampling index
		int front = r.Read( 4 ), side = r.Read( 4 ), back = r.Read( 4 ), lfe = r.Read( 2 ), assoc = r.Read( 3 ), cc = r.Read( 4 );
		if ( r.Read( 1 ) == 1 ) r.Read( 4 ); // mono mixdown
		if ( r.Read( 1 ) == 1 ) r.Read( 4 ); // stereo mixdown
		if ( r.Read( 1 ) == 1 ) r.Read( 3 ); // matrix mixdown
		r.Skip( (front + side + back) * 5 + lfe * 4 + assoc * 4 + cc * 5 );
		r.ByteAlign();
		r.Skip( r.Read( 8 ) * 8 ); // comment
	}

	//
	// spectral processing
	//

	/// <summary> Inverse quantization and scaling of one channel into <paramref name="spec"/> (1024 values, window by window). </summary>
	void Dequantize( Ics ics, float[] spec )
	{
		Array.Clear( spec );
		var swb = ics.IsLong ? swbLong : swbShort;
		var window = 0;
		for ( int g = 0; g < ics.NumWindowGroups; g++ )
		{
			for ( int w = 0; w < ics.WindowGroupLength[g]; w++, window++ )
			{
				var baseIndex = window * 128;
				for ( int sfb = 0; sfb < ics.MaxSfb; sfb++ )
				{
					var cb = ics.SfbCb[g * 64 + sfb];
					if ( cb is ZeroHcb or NoiseHcb or IntensityHcb or IntensityHcb2 ) continue;
					var gain = MathF.Pow( 2, 0.25f * (ics.ScaleFactors[g * 64 + sfb] - 100) );
					for ( int k = swb[sfb]; k < swb[sfb + 1]; k++ )
					{
						var q = ics.Quant[baseIndex + k];
						if ( q != 0 ) spec[baseIndex + k] = (q > 0 ? Pow43( q ) : -Pow43( -q )) * gain;
					}
				}
			}
		}
	}

	static readonly float[] Pow43Table = MakePow43();
	static float[] MakePow43()
	{
		var t = new float[8192];
		for ( int i = 0; i < t.Length; i++ ) t[i] = MathF.Pow( i, 4.0f / 3.0f );
		return t;
	}
	static float Pow43( int q ) => q < 8192 ? Pow43Table[q] : MathF.Pow( q, 4.0f / 3.0f );

	/// <summary> Only the single channel path: dequantize, noise, TNS. </summary>
	void Reconstruct( Ics ics, float[] spec )
	{
		Dequantize( ics, spec );
		Noise( ics, spec, null, Span<bool>.Empty );
		Tns( ics, spec );
	}

	/// <summary> Perceptual noise substitution: noise bands get random values of the coded energy. </summary>
	void Noise( Ics ics, float[] spec, Ics other, ReadOnlySpan<bool> msUsed, float[] otherSpec = null )
	{
		var swb = ics.IsLong ? swbLong : swbShort;
		var window = 0;
		for ( int g = 0; g < ics.NumWindowGroups; g++ )
		{
			for ( int w = 0; w < ics.WindowGroupLength[g]; w++, window++ )
			{
				var baseIndex = window * 128;
				for ( int sfb = 0; sfb < ics.MaxSfb; sfb++ )
				{
					var i = g * 64 + sfb;
					if ( ics.SfbCb[i] != NoiseHcb ) continue;
					int from = baseIndex + swb[sfb], to = baseIndex + swb[sfb + 1];

					var correlated = other is not null && otherSpec is not null && other.SfbCb[i] == NoiseHcb && msUsed.Length > i && msUsed[i];
					float energy = 0;
					for ( int k = from; k < to; k++ )
					{
						var v = correlated ? otherSpec[k] : NextNoise();
						spec[k] = v;
						energy += v * v;
					}
					if ( energy <= 0 ) continue;
					var scale = MathF.Pow( 2, 0.25f * ics.ScaleFactors[i] ) / MathF.Sqrt( energy );
					for ( int k = from; k < to; k++ ) spec[k] *= scale;
				}
			}
		}
	}

	float NextNoise()
	{
		noiseState = noiseState * 1664525u + 1013904223u;
		return (int)noiseState / 2147483648.0f;
	}

	void MidSide( Ics ics, Ics right, float[] l, float[] r, ReadOnlySpan<bool> msUsed )
	{
		var swb = ics.IsLong ? swbLong : swbShort;
		var window = 0;
		for ( int g = 0; g < ics.NumWindowGroups; g++ )
		{
			for ( int w = 0; w < ics.WindowGroupLength[g]; w++, window++ )
			{
				for ( int sfb = 0; sfb < ics.MaxSfb; sfb++ )
				{
					var i = g * 64 + sfb;
					if ( !msUsed[i] || right.SfbCb[i] >= NoiseHcb || ics.SfbCb[i] == NoiseHcb ) continue;
					for ( int k = window * 128 + swb[sfb]; k < window * 128 + swb[sfb + 1]; k++ )
					{
						var m = l[k];
						var s = r[k];
						l[k] = m + s;
						r[k] = m - s;
					}
				}
			}
		}
	}

	void Intensity( Ics right, float[] l, float[] r, ReadOnlySpan<bool> msUsed, int msMask )
	{
		var swb = right.IsLong ? swbLong : swbShort;
		var window = 0;
		for ( int g = 0; g < right.NumWindowGroups; g++ )
		{
			for ( int w = 0; w < right.WindowGroupLength[g]; w++, window++ )
			{
				for ( int sfb = 0; sfb < right.MaxSfb; sfb++ )
				{
					var i = g * 64 + sfb;
					var cb = right.SfbCb[i];
					if ( cb is not (IntensityHcb or IntensityHcb2) ) continue;
					var scale = MathF.Pow( 0.5f, 0.25f * right.ScaleFactors[i] );
					if ( cb == IntensityHcb2 ) scale = -scale;
					if ( msMask > 0 && msUsed[i] ) scale = -scale;
					for ( int k = window * 128 + swb[sfb]; k < window * 128 + swb[sfb + 1]; k++ ) r[k] = l[k] * scale;
				}
			}
		}
	}

	void Tns( Ics ics, float[] spec )
	{
		if ( !ics.TnsPresent ) return;
		var isLong = ics.IsLong;
		var swb = isLong ? swbLong : swbShort;
		var numSwb = isLong ? numSwbLong : numSwbShort;
		var maxBands = Math.Min( (isLong ? AacTables.TnsMaxBandsLong : AacTables.TnsMaxBandsShort)[Math.Min( rateIndex, 11 )], ics.MaxSfb );
		Span<float> state = stackalloc float[20];

		for ( int w = 0; w < ics.NumWindows; w++ )
		{
			var bottom = numSwb;
			for ( int f = 0; f < ics.TnsFilters[w]; f++ )
			{
				var top = bottom;
				bottom = Math.Max( top - ics.TnsLength[w, f], 0 );
				var order = Math.Min( ics.TnsOrder[w, f], isLong ? 20 : 7 );
				if ( order == 0 ) continue;

				var start = swb[Math.Min( bottom, maxBands )];
				var end = swb[Math.Min( top, maxBands )];
				var size = end - start;
				if ( size <= 0 ) continue;

				int at = w * 128 + (ics.TnsDirection[w, f] == 1 ? end - 1 : start);
				var step = ics.TnsDirection[w, f] == 1 ? -1 : 1;
				state.Clear();
				for ( int n = 0; n < size; n++, at += step )
				{
					// all-pole filter: y = x - sum(a[i] * y[n-i])
					var y = spec[at];
					for ( int i = 0; i < order; i++ ) y -= state[i] * ics.TnsLpc[w, f, i + 1];
					for ( int i = order - 1; i > 0; i-- ) state[i] = state[i - 1];
					state[0] = y;
					spec[at] = y;
				}
			}
		}
	}

	//
	// filterbank
	//

	static readonly float[] SineLong = SineWindow( 2048 ), SineShort = SineWindow( 256 );
	static readonly float[] KbdLong = KbdWindow( 2048, 4 ), KbdShort = KbdWindow( 256, 6 );

	static float[] SineWindow( int n )
	{
		var w = new float[n / 2];
		for ( int i = 0; i < n / 2; i++ ) w[i] = MathF.Sin( MathF.PI / n * (i + 0.5f) );
		return w;
	}

	/// <summary> The rising half of a Kaiser-Bessel-derived window. </summary>
	static float[] KbdWindow( int n, double alpha )
	{
		var half = n / 2;
		var kernel = new double[half + 1];
		for ( int i = 0; i <= half; i++ )
		{
			var x = (i - half / 2.0) / (half / 2.0);
			kernel[i] = BesselI0( Math.PI * alpha * Math.Sqrt( Math.Max( 0, 1 - x * x ) ) );
		}
		var total = 0.0;
		for ( int i = 0; i <= half; i++ ) total += kernel[i];
		var w = new float[half];
		var sum = 0.0;
		for ( int i = 0; i < half; i++ )
		{
			sum += kernel[i];
			w[i] = (float)Math.Sqrt( sum / total );
		}
		return w;
	}

	static double BesselI0( double x )
	{
		double sum = 1, term = 1;
		for ( int k = 1; k < 50; k++ )
		{
			term *= (x / (2 * k)) * (x / (2 * k));
			sum += term;
			if ( term < 1e-12 * sum ) break;
		}
		return sum;
	}

	static float[] Rising( int shape, bool isLong ) => shape == 1 ? (isLong ? KbdLong : KbdShort) : (isLong ? SineLong : SineShort);

	/// <summary> IMDCT, window, overlap-add: 1024 new samples for output channel <paramref name="ch"/>. </summary>
	void Synthesize( Ics ics, float[] spec, int ch )
	{
		if ( ch >= 4 ) return;
		var t = timeBuffer;
		var prevShape = previousShape[ch];
		var shape = ics.WindowShape;
		var longLeft = Rising( prevShape, true );
		var longRight = Rising( shape, true );
		var shortLeft = Rising( prevShape, false );
		var shortRight = Rising( shape, false );

		if ( ics.WindowSequence == EightShort )
		{
			Array.Clear( t );
			Span<float> block = stackalloc float[256];
			for ( int w = 0; w < 8; w++ )
			{
				Imdct.Short.Inverse( spec.AsSpan( w * 128, 128 ), block );
				var left = w == 0 ? shortLeft : shortRight;
				var at = 448 + w * 128;
				for ( int i = 0; i < 128; i++ )
				{
					t[at + i] += block[i] * left[i];
					t[at + 128 + i] += block[128 + i] * shortRight[127 - i];
				}
			}
		}
		else
		{
			Imdct.Long.Inverse( spec.AsSpan( 0, 1024 ), t );
			switch ( ics.WindowSequence )
			{
				case OnlyLong:
					for ( int i = 0; i < 1024; i++ ) { t[i] *= longLeft[i]; t[1024 + i] *= longRight[1023 - i]; }
					break;
				case LongStart:
					for ( int i = 0; i < 1024; i++ ) t[i] *= longLeft[i];
					for ( int i = 0; i < 128; i++ ) t[1472 + i] *= shortRight[127 - i];
					for ( int i = 1600; i < 2048; i++ ) t[i] = 0;
					break;
				case LongStop:
					for ( int i = 0; i < 448; i++ ) t[i] = 0;
					for ( int i = 0; i < 128; i++ ) t[448 + i] *= shortLeft[i];
					for ( int i = 0; i < 1024; i++ ) t[1024 + i] *= longRight[1023 - i];
					break;
			}
		}

		var o = overlap[ch];
		var output = pcm[ch];
		for ( int i = 0; i < 1024; i++ )
		{
			output[i] = t[i] + o[i];
			o[i] = t[1024 + i];
		}
		previousShape[ch] = shape;
	}

	/// <summary> Inverse MDCT through a DCT-IV of half the length, done with a complex FFT of a quarter. </summary>
	sealed class Imdct
	{
		public static readonly Imdct Long = new( 2048 ), Short = new( 256 );

		readonly int n, m;        // window length, coefficient count (n / 2)
		readonly float[] twiddleCos, twiddleSin; // exp(-i pi (4k+1) / 4m)
		readonly Fft fft;
		readonly float[] re, im, dct;

		Imdct( int length )
		{
			n = length;
			m = length / 2;
			twiddleCos = new float[m / 2];
			twiddleSin = new float[m / 2];
			for ( int k = 0; k < m / 2; k++ )
			{
				var a = -Math.PI * (4 * k + 1) / (4.0 * m);
				twiddleCos[k] = (float)Math.Cos( a );
				twiddleSin[k] = (float)Math.Sin( a );
			}
			fft = new Fft( m / 2 );
			re = new float[m / 2];
			im = new float[m / 2];
			dct = new float[m];
		}

		/// <summary> <paramref name="spec"/>: m coefficients. <paramref name="output"/>: n samples, scaled by 2/n. </summary>
		public void Inverse( ReadOnlySpan<float> spec, Span<float> output )
		{
			var half = m / 2;

			// DCT-IV of spec into dct[]
			for ( int k = 0; k < half; k++ )
			{
				float xr = spec[2 * k], xi = spec[m - 1 - 2 * k];
				re[k] = xr * twiddleCos[k] - xi * twiddleSin[k];
				im[k] = xr * twiddleSin[k] + xi * twiddleCos[k];
			}
			fft.Forward( re, im );
			for ( int k = 0; k < half; k++ )
			{
				float yr = re[k] * twiddleCos[k] - im[k] * twiddleSin[k];
				float yi = re[k] * twiddleSin[k] + im[k] * twiddleCos[k];
				dct[2 * k] = yr;
				dct[m - 1 - 2 * k] = -yi;
			}

			// unfold to the IMDCT's n outputs (phase n0 = m/2 + 1/2)
			var scale = 2.0f / n;
			for ( int i = 0; i < half; i++ ) output[i] = dct[i + half] * scale;
			for ( int i = half; i < 3 * half; i++ ) output[i] = -dct[3 * half - 1 - i] * scale;
			for ( int i = 3 * half; i < n; i++ ) output[i] = -dct[i - 3 * half] * scale;
		}
	}

	/// <summary> In-place radix-2 complex FFT (forward, e^-i). </summary>
	sealed class Fft
	{
		readonly int size;
		readonly int[] reverse;
		readonly float[] cos, sin;

		public Fft( int size )
		{
			this.size = size;
			reverse = new int[size];
			var bits = 0;
			while ( (1 << bits) < size ) bits++;
			for ( int i = 0; i < size; i++ )
			{
				var r = 0;
				for ( int b = 0; b < bits; b++ ) if ( (i & (1 << b)) != 0 ) r |= 1 << (bits - 1 - b);
				reverse[i] = r;
			}
			cos = new float[size / 2];
			sin = new float[size / 2];
			for ( int i = 0; i < size / 2; i++ )
			{
				cos[i] = (float)Math.Cos( -2 * Math.PI * i / size );
				sin[i] = (float)Math.Sin( -2 * Math.PI * i / size );
			}
		}

		public void Forward( float[] re, float[] im )
		{
			for ( int i = 0; i < size; i++ )
			{
				var j = reverse[i];
				if ( j <= i ) continue;
				(re[i], re[j]) = (re[j], re[i]);
				(im[i], im[j]) = (im[j], im[i]);
			}
			for ( int len = 2; len <= size; len <<= 1 )
			{
				var halfLen = len / 2;
				var step = size / len;
				for ( int i = 0; i < size; i += len )
				{
					for ( int j = 0; j < halfLen; j++ )
					{
						float wr = cos[j * step], wi = sin[j * step];
						int a = i + j, b = a + halfLen;
						float tr = re[b] * wr - im[b] * wi;
						float ti = re[b] * wi + im[b] * wr;
						re[b] = re[a] - tr;
						im[b] = im[a] - ti;
						re[a] += tr;
						im[a] += ti;
					}
				}
			}
		}
	}

	//
	// Huffman
	//

	/// <summary> A codebook as a binary tree, built once from the code / length tables. </summary>
	sealed class Huffman
	{
		public static readonly Huffman ScaleFactor = new( AacTables.CodeScf, AacTables.LengthScf, 1, 0, false, false );
		public static readonly Huffman[] Spectral =
		{
			null,
			new( AacTables.Code1, AacTables.Length1, 4, 3, true, false ),
			new( AacTables.Code2, AacTables.Length2, 4, 3, true, false ),
			new( AacTables.Code3, AacTables.Length3, 4, 3, false, false ),
			new( AacTables.Code4, AacTables.Length4, 4, 3, false, false ),
			new( AacTables.Code5, AacTables.Length5, 2, 9, true, false ),
			new( AacTables.Code6, AacTables.Length6, 2, 9, true, false ),
			new( AacTables.Code7, AacTables.Length7, 2, 8, false, false ),
			new( AacTables.Code8, AacTables.Length8, 2, 8, false, false ),
			new( AacTables.Code9, AacTables.Length9, 2, 13, false, false ),
			new( AacTables.Code10, AacTables.Length10, 2, 13, false, false ),
			new( AacTables.Code11, AacTables.Length11, 2, 17, false, true ),
		};

		public readonly int Dimension;
		readonly int modulo;
		readonly bool signed, escape;
		readonly int[] tree; // node * 2 + bit: > 0 next node, <= 0 leaf -(index)

		Huffman( uint[] codes, byte[] lengths, int dimension, int modulo, bool signed, bool escape )
		{
			Dimension = dimension;
			this.modulo = modulo;
			this.signed = signed;
			this.escape = escape;
			var nodes = new List<int> { 0, 0 };
			var used = new List<bool> { false, false };
			for ( int index = 0; index < codes.Length; index++ )
			{
				var node = 0;
				for ( int b = lengths[index] - 1; b >= 0; b-- )
				{
					var bit = (int)(codes[index] >> b) & 1;
					var slot = node * 2 + bit;
					if ( b == 0 )
					{
						nodes[slot] = -index;
						used[slot] = true;
						break;
					}
					if ( !used[slot] )
					{
						used[slot] = true;
						nodes[slot] = nodes.Count / 2;
						nodes.Add( 0 ); nodes.Add( 0 );
						used.Add( false ); used.Add( false );
					}
					node = nodes[slot];
				}
			}
			tree = nodes.ToArray();
		}

		public int Decode( BitReader r )
		{
			var node = 0;
			while ( true )
			{
				var next = tree[node * 2 + r.Read( 1 )];
				if ( next <= 0 ) return -next;
				node = next;
			}
		}

		/// <summary> One codeword's values (4 or 2), with their signs and escapes, into <paramref name="dest"/>. </summary>
		public void DecodeValues( BitReader r, int[] dest, int at )
		{
			var index = Decode( r );
			Span<int> v = stackalloc int[4];
			if ( Dimension == 4 )
			{
				v[0] = index / 27; v[1] = index / 9 % 3; v[2] = index / 3 % 3; v[3] = index % 3;
			}
			else
			{
				v[0] = index / modulo; v[1] = index % modulo;
			}

			if ( signed )
			{
				var offset = Dimension == 4 ? 1 : 4;
				for ( int i = 0; i < Dimension; i++ ) v[i] -= offset;
			}
			else
			{
				for ( int i = 0; i < Dimension; i++ )
					if ( v[i] != 0 && r.Read( 1 ) == 1 ) v[i] = -v[i];
			}

			if ( escape )
			{
				for ( int i = 0; i < 2; i++ )
				{
					if ( Math.Abs( v[i] ) != 16 ) continue;
					var n = 4;
					while ( r.Read( 1 ) == 1 ) { n++; if ( n > 12 ) throw new IndexOutOfRangeException( "escape" ); }
					var value = (1 << n) + r.Read( n );
					v[i] = v[i] < 0 ? -value : value;
				}
			}

			for ( int i = 0; i < Dimension && at + i < dest.Length; i++ ) dest[at + i] = v[i];
		}
	}

	sealed class BitReader
	{
		readonly byte[] data;
		int bit;

		public BitReader( byte[] data ) { this.data = data; }

		public int BitsLeft => data.Length * 8 - bit;

		public int Read( int n )
		{
			if ( n == 0 ) return 0;
			if ( bit + n > data.Length * 8 ) throw new IndexOutOfRangeException( "end of frame" );
			var v = 0;
			for ( int i = 0; i < n; i++, bit++ )
				v = (v << 1) | ((data[bit >> 3] >> (7 - (bit & 7))) & 1);
			return v;
		}

		public void Skip( int n )
		{
			if ( bit + n > data.Length * 8 ) throw new IndexOutOfRangeException( "end of frame" );
			bit += n;
		}

		public void ByteAlign() => bit = (bit + 7) & ~7;
	}
}
