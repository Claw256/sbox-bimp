using System.Buffers.Binary;
using System.Threading;

namespace Bimp.Resolver.Media;

/// <summary>
/// Works around the engine playing mono AAC in MP4 as crackling noise.
/// <para>
/// ROOT CAUSE (measured with sine test files through the engine's own recorder): the engine's VideoPlayer takes
/// the channel count from the MP4 <c>mp4a</c> sample entry, while its (Media Foundation) AAC decoder outputs what
/// the AAC stream really is (the AudioSpecificConfig). Lots of encoders write a placeholder "2" in the sample
/// entry for mono audio - standard decoders ignore it - so the engine opens a stereo stream and feeds it mono
/// samples: constant crackle. A clean mono tone turns to noise by changing only that 2 byte field from 1 to 2.
/// </para>
/// <para>
/// Fix: read the file's moov with range requests; if a sample entry disagrees with its AudioSpecificConfig,
/// download the file with those 2 bytes corrected and play it locally. Files that are fine stream as before.
/// </para>
/// </summary>
public static class Mp4ChannelFix
{
	/// <summary> Bigger files stream as they are (with the crackle) rather than download before playing. </summary>
	public const long MaxDownloadBytes = 400L * 1024 * 1024;

	static readonly string[] Extensions = { "mp4", "m4v", "m4a", "mov" };

	public static bool Applies( string url )
	{
		if ( !Uri.TryCreate( url, UriKind.Absolute, out var uri ) ) return false;
		var ext = System.IO.Path.GetExtension( uri.AbsolutePath ).TrimStart( '.' ).ToLowerInvariant();
		return Extensions.Contains( ext );
	}

	/// <summary>
	/// Check an MP4 url. Returns a <see cref="FileSystem.Data"/> path to a corrected local copy, or null if the
	/// url is fine to play as it is (or can't / shouldn't be fixed).
	/// </summary>
	public static async Task<string> PrepareAsync( string url, string directory, CancellationToken ct )
	{
		var file = new RemoteFile( url );
		List<long> patches;
		try
		{
			patches = await FindMismatches( file, ct );
		}
		catch ( Exception e ) when ( e is not OperationCanceledException )
		{
			// not something we can read (no range support, odd layout...) - let the engine have it
			Log.Trace( $"[bimp] mp4 check skipped: {e.Message}" );
			return null;
		}

		if ( patches.Count == 0 ) return null;

		var name = Uri.TryCreate( url, UriKind.Absolute, out var u ) ? $"{u.Host}{u.AbsolutePath}" : url;

		if ( file.Size > MaxDownloadBytes )
		{
			Log.Warning( $"[bimp] {name} has mono audio labelled as stereo (it will crackle), but it's too big ({file.Size / 1048576} MB) to fix by downloading" );
			return null;
		}

		Log.Info( $"[bimp] {name}: mono audio labelled as stereo - downloading a corrected copy ({file.Size / 1048576.0:0.0} MB)" );
		FileSystem.Data.CreateDirectory( directory );
		var path = $"{directory}/media{System.IO.Path.GetExtension( new Uri( url ).AbsolutePath ).ToLowerInvariant()}";

		using ( var output = FileSystem.Data.OpenWrite( path ) )
		{
			await file.CopyToAsync( output, ( offset, chunk ) =>
			{
				// each patch is a big endian u16 channel count at an absolute offset: write 1
				foreach ( var p in patches )
				{
					for ( int i = 0; i < 2; i++ )
					{
						var at = p + i - offset;
						if ( at >= 0 && at < chunk.Length ) chunk[at] = (byte)(i == 0 ? 0 : 1);
					}
				}
			}, ct );
		}

		return path;
	}

	/// <summary>
	/// Absolute offsets of mp4a channel count fields that claim more channels than the AAC stream has.
	/// </summary>
	static async Task<List<long>> FindMismatches( RemoteFile file, CancellationToken ct )
	{
		var head = await file.ProbeAsync( 64 * 1024, ct );
		var result = new List<long>();

		// walk the top level boxes to the moov (it may be after a huge mdat)
		long off = 0;
		while ( off + 8 <= file.Size )
		{
			var hdr = off + 16 <= head.Length ? head.AsSpan( (int)off, 16 ).ToArray() : await file.ReadAsync( off, Math.Min( off + 16, file.Size ), ct );
			var (size, type, hlen) = BoxHeader( hdr, 0, file.Size - off );
			if ( size < 8 ) break;

			if ( type == "moov" )
			{
				if ( size > 64 * 1024 * 1024 ) break; // not a sane moov
				var moov = off + size <= head.Length ? head.AsSpan( (int)off, (int)size ).ToArray() : await file.ReadAsync( off, off + size, ct );
				Scan( moov, hlen, moov.Length, off, result );
				break;
			}

			off += size;
		}

		return result;
	}

	static (long size, string type, int headerLength) BoxHeader( byte[] d, int at, long remaining )
	{
		if ( at + 8 > d.Length ) return (0, null, 0);
		long size = BinaryPrimitives.ReadUInt32BigEndian( d.AsSpan( at ) );
		var type = System.Text.Encoding.ASCII.GetString( d, at + 4, 4 );
		var hlen = 8;
		if ( size == 1 )
		{
			if ( at + 16 > d.Length ) return (0, null, 0);
			size = (long)BinaryPrimitives.ReadUInt64BigEndian( d.AsSpan( at + 8 ) );
			hlen = 16;
		}
		else if ( size == 0 )
		{
			size = remaining;
		}
		return (size, type, hlen);
	}

	static readonly HashSet<string> Containers = new() { "moov", "trak", "mdia", "minf", "stbl" };

	/// <summary> Walk moov children; for each mp4a compare the sample entry's channel count with its esds. </summary>
	static void Scan( byte[] d, int start, int end, long baseOffset, List<long> result )
	{
		var at = start;
		while ( at + 8 <= end )
		{
			var (size, type, hlen) = BoxHeader( d, at, end - at );
			if ( size < 8 || at + size > end ) break;

			if ( Containers.Contains( type ) )
			{
				Scan( d, at + hlen, (int)(at + size), baseOffset, result );
			}
			else if ( type == "stsd" )
			{
				// full box (4) + entry count (4), then sample entries
				Scan( d, at + hlen + 8, (int)(at + size), baseOffset, result );
			}
			else if ( type == "mp4a" )
			{
				CheckMp4a( d, at, hlen, (int)size, baseOffset, result );
			}

			at += (int)size;
		}
	}

	static void CheckMp4a( byte[] d, int at, int hlen, int size, long baseOffset, List<long> result )
	{
		// AudioSampleEntry: reserved(6) data_reference_index(2) version(2) revision(2) vendor(4)
		//                   channelcount(2) samplesize(2) ...(4) samplerate(4), then child boxes (esds)
		var body = at + hlen;
		var version = BinaryPrimitives.ReadUInt16BigEndian( d.AsSpan( body + 8 ) );
		var channelPos = body + 16;
		var declared = BinaryPrimitives.ReadUInt16BigEndian( d.AsSpan( channelPos ) );

		// QuickTime sound description v1/v2 carry more fields before the children
		var children = body + 28 + version switch { 1 => 16, 2 => 36, _ => 0 };

		var esds = FindChild( d, children, at + size, "esds" );
		if ( esds < 0 ) return;

		var asc = AudioSpecificConfig( d, esds, at + size );
		if ( asc is null ) return;

		var (objectType, channelConfig) = asc.Value;

		// Only plain AAC: with SBR/PS (HE-AAC) a mono config can legitimately decode to stereo
		if ( objectType is 5 or 29 ) return;
		if ( channelConfig != 1 ) return;

		if ( declared != 1 )
			result.Add( baseOffset + channelPos );
	}

	static int FindChild( byte[] d, int start, int end, string wanted )
	{
		var at = start;
		while ( at + 8 <= end )
		{
			var (size, type, _) = BoxHeader( d, at, end - at );
			if ( size < 8 ) return -1;
			if ( type == wanted ) return at;
			at += (int)size;
		}
		return -1;
	}

	/// <summary>
	/// (audio object type, channel configuration) from an esds box's DecoderSpecificInfo.
	/// </summary>
	static (int objectType, int channelConfig)? AudioSpecificConfig( byte[] d, int esds, int limit )
	{
		var (size, _, hlen) = BoxHeader( d, esds, limit - esds );
		var end = (int)Math.Min( esds + size, limit );
		var at = esds + hlen + 4; // full box header

		// descriptors: ES_Descriptor(3) > DecoderConfigDescriptor(4) > DecoderSpecificInfo(5)
		while ( at < end )
		{
			var tag = d[at++];
			var len = 0;
			for ( int i = 0; i < 4 && at < end; i++ )
			{
				var b = d[at++];
				len = (len << 7) | (b & 0x7F);
				if ( (b & 0x80) == 0 ) break;
			}

			switch ( tag )
			{
				case 3:
					// ES_ID(2) flags(1) [+ optional fields]
					var flags = d[at + 2];
					at += 3;
					if ( (flags & 0x80) != 0 ) at += 2;
					if ( (flags & 0x40) != 0 ) at += 1 + d[at];
					if ( (flags & 0x20) != 0 ) at += 2;
					break;
				case 4:
					at += 13; // objectTypeIndication, streamType, bufferSize, max/avg bitrate
					break;
				case 5:
					if ( len < 2 || at + 2 > end ) return null;
					var v = (d[at] << 8) | d[at + 1];
					var objectType = v >> 11;
					var freqIndex = (v >> 7) & 0xF;
					if ( objectType == 31 || freqIndex == 15 ) return null; // escaped forms - leave alone
					return (objectType, (v >> 3) & 0xF);
				default:
					at += len;
					break;
			}
		}

		return null;
	}
}
