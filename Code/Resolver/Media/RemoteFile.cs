using System.Globalization;
using System.Net.Http;
using System.Threading;

namespace Bimp.Resolver.Media;

public class FetchException : Exception
{
	public int StatusCode { get; }

	public FetchException( string message, int statusCode = 0 ) : base( message )
	{
		StatusCode = statusCode;
	}

	/// <summary> The server refused the url (expired, or tied to an old session) - worth re-resolving. </summary>
	public bool IsRefused => StatusCode is 403 or 410;
}

/// <summary>
/// A remote file read with byte range requests. Large ranges are split into chunks because googlevideo
/// throttles very large single ranges (and unranged downloads) to playback speed.
/// </summary>
public sealed class RemoteFile
{
	public const int ChunkSize = 8 * 1024 * 1024;
	const int Retries = 3;

	public string Url { get; private set; }
	public long Size { get; private set; }

	/// <summary>
	/// Called once when the server refuses the url (403/410), to get a fresh one. Null to not retry.
	/// </summary>
	public Func<CancellationToken, Task<string>> Refresh { get; set; }
	bool refreshed;

	/// <summary> Bytes downloaded so far (YouTube stops serving some urls after so many - see WebmSegmenter). </summary>
	public long BytesRead => System.Threading.Interlocked.Read( ref bytesRead );
	long bytesRead;

	public RemoteFile( string url, long size = 0 )
	{
		Url = url;
		Size = size;
	}

	/// <summary>
	/// Fill in <see cref="Size"/> (if not known) and return the first <paramref name="count"/> bytes.
	/// </summary>
	public async Task<byte[]> ProbeAsync( int count, CancellationToken ct )
	{
		var (data, total) = await RequestAsync( 0, count, ct );
		if ( total > 0 ) Size = total;
		if ( Size <= 0 ) throw new FetchException( "the server didn't report a file size" );
		return data;
	}

	/// <summary>
	/// Bytes [start, end) of the file.
	/// </summary>
	public async Task<byte[]> ReadAsync( long start, long end, CancellationToken ct )
	{
		if ( Size > 0 ) end = Math.Min( end, Size );
		if ( end <= start ) return Array.Empty<byte>();

		var result = new byte[end - start];
		var pos = start;
		while ( pos < end )
		{
			var want = (int)Math.Min( ChunkSize, end - pos );
			var (chunk, _) = await RequestAsync( pos, want, ct );
			if ( chunk.Length == 0 ) throw new FetchException( "the server returned no data" );
			var n = Math.Min( chunk.Length, (int)(end - pos) );
			Buffer.BlockCopy( chunk, 0, result, (int)(pos - start), n );
			System.Threading.Interlocked.Add( ref bytesRead, n );
			pos += n;
		}
		return result;
	}

	/// <summary>
	/// Download the whole file into <paramref name="output"/>, chunk by chunk. <paramref name="patch"/> may modify
	/// each chunk before it's written (given the chunk's offset in the file).
	/// </summary>
	public async Task CopyToAsync( System.IO.Stream output, Action<long, byte[]> patch, CancellationToken ct )
	{
		if ( Size <= 0 ) await ProbeAsync( 1, ct );

		for ( long pos = 0; pos < Size; )
		{
			var want = (int)Math.Min( ChunkSize, Size - pos );
			var (chunk, _) = await RequestAsync( pos, want, ct );
			if ( chunk.Length == 0 ) throw new FetchException( "the server returned no data" );
			patch?.Invoke( pos, chunk );
			await output.WriteAsync( chunk, ct );
			pos += chunk.Length;
		}
	}

	async Task<(byte[] data, long total)> RequestAsync( long start, int count, CancellationToken ct )
	{
		for ( int attempt = 0; ; attempt++ )
		{
			ct.ThrowIfCancellationRequested();

			int status;
			try
			{
				var headers = new Dictionary<string, string> { ["Range"] = $"bytes={start}-{start + count - 1}" };
				using var response = await Http.RequestAsync( Url, headers: headers, cancellationToken: ct );
				status = (int)response.StatusCode;

				if ( status is 200 or 206 )
				{
					var data = await response.Content.ReadAsByteArrayAsync( ct );
					var total = TotalSize( response, status );

					// A server that ignores Range sends the whole file
					if ( status == 200 && start > 0 )
					{
						if ( data.Length <= start ) throw new FetchException( "the server ignored the byte range" );
						data = data.AsSpan( (int)start, (int)Math.Min( count, data.Length - start ) ).ToArray();
					}
					else if ( data.Length > count )
					{
						data = data.AsSpan( 0, count ).ToArray();
					}

					return (data, total);
				}
			}
			catch ( OperationCanceledException ) when ( ct.IsCancellationRequested )
			{
				throw;
			}
			catch ( FetchException )
			{
				throw;
			}
			catch ( Exception e ) when ( attempt < Retries )
			{
				// connection dropped - try again
				Log.Trace( $"[bimp] range request failed ({e.Message}), retrying" );
				await Task.Delay( 250 * (attempt + 1), ct );
				continue;
			}

			if ( status is 403 or 410 && Refresh is not null && !refreshed )
			{
				refreshed = true;
				Url = await Refresh( ct );
				continue;
			}

			if ( status >= 500 && attempt < Retries )
			{
				await Task.Delay( 250 * (attempt + 1), ct );
				continue;
			}

			throw new FetchException( $"the server returned {status}", status );
		}
	}

	static long TotalSize( HttpResponseMessage response, int status )
	{
		var headers = response.Content.Headers;
		if ( status == 206 )
		{
			// "bytes 0-1023/123456"
			if ( headers.TryGetValues( "Content-Range", out var values ) )
			{
				var value = values.FirstOrDefault();
				var slash = value?.LastIndexOf( '/' ) ?? -1;
				if ( slash >= 0 && long.TryParse( value[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total ) )
					return total;
			}
			return 0;
		}

		return headers.ContentLength ?? 0;
	}
}
