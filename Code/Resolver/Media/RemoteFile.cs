using System.Buffers;
using System.Globalization;
using System.Net.Http;
using System.Threading;

namespace Bimp.Resolver.Media;

/// <summary>
/// A downloaded range held in a buffer rented from the shared array pool (a 44 s segment's video is 50-66 MB: a fresh
/// array of that size per segment is all large-object-heap garbage). <b>Dispose it</b> to give the buffer back - nothing
/// may touch <see cref="Data"/> afterwards, so whoever reads it (the mux, on a worker thread) must be finished first.
/// <para>
/// The pool may hand out a larger array than asked for: only <see cref="Length"/> bytes of <see cref="Data"/> are the
/// range - never use <c>Data.Length</c>.
/// </para>
/// </summary>
public sealed class PooledRange : IDisposable
{
	/// <summary> How many bytes past the range are filled with 0xFF (see the constructor). </summary>
	const int TailGuard = 16;

	/// <summary> The range is the first <see cref="Length"/> bytes; the rest is whatever the buffer held before. </summary>
	public byte[] Data { get; private set; }

	/// <summary> The size of the range. </summary>
	public int Length { get; }

	bool pooled;

	internal PooledRange( int length )
	{
		Length = length;
		if ( length <= 0 )
		{
			Data = Array.Empty<byte>();
			return;
		}

		Data = ArrayPool<byte>.Shared.Rent( length );
		pooled = true;

		// The parsers stop quietly at an element header that the buffer cuts off, but an oversized buffer would hand them
		// the stale bytes after the range instead (zeros or an old segment) - 0xFF decodes as an unknown-size element
		// that ends the walk, the same as a cut off one.
		var guard = Math.Min( TailGuard, Data.Length - length );
		for ( int i = 0; i < guard; i++ ) Data[length + i] = 0xFF;
	}

	public void Dispose()
	{
		if ( !pooled ) return;
		pooled = false;

		var data = Data;
		Data = Array.Empty<byte>();
		ArrayPool<byte>.Shared.Return( data );
	}
}

public class FetchException : Exception
{
	public int StatusCode { get; }

	public FetchException( string message, int statusCode = 0 ) : base( message )
	{
		StatusCode = statusCode;
	}

	/// <summary> The server refused the url (expired, or tied to an old session) - worth re-resolving. </summary>
	public bool IsRefused => StatusCode is 403 or 410;

	/// <summary> The resolver client that minted the refused file's url (see <see cref="RemoteFile.ClientKey"/>), so a re-resolve can skip it. </summary>
	public string ClientKey { get; init; }
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
	/// Called when the server refuses the url (403/410), to get a fresh one. Null to not retry. Rate limited
	/// (see <see cref="RefreshCooldown"/> and <see cref="MaxRefreshes"/>) so a url that keeps dying can't spin here.
	/// The bool says whether the server refused the url (true: the minting client is suspect) or it's only about to
	/// expire (false: the same client's fresh url is fine).
	/// </summary>
	public Func<bool, CancellationToken, Task<string>> Refresh { get; set; }

	/// <summary> Another refresh no earlier than this long after the last one - a fresh url that 403s again isn't a second refusal, it's the same one landing. </summary>
	const float RefreshCooldown = 10f;

	/// <summary> A refresh continue doesn't count as a retry attempt, so this caps it: a client serving fresh-but-dying urls can't loop here. </summary>
	const int MaxRefreshes = 4;

	RealTimeSince sinceRefresh;
	int refreshes;

	/// <summary> [probe] bimp_probe_refuse: answer 403 without touching the network, to exercise the refresh and the session re-resolve. </summary>
	bool poisoned;
	public void PoisonForProbe() => poisoned = true;

	/// <summary> The resolver client that minted <see cref="Url"/> (null off YouTube) - carried into the <see cref="FetchException"/> of a refusal. </summary>
	public string ClientKey { get; set; }

	/// <summary>
	/// Bytes downloaded from the current url, probes included (YouTube stops serving some urls after so many - see
	/// WebmSegmenter). Lives with this file, so it carries across the segmenters and reloads that share a cached session,
	/// and starts over when the url is replaced.
	/// </summary>
	public long BytesRead => System.Threading.Interlocked.Read( ref bytesRead );
	long bytesRead;

	/// <summary> A url this close to its <c>expire=</c> time is replaced before it's used (seconds). </summary>
	public const long ExpiryMargin = 60;

	/// <summary> When the current url stops working (unix seconds), from its <c>expire=</c> parameter; null if it doesn't say. </summary>
	public long? ExpiresAt => ExpiryOf( Url );

	/// <summary> The <c>expire=</c> (unix seconds) of a stream url, or null. </summary>
	public static long? ExpiryOf( string url )
	{
		if ( string.IsNullOrEmpty( url ) ) return null;

		var q = url.IndexOf( '?' );
		if ( q < 0 ) return null;

		foreach ( var pair in url[(q + 1)..].Split( '&' ) )
		{
			if ( pair.StartsWith( "expire=", StringComparison.Ordinal )
				&& long.TryParse( pair.AsSpan( 7 ), NumberStyles.Integer, CultureInfo.InvariantCulture, out var at ) )
				return at;
		}
		return null;
	}

	/// <summary> The url is about to expire (or has) according to its own <c>expire=</c>. </summary>
	bool ExpiringSoon( long margin = ExpiryMargin ) => ExpiresAt is { } at && at - DateTimeOffset.UtcNow.ToUnixTimeSeconds() < margin;

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
		System.Threading.Interlocked.Add( ref bytesRead, data.Length );
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
	/// Bytes [start, end) of the file, in a pooled buffer instead of a new array - and each chunk goes from the response
	/// straight into it, with no 8 MiB array of its own. The caller owns the result and must dispose it (on this method's
	/// failure it already has).
	/// </summary>
	public async Task<PooledRange> ReadPooledAsync( long start, long end, CancellationToken ct )
	{
		if ( Size > 0 ) end = Math.Min( end, Size );
		if ( end <= start ) return new PooledRange( 0 );

		var range = new PooledRange( checked((int)(end - start)) );
		try
		{
			var pos = start;
			while ( pos < end )
			{
				var want = (int)Math.Min( ChunkSize, end - pos );
				var (_, _, n) = await RequestAsync( pos, want, range.Data, (int)(pos - start), ct );
				if ( n == 0 ) throw new FetchException( "the server returned no data" );
				System.Threading.Interlocked.Add( ref bytesRead, n );
				pos += n;
			}
			return range;
		}
		catch
		{
			range.Dispose();
			throw;
		}
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
			System.Threading.Interlocked.Add( ref bytesRead, chunk.Length );
			pos += chunk.Length;
		}
	}

	async Task<(byte[] data, long total)> RequestAsync( long start, int count, CancellationToken ct )
	{
		var (data, total, _) = await RequestAsync( start, count, null, 0, ct );
		return (data, total);
	}

	/// <summary>
	/// One range request, with the retry and refresh handling. Without <paramref name="into"/> the bytes come back as a new
	/// array. With it they are written to into[intoOffset..intoOffset + count) (data is null, copied says how many) - read
	/// from the response as it arrives, nothing buffered whole in between.
	/// </summary>
	async Task<(byte[] data, long total, int copied)> RequestAsync( long start, int count, byte[] into, int intoOffset, CancellationToken ct )
	{
		// into: a response that doesn't fit the window (see below) is read the buffered way, which can trim it
		var buffered = into is null;

		for ( int attempt = 0; ; attempt++ )
		{
			ct.ThrowIfCancellationRequested();

			// the url says when it dies: swap it before that, rather than after the 403 (same limits as a refusal's refresh)
			if ( attempt == 0 && !poisoned && CanRefresh && ExpiringSoon() )
			{
				refreshes++;
				sinceRefresh = 0;
				try
				{
					var fresh = await Refresh( false, ct );
					if ( !string.IsNullOrEmpty( fresh ) && fresh != Url )
					{
						Log.Info( "[bimp] url about to expire, carrying on with a fresh one" );
						ReplaceUrl( fresh );
					}
				}
				catch ( Exception e ) when ( e is not OperationCanceledException )
				{
					// the request below is still worth a try - a refusal refreshes again
					Log.Trace( $"[bimp] couldn't refresh the expiring url: {e.Message}" );
				}
			}

			int status;
			try
			{
				// no User-Agent here: the engine rejects setting it (measured 2026-10-02), so media GETs can't
				// claim the minting client's identity - a refusal recovers by re-resolving past that client
				var headers = new Dictionary<string, string> { ["Range"] = $"bytes={start}-{start + count - 1}" };
				var requestStart = Trace.On ? Trace.Now : 0;
				Trace.Mark( TraceId.DownloadBegin, count );

				// [probe] bimp_probe_refuse: answer 403 without asking the server, so the refresh and the
				// session re-resolve can be exercised on demand
				if ( poisoned ) status = 403;
				else if ( !buffered )
				{
					// the body is copied from the response into the caller's buffer as it arrives. An error status throws, so
					// the window is exactly the bytes asked for: a body that outgrows it is a server that ignored the range
					// (it sent the whole file) or over-delivered
					using var body = await Http.RequestStreamAsync( Url, headers: headers, cancellationToken: ct );
					using var window = new System.IO.MemoryStream( into, intoOffset, count, true );
					try
					{
						await body.CopyToAsync( window, 81920, ct );
					}
					catch ( NotSupportedException )
					{
						Log.Trace( "[bimp] the server sent more than the byte range, trimming it" );
						buffered = true;
						attempt--; // not a failed attempt
						continue;
					}

					if ( Trace.On ) Trace.Mark( TraceId.DownloadEnd, (int)window.Position, Trace.Now - requestStart );
					return (null, 0, (int)window.Position);
				}
				else
				{
					using var response = await Http.RequestAsync( Url, headers: headers, cancellationToken: ct );
					status = (int)response.StatusCode;

					if ( status is 200 or 206 )
					{
						var data = await response.Content.ReadAsByteArrayAsync( ct );
						var total = TotalSize( response, status );
						if ( Trace.On ) Trace.Mark( TraceId.DownloadEnd, data.Length, Trace.Now - requestStart );

						// A server that ignores Range sends the whole file
						var offset = 0;
						var length = data.Length;
						if ( status == 200 && start > 0 )
						{
							if ( data.Length <= start ) throw new FetchException( "the server ignored the byte range" );
							offset = (int)start;
							length = (int)Math.Min( count, data.Length - start );
						}
						else if ( length > count )
						{
							length = count;
						}

						if ( into is not null )
						{
							Buffer.BlockCopy( data, offset, into, intoOffset, length );
							return (null, total, length);
						}

						if ( length != data.Length ) data = data.AsSpan( offset, length ).ToArray();
						return (data, total, length);
					}
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
			catch ( HttpRequestException e ) when ( e.StatusCode is not null )
			{
				// the streamed request throws on an error status: carry on with it like any other response's
				status = (int)e.StatusCode.Value;
			}
			catch ( Exception e ) when ( attempt < Retries )
			{
				// connection dropped - try again
				Log.Trace( $"[bimp] range request failed ({e.Message}), retrying" );
				await Task.Delay( 250 * (attempt + 1), ct );
				continue;
			}

			if ( status is 403 or 410 && CanRefresh )
			{
				refreshes++;
				sinceRefresh = 0;
				var fresh = await Refresh( true, ct );

				// null (the format is gone from the fresh resolve) or the same url again: retrying it is pointless -
				// fail so the segmenter records the refusal and the session gets re-resolved from another client
				if ( string.IsNullOrEmpty( fresh ) || fresh == Url )
					throw new FetchException( $"the server returned {status}", status ) { ClientKey = ClientKey };

				Log.Info( $"[bimp] url refused ({status}), carrying on with a fresh one" );
				ReplaceUrl( fresh );
				continue;
			}

			if ( status >= 500 && attempt < Retries )
			{
				await Task.Delay( 250 * (attempt + 1), ct );
				continue;
			}

			throw new FetchException( $"the server returned {status}", status ) { ClientKey = ClientKey };
		}
	}

	bool CanRefresh => Refresh is not null && refreshes < MaxRefreshes && sinceRefresh >= RefreshCooldown;

	/// <summary> A fresh url is a fresh allowance: what was downloaded from the old one no longer counts (see <see cref="BytesRead"/>). </summary>
	void ReplaceUrl( string fresh )
	{
		Url = fresh;
		System.Threading.Interlocked.Exchange( ref bytesRead, 0 );
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
