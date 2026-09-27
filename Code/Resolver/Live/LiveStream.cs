using System.Threading;

namespace Bimp.Resolver.Live;

/// <summary>
/// One client's connection to a live source (MPEG-TS over HTTP, HLS, RTSP tunnelled over HTTP), remuxed into
/// segment files by a <see cref="LiveSegmenter"/>. Every client connects on its own.
/// </summary>
public sealed class LiveStream : IDisposable
{
	static readonly string[] RtspSchemes = { "rtsp", "rtsps", "rtspt" };
	static readonly string[] TsExtensions = { "ts", "m2ts", "mts" };
	static readonly string[] HlsExtensions = { "m3u8", "m3u" };
	static readonly string[] MjpegExtensions = { "mjpg", "mjpeg" };

	/// <summary> Don't get further than this many segments ahead of playback (finite sources download faster than real time). </summary>
	int MaxAhead => Segmenter.LowLatency ? 12 : 4;

	readonly CancellationTokenSource cts = new();
	readonly string url;

	public LiveSegmenter Segmenter { get; }

	/// <summary> The segment being played, so ingest can wait instead of racing ahead. </summary>
	public int PlayingSeq { get; set; } = -1;

	string error;
	public string Error => error ?? Segmenter.Error;

	/// <summary> What we connected to, for diagnostics ("RTSP H.264 1920x1080 + PCMU"). </summary>
	public string Description
	{
		get => $"{description} {Segmenter.VideoInfo} | {(Segmenter.Mjpeg is { } mjpeg ? $"frame by frame, {mjpeg.Dropped} dropped, decode {mjpeg.DecodeMilliseconds:0}ms" : $"{(Segmenter.LowLatency ? "low latency" : "normal latency")}, keyframes every {Segmenter.KeyframeInterval:0.0}s")} | {BytesReceived / 1024}KB in, video {Segmenter.VideoFrames} (skipped {Segmenter.DroppedBeforeKeyframe}), audio {Segmenter.AudioFrames}, newest seg {Segmenter.Newest}, playing {PlayingSeq}, last write {Segmenter.WriteMilliseconds:0}ms";
		private set => description = value;
	}
	string description;

	public long BytesReceived { get; set; }

	public static bool IsRtsp( Uri uri ) => RtspSchemes.Contains( uri.Scheme.ToLowerInvariant() );

	/// <summary>
	/// An HTTP Motion JPEG camera stream: .mjpg / .mjpeg, or the usual camera and mjpg-streamer paths
	/// (/mjpg/video.mjpg, /video.cgi?..mjpeg.., ?action=stream).
	/// </summary>
	public static bool IsMjpeg( Uri uri )
	{
		if ( uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps ) return false;
		var ext = System.IO.Path.GetExtension( uri.AbsolutePath ).TrimStart( '.' ).ToLowerInvariant();
		var all = uri.PathAndQuery.ToLowerInvariant();
		return MjpegExtensions.Contains( ext ) || all.Contains( "mjpg" ) || all.Contains( "mjpeg" ) || all.Contains( "action=stream" );
	}

	/// <summary> A url we play as a live stream. </summary>
	public static bool IsLiveUrl( Uri uri )
	{
		if ( IsRtsp( uri ) || IsMjpeg( uri ) ) return true;
		if ( uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps ) return false;
		var ext = System.IO.Path.GetExtension( uri.AbsolutePath ).TrimStart( '.' ).ToLowerInvariant();
		return TsExtensions.Contains( ext ) || HlsExtensions.Contains( ext );
	}

	/// <summary> VOD (a finished HLS playlist): the media time to start at, and how far behind it the start may be. </summary>
	readonly double? vodStart;
	readonly double vodMaxLag;

	/// <summary>
	/// <paramref name="hls"/>: the url is an HLS playlist whatever it looks like (from an extractor).
	/// <paramref name="vodStart"/>: a finished HLS playlist, played from that media time (see <see cref="HlsReader"/>).
	/// </summary>
	public LiveStream( string url, string directory, int maxHeight, bool hls = false, double? vodStart = null, double vodMaxLag = double.MaxValue )
	{
		this.url = url;
		var uri = new Uri( url );
		this.hls = hls || vodStart is not null || HlsExtensions.Contains( System.IO.Path.GetExtension( uri.AbsolutePath ).TrimStart( '.' ).ToLowerInvariant() );
		this.vodStart = vodStart;
		this.vodMaxLag = vodMaxLag;
		// HLS arrives a whole segment at a time, so it gains nothing from low latency
		Segmenter = new LiveSegmenter( directory, MediaSettings.LowLatencyLive && !this.hls, vod: vodStart is not null );
		_ = Run( maxHeight );
	}

	readonly bool hls;

	async Task Run( int maxHeight )
	{
		var ct = cts.Token;
		try
		{
			var uri = new Uri( url );
			var ext = System.IO.Path.GetExtension( uri.AbsolutePath ).TrimStart( '.' ).ToLowerInvariant();

			if ( IsRtsp( uri ) )
			{
				Description = "RTSP";
				await RunRtsp( uri, ct );
			}
			else if ( IsMjpeg( uri ) )
			{
				Description = "HTTP MJPEG";
				await RunMjpeg( uri, ct );
			}
			else if ( hls )
			{
				Description = vodStart is null ? "HLS" : "HLS VOD";
				await new HlsReader( uri, Segmenter, maxHeight, WaitForPlayback, vodStart ?? 0, vodMaxLag ).Run( ct );
			}
			else
			{
				Description = "MPEG-TS";
				await RunTs( uri, ct );
			}
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception ) when ( cts.IsCancellationRequested ) { } // closed: the tunnel dropping as it goes isn't news
		catch ( Exception e )
		{
			error = e switch
			{
				InvalidOperationException when e.Message.Contains( "not allowed" ) || e.Message.Contains( "Access to" ) =>
					"This address isn't allowed: s&box blocks raw IP addresses and private networks (a dedicated server started with -allowlocalhttp can reach them).",
				ResolveException => e.Message,
				_ => $"Live stream failed: {e.Message}",
			};
			Log.Warning( $"[bimp] live {Redact( url )}: {e.Message}" );
		}
	}

	/// <summary>
	/// RTSP through the HTTP tunnel. Reconnects when the tunnel drops or a request goes unanswered - load-balanced
	/// servers (VRCDN) now and then lose one - but not when the camera says no (auth, missing stream).
	/// </summary>
	async Task RunRtsp( Uri uri, CancellationToken ct )
	{
		const int MaxFailures = 5;
		var failures = 0;
		bool? longPost = null; // which way of tunnelling this server answers, once one has worked
		while ( true )
		{
			var tunnel = new RtspTunnel( uri, Segmenter, longPost ?? failures % 2 == 0 );
			var started = RealTime.NowDouble;
			string lost;
			try
			{
				await tunnel.Run( ct, d => Description = d, n => BytesReceived += n );
				return;
			}
			catch ( Exception e ) when ( !ct.IsCancellationRequested && (RealTime.NowDouble - started > 30 || failures < MaxFailures)
				&& (e is ResolveException && tunnel.Retryable || e is System.IO.IOException || e is System.Net.Http.HttpRequestException { StatusCode: null }) )
			{
				// an unanswered request, or the connection dropped
				lost = e.Message;
			}

			if ( tunnel.Played ) longPost ??= tunnel.LongPost;

			// a session that ran a while and then dropped starts the count again
			if ( RealTime.NowDouble - started > 30 ) failures = 0;
			failures++;
			Log.Info( $"[bimp] rtsp: {lost} - reconnecting ({failures}/{MaxFailures}, {((longPost ?? failures % 2 == 0) ? "one long POST" : "a POST per request")})" );
			await Task.Delay( 500 * failures, ct );
		}
	}

	/// <summary> A Motion JPEG camera over HTTP (multipart/x-mixed-replace): frames timed by arrival. Reconnects when it drops. </summary>
	async Task RunMjpeg( Uri uri, CancellationToken ct )
	{
		Segmenter.ExpectMjpeg();
		var clock = System.Diagnostics.Stopwatch.StartNew();
		var failures = 0;
		while ( !ct.IsCancellationRequested )
		{
			try
			{
				using var stream = await Http.RequestStreamAsync( uri.ToString(), cancellationToken: ct );
				var splitter = new MjpegSplitter();
				var buffer = new byte[64 * 1024];
				while ( true )
				{
					var n = await stream.ReadAsync( buffer, ct );
					if ( n <= 0 ) break;
					BytesReceived += n;
					failures = 0;
					splitter.Feed( buffer.AsSpan( 0, n ), jpeg => Segmenter.AddJpeg( (long)(clock.Elapsed.TotalSeconds * 90000), jpeg ) );
				}
			}
			catch ( System.Net.Http.HttpRequestException e ) when ( e.StatusCode is { } status && (int)status is >= 400 and < 500 )
			{
				throw new ResolveException( $"The camera returned {(int)status}." );
			}
			catch ( Exception e ) when ( e is not OperationCanceledException && e is not ResolveException && e is not InvalidOperationException )
			{
				Log.Trace( $"[bimp] live mjpeg: {e.Message}" );
			}

			if ( ++failures > 5 ) throw new ResolveException( "Lost the camera stream." );
			await Task.Delay( 1000 * failures, ct );
		}
	}

	/// <summary> A continuous (or finite) MPEG-TS over HTTP. Reconnects when a live stream drops. </summary>
	async Task RunTs( Uri uri, CancellationToken ct )
	{
		var demuxer = new TsDemuxer( Segmenter );
		var failures = 0;

		// A file has a length; a live stream doesn't. (Http.RequestAsync reads the whole body before returning,
		// which never happens for a live stream - so ask for the headers alone, then stream the body.)
		long? length = null;
		try
		{
			using var head = await Http.RequestAsync( uri.ToString(), "HEAD", cancellationToken: ct );
			if ( head.IsSuccessStatusCode ) length = head.Content.Headers.ContentLength;
		}
		catch ( Exception e ) when ( e is not OperationCanceledException && e is not InvalidOperationException )
		{
			// some servers don't do HEAD - treat it as live
		}

		while ( !ct.IsCancellationRequested )
		{
			long received = 0;
			try
			{
				using var stream = await Http.RequestStreamAsync( uri.ToString(), cancellationToken: ct );
				var buffer = new byte[64 * 1024];

				while ( true )
				{
					await WaitForPlayback( ct );
					var n = await stream.ReadAsync( buffer, ct );
					if ( n <= 0 ) break;
					received += n;
					BytesReceived += n;
					failures = 0;
					demuxer.Feed( buffer.AsSpan( 0, n ) );
					if ( demuxer.Problem is not null && !demuxer.FoundProgram ) throw new ResolveException( demuxer.Problem );
				}
			}
			catch ( System.Net.Http.HttpRequestException e ) when ( e.StatusCode is { } status && (int)status is >= 400 and < 500 )
			{
				throw new ResolveException( $"The stream returned {(int)status}." );
			}
			catch ( Exception e ) when ( e is not OperationCanceledException && e is not ResolveException && e is not InvalidOperationException )
			{
				Log.Trace( $"[bimp] live ts: {e.Message}" );
			}

			// a file with a length that we read to the end is finished, not dropped
			if ( length is > 0 && received >= length )
			{
				demuxer.Flush();
				Segmenter.End();
				return;
			}

			if ( ++failures > 5 ) throw new ResolveException( "Lost the live stream." );
			await Task.Delay( 1000 * failures, ct );
		}
	}

	/// <summary> Hold ingest while it's far enough ahead of what's playing. </summary>
	public async Task WaitForPlayback( CancellationToken ct )
	{
		while ( Segmenter.Newest - Math.Max( PlayingSeq, 0 ) >= MaxAhead && !ct.IsCancellationRequested )
			await Task.Delay( 100, ct );
	}

	/// <summary> A url without its user:password, for logs. </summary>
	public static string Redact( string url )
	{
		return Uri.TryCreate( url, UriKind.Absolute, out var u ) && !string.IsNullOrEmpty( u.UserInfo )
			? url.Replace( u.UserInfo + "@", "***@" )
			: url;
	}

	public void Dispose()
	{
		cts.Cancel();
		Segmenter.Dispose();
	}
}
