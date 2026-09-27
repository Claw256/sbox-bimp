using System.Buffers.Binary;
using System.IO;

namespace Bimp.Resolver.Live;

/// <summary>
/// RTP/JPEG (RFC 2435, payload type 26): reassembles a frame's fragments and rebuilds the JPEG headers the
/// packets leave out (quantization tables, the standard Huffman tables, frame and scan headers).
/// </summary>
public sealed class RtpJpeg
{
	readonly MemoryStream scan = new();
	uint timestamp;
	bool active;
	int expectOffset;
	byte[] quant;
	int type, width, height, restartInterval, q;

	/// <summary> One RTP payload. Returns a complete JPEG when this packet ends a frame, else null. </summary>
	public byte[] Add( ReadOnlySpan<byte> p, uint ts, bool marker, bool lost )
	{
		if ( p.Length < 8 ) return null;
		var offset = (p[1] << 16) | (p[2] << 8) | p[3];
		var at = 8;
		var t = p[4];
		var quality = p[5];

		if ( offset == 0 || ts != timestamp )
		{
			scan.SetLength( 0 );
			active = offset == 0;
			expectOffset = 0;
			timestamp = ts;
			type = t & ~64;
			q = quality;
			width = p[6] * 8;
			height = p[7] * 8;
			restartInterval = 0;
		}
		if ( lost ) active = false;
		if ( !active ) return null;

		if ( (t & 64) != 0 )
		{
			if ( p.Length < at + 4 ) return null;
			restartInterval = (p[at] << 8) | p[at + 1];
			at += 4;
		}

		if ( offset == 0 && quality >= 128 )
		{
			if ( p.Length < at + 4 ) return null;
			var precision = p[at + 1];
			var length = (p[at + 2] << 8) | p[at + 3];
			at += 4;
			if ( length > 0 )
			{
				if ( precision != 0 || p.Length < at + length ) { active = false; return null; } // 16 bit tables: rare, unsupported
				quant = p.Slice( at, length ).ToArray();
				at += length;
			}
		}

		if ( offset != expectOffset ) { active = false; return null; }
		scan.Write( p[at..] );
		expectOffset += p.Length - at;

		if ( !marker ) return null;
		active = false;
		return Build();
	}

	byte[] Build()
	{
		var tables = q >= 128 ? quant : MakeTables( q );
		if ( tables is null || tables.Length < 64 || width == 0 || height == 0 || type > 1 ) return null;

		var o = new MemoryStream( (int)scan.Length + 1024 );
		o.Write( stackalloc byte[] { 0xFF, 0xD8 } );

		// quantization tables: luma, and chroma if there are two
		var count = Math.Min( 2, tables.Length / 64 );
		for ( int i = 0; i < count; i++ )
		{
			Marker( o, 0xDB, 65 );
			o.WriteByte( (byte)i );
			o.Write( tables, i * 64, 64 );
		}

		// baseline frame: 3 components, luma 2x1 (type 0, 4:2:2) or 2x2 (type 1, 4:2:0)
		Marker( o, 0xC0, 15 );
		o.WriteByte( 8 );
		U16( o, height );
		U16( o, width );
		o.WriteByte( 3 );
		o.Write( stackalloc byte[] { 0, (byte)(type == 0 ? 0x21 : 0x22), 0, 1, 0x11, (byte)(count - 1), 2, 0x11, (byte)(count - 1) } );

		Huffman( o, 0x00, DcLuminanceBits, DcLuminanceValues );
		Huffman( o, 0x10, AcLuminanceBits, AcLuminanceValues );
		Huffman( o, 0x01, DcChrominanceBits, DcChrominanceValues );
		Huffman( o, 0x11, AcChrominanceBits, AcChrominanceValues );

		if ( restartInterval > 0 )
		{
			Marker( o, 0xDD, 2 );
			U16( o, restartInterval );
		}

		Marker( o, 0xDA, 10 );
		o.Write( stackalloc byte[] { 3, 0, 0x00, 1, 0x11, 2, 0x11, 0, 63, 0 } );
		scan.Position = 0;
		scan.CopyTo( o );
		o.Write( stackalloc byte[] { 0xFF, 0xD9 } );
		return o.ToArray();
	}

	static void Marker( Stream o, byte marker, int length )
	{
		o.WriteByte( 0xFF );
		o.WriteByte( marker );
		U16( o, length + 2 );
	}

	static void U16( Stream o, int v )
	{
		o.WriteByte( (byte)(v >> 8) );
		o.WriteByte( (byte)v );
	}

	static void Huffman( Stream o, byte classId, byte[] bits, byte[] values )
	{
		Marker( o, 0xC4, 1 + 16 + values.Length );
		o.WriteByte( classId );
		o.Write( bits );
		o.Write( values );
	}

	/// <summary> RFC 2435 quality factors 1-99: the standard tables (ITU-T T.81 K.1, K.2), scaled. Zigzag order. </summary>
	static byte[] MakeTables( int quality )
	{
		var factor = Math.Clamp( quality, 1, 99 );
		var scale = factor < 50 ? 5000 / factor : 200 - factor * 2;
		var result = new byte[128];
		for ( int i = 0; i < 64; i++ )
		{
			result[i] = (byte)Math.Clamp( (LumaNatural[ZigZag[i]] * scale + 50) / 100, 1, 255 );
			result[64 + i] = (byte)Math.Clamp( (ChromaNatural[ZigZag[i]] * scale + 50) / 100, 1, 255 );
		}
		return result;
	}

	/// <summary> Natural (row major) index of each zigzag position. </summary>
	static readonly int[] ZigZag =
	{
		0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
		35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
	};

	static readonly int[] LumaNatural =
	{
		16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
		18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
	};

	static readonly int[] ChromaNatural =
	{
		17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
		99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
	};

	// standard Huffman tables (ITU-T T.81 K.3)
	static readonly byte[] DcLuminanceBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
	static readonly byte[] DcLuminanceValues = { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b };
	static readonly byte[] DcChrominanceBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
	static readonly byte[] DcChrominanceValues = { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b };
	static readonly byte[] AcLuminanceBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 125 };
	static readonly byte[] AcLuminanceValues = { 0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa };
	static readonly byte[] AcChrominanceBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 119 };
	static readonly byte[] AcChrominanceValues = { 0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa };
}

/// <summary>
/// Motion JPEG playback, no segments: each frame is decoded on a worker thread (<see cref="Bitmap.CreateFromBytes"/>,
/// measured 3 ms at 720p, 7 ms at 1080p), then shown at its own time, <see cref="PlayoutDelay"/> after the earliest
/// any frame has arrived. Showing each the moment it decoded passed the network's jitter straight to the screen
/// (measured: 138 gaps of 50 ms a minute). If frames come faster than they decode, the older waiting one is dropped.
/// </summary>
public sealed class LiveMjpeg : IDisposable
{
	/// <summary> How far behind the earliest arrival frames are shown - soaks up arrival jitter. </summary>
	const double PlayoutDelay = 0.06;

	(double time, byte[] jpeg)? waiting;
	bool decoding, disposed;
	readonly Queue<(double time, Bitmap bitmap)> ready = new();

	/// <summary> Wall clock minus stream time, the smallest seen (the least delayed arrival), drifting up slowly. </summary>
	double clockOffset = double.MaxValue;

	public int Received { get; private set; }
	public int Dropped { get; private set; }
	public int Failed { get; private set; }
	public double DecodeMilliseconds { get; private set; }

	/// <summary> Stream time (seconds) of the frame on screen. </summary>
	public double ShownTime { get; private set; }
	public bool HasShown { get; private set; }
	public string Size { get; private set; }

	/// <summary> A complete JPEG, received at stream time <paramref name="time"/> (seconds). Main thread. </summary>
	public void Add( double time, byte[] jpeg )
	{
		if ( disposed ) return;
		Received++;
		// pace by the frames' own clock: the least delayed arrival sets it (slowly forgiving, for clock drift)
		var offset = RealTime.NowDouble - time;
		clockOffset = offset < clockOffset ? offset : clockOffset + 0.0005;
		if ( decoding )
		{
			if ( waiting is not null ) Dropped++;
			waiting = (time, jpeg);
			return;
		}
		_ = Decode( time, jpeg );
	}

	async Task Decode( double time, byte[] jpeg )
	{
		decoding = true;
		Bitmap bitmap = null;
		var timer = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			await GameTask.RunInThreadAsync( () => { bitmap = Bitmap.CreateFromBytes( jpeg ); } );
		}
		catch ( Exception e )
		{
			Log.Trace( $"[bimp] mjpeg: {e.Message}" );
		}
		DecodeMilliseconds = timer.Elapsed.TotalMilliseconds;
		decoding = false;

		if ( disposed ) { bitmap?.Dispose(); return; }
		if ( bitmap is null || !bitmap.IsValid ) Failed++;
		else
		{
			ready.Enqueue( (time, bitmap) );
			while ( ready.Count > 4 ) { ready.Dequeue().bitmap.Dispose(); Dropped++; }
		}

		if ( waiting is { } next )
		{
			waiting = null;
			_ = Decode( next.time, next.jpeg );
		}
	}

	/// <summary> Call every frame: uploads the newest decoded frame that's due. </summary>
	public void Present( VideoFrameSink sink )
	{
		var now = RealTime.NowDouble - clockOffset - PlayoutDelay;
		(double time, Bitmap bitmap)? due = null;
		while ( ready.Count > 0 && ready.Peek().time <= now )
		{
			if ( due is { } skipped ) { skipped.bitmap.Dispose(); Dropped++; }
			due = ready.Dequeue();
		}
		if ( due is not { } frame ) return;

		sink.Upload( frame.bitmap );
		Size = $"{frame.bitmap.Width}x{frame.bitmap.Height}";
		ShownTime = frame.time;
		HasShown = true;
		frame.bitmap.Dispose();
	}

	public void Dispose()
	{
		disposed = true;
		while ( ready.Count > 0 ) ready.Dequeue().bitmap.Dispose();
	}
}

/// <summary>
/// An HTTP Motion JPEG stream (multipart/x-mixed-replace, as IP cameras and mjpg-streamer serve it): the JPEGs are
/// found by their start / end markers, so part headers and boundaries don't matter.
/// </summary>
public sealed class MjpegSplitter
{
	byte[] buffer = new byte[512 * 1024];
	int length;

	/// <summary> Feed received bytes; <paramref name="frame"/> gets each complete JPEG. </summary>
	public void Feed( ReadOnlySpan<byte> data, Action<byte[]> frame )
	{
		if ( length + data.Length > buffer.Length )
		{
			if ( length + data.Length > 16 * 1024 * 1024 ) length = 0; // no frame end in 16 MB: not JPEG
			else Array.Resize( ref buffer, Math.Max( buffer.Length * 2, length + data.Length ) );
		}
		data.CopyTo( buffer.AsSpan( length ) );
		length += data.Length;

		var consumed = 0;
		while ( true )
		{
			var span = buffer.AsSpan( consumed, length - consumed );
			var start = span.IndexOf( (ReadOnlySpan<byte>)new byte[] { 0xFF, 0xD8, 0xFF } );
			if ( start < 0 ) { consumed = Math.Max( consumed, length - 2 ); break; }
			var end = span[start..].IndexOf( (ReadOnlySpan<byte>)new byte[] { 0xFF, 0xD9 } );
			if ( end < 0 ) { consumed += start; break; }
			frame( span.Slice( start, end + 2 ).ToArray() );
			consumed += start + end + 2;
		}

		Buffer.BlockCopy( buffer, consumed, buffer, 0, length - consumed );
		length -= consumed;
	}
}
