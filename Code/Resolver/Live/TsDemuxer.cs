namespace Bimp.Resolver.Live;

/// <summary>
/// MPEG transport stream demuxer: finds the programme's H.264 video and AAC (ADTS) audio and hands access units
/// to a <see cref="LiveSegmenter"/>. Feed it bytes as they arrive, in any chunk sizes.
/// </summary>
public sealed class TsDemuxer
{
	const int PacketSize = 188;
	const int StreamH264 = 0x1B;
	const int StreamAacAdts = 0x0F;
	const int StreamHevc = 0x24;

	readonly LiveSegmenter sink;
	readonly byte[] carry = new byte[PacketSize];
	int carryLength;

	int pmtPid = -1;
	int videoPid = -1, audioPid = -1;
	readonly Dictionary<int, Pes> pes = new();

	/// <summary> Something about the stream we can't play, user facing (e.g. H.265 video). </summary>
	public string Problem { get; private set; }

	public bool FoundProgram => videoPid >= 0 || audioPid >= 0;

	// 33 bit timestamp unwrapping
	long lastRaw = -1, wraps;

	/// <summary>
	/// One stream's PES packet being assembled, in a reused buffer (not a List of bytes - a few megabits a
	/// second of per-byte appends and ToArray copies was needless main-thread work and garbage).
	/// </summary>
	sealed class Pes
	{
		public byte[] Buffer = new byte[256 * 1024];
		public int Length;
		public long Pts = -1, Dts = -1;
		public bool Started;

		public void Append( ReadOnlySpan<byte> data )
		{
			if ( Length + data.Length > Buffer.Length )
				Array.Resize( ref Buffer, Math.Max( Buffer.Length * 2, Length + data.Length ) );
			data.CopyTo( Buffer.AsSpan( Length ) );
			Length += data.Length;
		}
	}

	readonly bool takeVideo, takeAudio;

	/// <summary> <paramref name="video"/> / <paramref name="audio"/>: which of the programme's streams to use (HLS with separate audio). </summary>
	public TsDemuxer( LiveSegmenter sink, bool video = true, bool audio = true )
	{
		this.sink = sink;
		takeVideo = video;
		takeAudio = audio;
	}

	public void Feed( ReadOnlySpan<byte> data )
	{
		// finish a packet split across calls
		if ( carryLength > 0 )
		{
			var need = PacketSize - carryLength;
			if ( data.Length < need )
			{
				data.CopyTo( carry.AsSpan( carryLength ) );
				carryLength += data.Length;
				return;
			}
			data[..need].CopyTo( carry.AsSpan( carryLength ) );
			data = data[need..];
			carryLength = 0;
			if ( carry[0] == 0x47 ) Packet( carry );
		}

		var i = 0;
		while ( i + PacketSize <= data.Length )
		{
			if ( data[i] != 0x47 ) { i++; continue; } // resync
			Packet( data.Slice( i, PacketSize ) );
			i += PacketSize;
		}

		var rest = data[i..];
		if ( rest.Length > 0 )
		{
			rest.CopyTo( carry );
			carryLength = rest.Length;
		}
	}

	/// <summary>
	/// HLS packed audio: raw ADTS AAC after an ID3 tag whose PRIV frame (com.apple.streaming.transportStreamTimestamp)
	/// holds the 90 kHz timestamp of the first frame (YouTube live's audio playlists).
	/// </summary>
	public static bool IsPackedAudio( ReadOnlySpan<byte> d ) => d.Length > 10 && (d[0] == 'I' && d[1] == 'D' && d[2] == '3' || d[0] == 0xFF && (d[1] & 0xF6) == 0xF0);

	public void FeedPackedAudio( byte[] d )
	{
		if ( !takeAudio ) return;
		var at = 0;
		long pts = -1;
		while ( at + 10 <= d.Length && d[at] == 'I' && d[at + 1] == 'D' && d[at + 2] == '3' )
		{
			var size = (d[at + 6] << 21) | (d[at + 7] << 14) | (d[at + 8] << 7) | d[at + 9]; // syncsafe
			var end = Math.Min( d.Length, at + 10 + size );
			for ( var f = at + 10; f + 10 <= end; )
			{
				var id = System.Text.Encoding.ASCII.GetString( d, f, 4 );
				var frameSize = (d[f + 4] << 24) | (d[f + 5] << 16) | (d[f + 6] << 8) | d[f + 7];
				if ( frameSize <= 0 || f + 10 + frameSize > end ) break;
				if ( id == "PRIV" )
				{
					const string Owner = "com.apple.streaming.transportStreamTimestamp";
					var body = d.AsSpan( f + 10, frameSize );
					if ( body.Length >= Owner.Length + 9 && System.Text.Encoding.ASCII.GetString( body[..Owner.Length] ) == Owner )
					{
						var t = body[(Owner.Length + 1)..];
						long raw = 0;
						for ( int i = 0; i < 8; i++ ) raw = (raw << 8) | t[i];
						pts = Unwrap( raw & 0x1FFFFFFFFL );
					}
				}
				f += 10 + frameSize;
			}
			at = end;
		}
		if ( pts < 0 ) return;
		EmitAdts( d.AsSpan( at ).ToArray(), d.Length - at, pts );
	}

	/// <summary> End of input: emit what's buffered. </summary>
	public void Flush()
	{
		foreach ( var (pid, p) in pes ) Emit( pid, p );
	}

	void Packet( ReadOnlySpan<byte> p )
	{
		var start = (p[1] & 0x40) != 0;
		var pid = ((p[1] & 0x1F) << 8) | p[2];
		var adaptation = (p[3] >> 4) & 3;
		var offset = 4;

		if ( adaptation is 2 or 3 ) offset += 1 + p[4];
		if ( adaptation is 0 or 2 || offset >= PacketSize ) return;
		var payload = p[offset..];

		if ( pid == 0 ) { if ( start ) ParsePat( payload ); return; }
		if ( pid == pmtPid ) { if ( start ) ParsePmt( payload ); return; }
		if ( pid != videoPid && pid != audioPid ) return;

		if ( !pes.TryGetValue( pid, out var stream ) ) pes[pid] = stream = new Pes();

		if ( start )
		{
			// a new PES packet: the previous one is complete
			if ( stream.Started ) Emit( pid, stream );
			if ( !ParsePesHeader( payload, stream, out var headerLength ) ) { stream.Started = false; return; }
			stream.Started = true;
			stream.Append( payload[headerLength..] );
		}
		else if ( stream.Started )
		{
			stream.Append( payload );
		}
	}

	void ParsePat( ReadOnlySpan<byte> d )
	{
		var at = 1 + d[0]; // pointer field
		if ( at + 8 > d.Length || d[at] != 0x00 ) return;
		var sectionLength = ((d[at + 1] & 0x0F) << 8) | d[at + 2];
		var end = Math.Min( at + 3 + sectionLength - 4, d.Length );
		for ( int i = at + 8; i + 4 <= end; i += 4 )
		{
			var program = (d[i] << 8) | d[i + 1];
			var pid = ((d[i + 2] & 0x1F) << 8) | d[i + 3];
			if ( program != 0 ) { pmtPid = pid; return; } // first programme
		}
	}

	void ParsePmt( ReadOnlySpan<byte> d )
	{
		var at = 1 + d[0];
		if ( at + 12 > d.Length || d[at] != 0x02 ) return;
		var sectionLength = ((d[at + 1] & 0x0F) << 8) | d[at + 2];
		var end = Math.Min( at + 3 + sectionLength - 4, d.Length );
		var infoLength = ((d[at + 10] & 0x0F) << 8) | d[at + 11];
		var i = at + 12 + infoLength;

		while ( i + 5 <= end )
		{
			var type = d[i];
			var pid = ((d[i + 1] & 0x1F) << 8) | d[i + 2];
			var esInfo = ((d[i + 3] & 0x0F) << 8) | d[i + 4];

			if ( type == StreamH264 && videoPid < 0 && takeVideo ) { videoPid = pid; sink.ExpectVideo(); }
			else if ( type == StreamAacAdts && audioPid < 0 && takeAudio ) audioPid = pid;
			else if ( type == StreamHevc && videoPid < 0 && takeVideo ) Problem ??= "This stream's video is H.265 (HEVC), which the engine can't decode - only H.264 (or AV1 over RTSP / fMP4 HLS).";

			i += 5 + esInfo;
		}
	}

	bool ParsePesHeader( ReadOnlySpan<byte> d, Pes stream, out int headerLength )
	{
		headerLength = 0;
		if ( d.Length < 9 || d[0] != 0 || d[1] != 0 || d[2] != 1 ) return false;
		var flags = d[7];
		headerLength = 9 + d[8];
		if ( headerLength > d.Length ) return false;

		stream.Pts = (flags & 0x80) != 0 ? Unwrap( Timestamp( d, 9 ) ) : -1;
		stream.Dts = (flags & 0x40) != 0 ? Unwrap( Timestamp( d, 14 ) ) : stream.Pts;
		return true;
	}

	static long Timestamp( ReadOnlySpan<byte> d, int at )
	{
		return ((long)(d[at] & 0x0E) << 29) | ((long)d[at + 1] << 22) | ((long)(d[at + 2] & 0xFE) << 14)
			| ((long)d[at + 3] << 7) | ((long)d[at + 4] >> 1);
	}

	long Unwrap( long raw )
	{
		const long Span = 1L << 33;
		if ( lastRaw >= 0 && raw < lastRaw - Span / 2 ) wraps++;
		else if ( lastRaw >= 0 && raw > lastRaw + Span / 2 && wraps > 0 ) wraps--;
		lastRaw = raw;
		return raw + wraps * Span;
	}

	void Emit( int pid, Pes stream )
	{
		if ( stream.Length == 0 ) return;
		var data = stream.Buffer;
		var length = stream.Length;
		stream.Length = 0;

		if ( pid == videoPid )
		{
			if ( stream.Dts < 0 ) return;
			sink.AddVideo( stream.Dts, stream.Pts < 0 ? stream.Dts : stream.Pts, data, length );
		}
		else if ( pid == audioPid )
		{
			EmitAdts( data, length, stream.Pts );
		}
	}

	/// <summary> A PES packet of AAC holds one or more ADTS frames, each 1024 samples. </summary>
	void EmitAdts( byte[] d, int dataLength, long pts )
	{
		if ( pts < 0 ) return;
		var i = 0;
		var frame = 0;
		while ( i + 7 <= dataLength )
		{
			if ( d[i] != 0xFF || (d[i + 1] & 0xF6) != 0xF0 ) { i++; continue; }

			var protectionAbsent = (d[i + 1] & 1) == 1;
			var profile = (d[i + 2] >> 6) & 3;
			var rateIndex = (d[i + 2] >> 2) & 0xF;
			var channelConfig = ((d[i + 2] & 1) << 2) | (d[i + 3] >> 6);
			var length = ((d[i + 3] & 3) << 11) | (d[i + 4] << 3) | (d[i + 5] >> 5);
			var header = protectionAbsent ? 7 : 9;
			if ( length < header || i + length > dataLength ) break;

			var config = AacConfig.FromAdts( profile, rateIndex, channelConfig );
			if ( config.SampleRate == 0 ) break;
			sink.SetAacConfig( config );

			var framePts = pts + (long)frame * 1024 * 90000 / config.SampleRate;
			sink.AddAudio( framePts, d.AsSpan( i + header, length - header ).ToArray() );

			frame++;
			i += length;
		}
	}
}
