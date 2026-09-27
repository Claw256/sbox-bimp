using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace Bimp.Resolver.Live;

/// <summary>
/// RTSP tunnelled over HTTP (the QuickTime / Apple scheme most IP cameras support) - the only way to reach an RTSP
/// camera from sandboxed code, which gets no TCP or UDP sockets.
/// <para>
/// One long-lived HTTP GET carries everything from the camera (RTSP responses and the RTP media, interleaved as
/// in RTSP-over-TCP); each RTSP request goes up as a separate HTTP POST whose body is the request in base64.
/// Both carry the same x-sessioncookie. rtsp://, rtspt:// and rtsps:// all map to this.
/// </para>
/// </summary>
public sealed class RtspTunnel
{
	readonly Uri rtspUrl;
	readonly LiveSegmenter sink;
	readonly string user, password;
	readonly string cookie = Guid.NewGuid().ToString( "N" )[..22];

	string httpUrl;
	int cseq;

	/// <summary>
	/// Requests go up one long POST, as in Apple's scheme (VRCDN only reads the first POST of a tunnel), or a POST
	/// each (some cameras only take that). <see cref="LiveStream"/> tries the other on a reconnect.
	/// </summary>
	public bool LongPost { get; }

	/// <summary> The session got as far as PLAY, so this way of tunnelling works with the server. </summary>
	public bool Played { get; private set; }
	bool answered;
	UpstreamStream upstream;
	string session;
	int sessionTimeout = 60;
	string contentBase;
	(string realm, string nonce, string qop, bool digest)? auth;
	int nonceCount;

	readonly Dictionary<int, TaskCompletionSource<Response>> waiting = new();
	readonly Dictionary<int, Track> channels = new();
	readonly List<Track> tracks = new();
	readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

	sealed class Response
	{
		public int Status;
		public string Reason;
		public Dictionary<string, string> Headers = new( StringComparer.OrdinalIgnoreCase );
		public string Body = "";
	}

	public RtspTunnel( Uri url, LiveSegmenter sink, bool longPost = true )
	{
		LongPost = longPost;
		rtspUrl = url;
		this.sink = sink;
		if ( !string.IsNullOrEmpty( url.UserInfo ) )
		{
			var parts = url.UserInfo.Split( ':', 2 );
			user = Uri.UnescapeDataString( parts[0] );
			password = parts.Length > 1 ? Uri.UnescapeDataString( parts[1] ) : "";
		}
	}

	/// <summary> The rtsp url as sent in requests: rtsp://, no credentials. </summary>
	string RequestUrl => $"rtsp://{rtspUrl.Host}{(rtspUrl.IsDefaultPort || rtspUrl.Port <= 0 ? "" : $":{rtspUrl.Port}")}{rtspUrl.PathAndQuery}";

	/// <summary> The last failure is worth reconnecting for (a lost answer or a dropped tunnel, not a refusal). </summary>
	public bool Retryable { get; private set; }

	Action<int> received;

	public async Task Run( CancellationToken ct, Action<string> describe, Action<int> bytesReceived = null )
	{
		// The tunnel is HTTP on the camera's web port, or on the RTSP port itself (servers like VRCDN tell RTSP
		// and HTTP apart on 554): the port in the link if there is one, else 554 then 80 (443 for rtsps).
		// A link with the RTSP port (554) that refuses HTTP is retried on 80.
		var secure = rtspUrl.Scheme.Equals( "rtsps", StringComparison.OrdinalIgnoreCase );
		var explicitPort = rtspUrl.IsDefaultPort || rtspUrl.Port <= 0 ? -1 : rtspUrl.Port;
		var ports = explicitPort > 0 ? (explicitPort == 554 ? new[] { 554, 80 } : new[] { explicitPort }) : secure ? new[] { 443 } : new[] { 554, 80 };

		Stream stream = null;
		Exception last = null;
		foreach ( var port in ports )
		{
			httpUrl = $"{(secure ? "https" : "http")}://{rtspUrl.Host}:{port}{rtspUrl.PathAndQuery}";
			// a port nothing listens on can hang until the TCP timeout - give up on it sooner
			using var connect = CancellationTokenSource.CreateLinkedTokenSource( ct );
			connect.CancelAfter( ports.Length > 1 ? 6000 : 20000 );
			try
			{
				stream = await Http.RequestStreamAsync( httpUrl, headers: new()
				{
					["x-sessioncookie"] = cookie,
					["Accept"] = "application/x-rtsp-tunnelled",
					["Pragma"] = "no-cache",
					["Cache-Control"] = "no-cache",
				}, cancellationToken: connect.Token );
				break;
			}
			catch ( InvalidOperationException ) { throw; } // not allowed (private network / raw IP)
			catch ( Exception e ) when ( !ct.IsCancellationRequested )
			{
				last = e;
			}
		}

		if ( stream is null )
			throw new ResolveException( $"Couldn't open an RTSP-over-HTTP tunnel to {rtspUrl.Host} ({last?.Message}). The camera must support RTSP over HTTP tunnelling - put its HTTP port in the link." );

		using ( stream )
		{
			received = bytesReceived;
			// kept rather than left unobserved when a request fails first (the tunnel closing then faults the read)
			var reader = Observed( ReadLoop( stream, ct ) );

			var describeResponse = await Request( "DESCRIBE", RequestUrl, new() { ["Accept"] = "application/sdp" }, ct );
			if ( describeResponse.Status != 200 ) throw new ResolveException( $"The camera refused DESCRIBE: {describeResponse.Status} {describeResponse.Reason}" );

			// some servers (VRCDN) hand out the session at DESCRIBE already
			if ( describeResponse.Headers.TryGetValue( "Session", out var describeSession ) ) session = describeSession.Split( ';' )[0].Trim();
			contentBase = describeResponse.Headers.GetValueOrDefault( "Content-Base" ) ?? describeResponse.Headers.GetValueOrDefault( "Content-Location" ) ?? RequestUrl;
			var sdp = Sdp.Parse( describeResponse.Body );
			SetupTracks( sdp );
			if ( tracks.Count == 0 ) throw new ResolveException( "The camera offers no stream that can be played (needs H.264, AV1 or MJPEG video, or AAC / G.711 audio)." );

			var channel = 0;
			foreach ( var track in tracks )
			{
				var headers = new Dictionary<string, string> { ["Transport"] = $"RTP/AVP/TCP;unicast;interleaved={channel}-{channel + 1}" };
				var r = await Request( "SETUP", ControlUrl( sdp.Control, track.Media.Control ), headers, ct );
				if ( r.Status != 200 ) throw new ResolveException( $"The camera refused SETUP: {r.Status} {r.Reason}" );

				if ( r.Headers.TryGetValue( "Session", out var s ) )
				{
					var parts = s.Split( ';' );
					session = parts[0].Trim();
					foreach ( var p in parts.Skip( 1 ) )
						if ( p.Trim().StartsWith( "timeout=" ) && int.TryParse( p.Trim()[8..], out var t ) ) sessionTimeout = t;
				}

				// the camera may pick other channels
				var transport = r.Headers.GetValueOrDefault( "Transport" ) ?? "";
				var m = System.Text.RegularExpressions.Regex.Match( transport, "interleaved=(\\d+)" );
				var used = m.Success ? int.Parse( m.Groups[1].Value ) : channel;
				channels[used] = track;
				channel = used + 2;
			}

			describe( "RTSP " + string.Join( " + ", tracks.Select( t => t.Name ) ) );

			var play = await Request( "PLAY", ControlUrl( sdp.Control, null ), new() { ["Range"] = "npt=0.000-" }, ct );
			if ( play.Status != 200 ) throw new ResolveException( $"The camera refused PLAY: {play.Status} {play.Reason}" );
			Played = true;

			_ = KeepAlive( ct );
			await reader;
			if ( readError is not null and not OperationCanceledException ) throw readError;
		}

		Retryable = true;
		throw new ResolveException( "The camera closed the stream." );
	}

	Exception readError;

	async Task Observed( Task task )
	{
		try { await task; }
		catch ( Exception e ) { readError = e; }
	}

	string ControlUrl( string sessionControl, string trackControl )
	{
		var control = trackControl ?? sessionControl;
		if ( string.IsNullOrEmpty( control ) || control == "*" ) return contentBase;
		if ( control.StartsWith( "rtsp://", StringComparison.OrdinalIgnoreCase ) ) return control;
		return contentBase.TrimEnd( '/' ) + "/" + control.TrimStart( '/' );
	}

	void SetupTracks( Sdp sdp )
	{
		// AV1 first: the engine's AV1 decoder presents more evenly than its H.264 one (see VideoFormat)
		var av1 = sdp.Media.FirstOrDefault( m => m.Type == "video" && m.Encoding.Equals( "AV1", StringComparison.OrdinalIgnoreCase ) );
		var video = sdp.Media.FirstOrDefault( m => m.Type == "video" && m.Encoding.Equals( "H264", StringComparison.OrdinalIgnoreCase ) );
		var jpeg = sdp.Media.FirstOrDefault( m => m.Type == "video" && m.Encoding.Equals( "JPEG", StringComparison.OrdinalIgnoreCase ) );
		if ( av1 is not null ) tracks.Add( new Av1Track( av1, sink ) );
		else if ( video is not null ) tracks.Add( new H264Track( video, sink ) );
		else if ( jpeg is not null ) tracks.Add( new MjpegTrack( jpeg, sink ) );
		else if ( sdp.Media.FirstOrDefault( m => m.Type == "video" ) is { } other )
		{
			if ( other.Encoding.Equals( "H265", StringComparison.OrdinalIgnoreCase ) || other.Encoding.Equals( "HEVC", StringComparison.OrdinalIgnoreCase ) )
				throw new ResolveException( "This camera's video is H.265 (HEVC), which the engine can't decode - set it to H.264, AV1 or MJPEG." );
			Log.Warning( $"[bimp] rtsp: video is {other.Encoding}, only H.264, AV1 and MJPEG can be played" );
		}

		var audio = sdp.Media.Where( m => m.Type == "audio" ).ToList();
		var aac = audio.FirstOrDefault( m => m.Encoding.Equals( "mpeg4-generic", StringComparison.OrdinalIgnoreCase ) );
		var g711 = audio.FirstOrDefault( m => m.Encoding is "PCMU" or "PCMA" || m.PayloadType is 0 or 8 );
		if ( aac is not null ) tracks.Add( new AacTrack( aac, sink ) );
		else if ( g711 is not null ) tracks.Add( new G711Track( g711, sink ) );
		else if ( audio.Count > 0 ) Log.Warning( $"[bimp] rtsp: audio is {audio[0].Encoding}, which isn't supported - video only" );
	}

	async Task KeepAlive( CancellationToken ct )
	{
		var interval = Math.Clamp( sessionTimeout / 2, 10, 60 );
		while ( !ct.IsCancellationRequested )
		{
			await Task.Delay( interval * 1000, ct );
			try { await Request( "OPTIONS", RequestUrl, null, ct ); }
			catch ( Exception e ) when ( e is not OperationCanceledException ) { Log.Trace( $"[bimp] rtsp keepalive: {e.Message}" ); }
		}
	}

	/// <summary>
	/// Send an RTSP request up a POST and wait for its response on the GET. Retries once with credentials on 401.
	/// </summary>
	async Task<Response> Request( string method, string url, Dictionary<string, string> headers, CancellationToken ct, bool retried = false )
	{
		var id = ++cseq;
		var sb = new StringBuilder();
		sb.Append( $"{method} {url} RTSP/1.0\r\nCSeq: {id}\r\n" );
		if ( session is not null ) sb.Append( $"Session: {session}\r\n" );
		if ( Authorization( method, url ) is { } a ) sb.Append( $"Authorization: {a}\r\n" );
		foreach ( var (k, v) in headers ?? new() ) sb.Append( $"{k}: {v}\r\n" );
		// padded to a multiple of 3 bytes, so no base64 '=' lands in the middle of the long POST's body
		var userAgent = "User-Agent: bimp";
		while ( (sb.Length + userAgent.Length + 4) % 3 != 0 ) userAgent += " ";
		sb.Append( userAgent + "\r\n\r\n" );
		var base64 = Encoding.ASCII.GetBytes( Convert.ToBase64String( Encoding.ASCII.GetBytes( sb.ToString() ) ) );

		var tcs = new TaskCompletionSource<Response>( TaskCreationOptions.RunContinuationsAsynchronously );
		waiting[id] = tcs;

		// The server never answers the POST itself (answers come down the GET), so don't wait for it. A POST of
		// its own is dropped once the answer came down the GET.
		using var postCts = CancellationTokenSource.CreateLinkedTokenSource( ct );
		var persistent = LongPost;
		if ( persistent )
		{
			// its declared length runs out after an hour or so of keep-alives: start another
			var padded = UpstreamStream.Padded( base64 );
			if ( upstream is not null && !upstream.Fits( padded.Length ) )
			{
				upstream.Complete();
				upstream = null;
			}
			if ( upstream is null )
			{
				upstream = new UpstreamStream();
				var longPost = new StreamContent( upstream, 64 * 1024 );
				longPost.Headers.ContentLength = UpstreamStream.DeclaredLength;
				longPost.Headers.TryAddWithoutValidation( "Content-Type", "application/x-rtsp-tunnelled" );
				_ = Post( longPost, ct );
			}
			upstream.Send( padded );
		}
		else
		{
			var content = new ByteArrayContent( base64 );
			content.Headers.TryAddWithoutValidation( "Content-Type", "application/x-rtsp-tunnelled" );
			_ = Post( content, postCts.Token );
		}

		Response response;
		// the first answer shows whether this way of tunnelling works - don't wait the full 10 s for it
		using ( var timeout = new CancellationTokenSource( !answered ? 4000 : 10000 ) )
		using ( timeout.Token.Register( () => tcs.TrySetCanceled() ) )
		using ( ct.Register( () => tcs.TrySetCanceled() ) )
		{
			try
			{
				response = await tcs.Task;
			}
			catch ( OperationCanceledException )
			{
				ct.ThrowIfCancellationRequested();
				Retryable = true;
				throw new ResolveException( $"The camera didn't answer {method} (is RTSP over HTTP enabled on it?)." );
			}
			finally
			{
				waiting.Remove( id );
				postCts.Cancel();
			}
		}
		answered = true;
		if ( response.Status == 401 && !retried && user is not null && response.Headers.TryGetValue( "WWW-Authenticate", out var challenge ) )
		{
			ParseChallenge( challenge );
			return await Request( method, url, headers, ct, true );
		}
		if ( response.Status == 401 ) throw new ResolveException( user is null ? "The camera needs a login - put it in the link (rtsp://user:password@camera/...)." : "The camera rejected the login." );

		return response;
	}

	async Task Post( HttpContent content, CancellationToken ct )
	{
		try
		{
			using var response = await Http.RequestAsync( httpUrl, "POST", content, new() { ["x-sessioncookie"] = cookie, ["Pragma"] = "no-cache", ["Cache-Control"] = "no-cache" }, ct );
		}
		catch ( Exception e ) when ( e is OperationCanceledException or HttpRequestException )
		{
			// expected: dropped once the answer came down the GET
		}
		finally
		{
			content.Dispose();
		}
	}

	void ParseChallenge( string header )
	{
		// may hold several challenges ("Digest ..., Basic ...") joined - prefer Digest
		var digest = header.Contains( "Digest", StringComparison.OrdinalIgnoreCase );
		string Field( string name )
		{
			var m = System.Text.RegularExpressions.Regex.Match( header, name + "=\"?([^\",]*)\"?", System.Text.RegularExpressions.RegexOptions.IgnoreCase );
			return m.Success ? m.Groups[1].Value : null;
		}
		auth = (Field( "realm" ) ?? "", Field( "nonce" ) ?? "", Field( "qop" ), digest);
		nonceCount = 0;
	}

	string Authorization( string method, string uri )
	{
		if ( auth is not { } a || user is null ) return null;
		if ( !a.digest ) return "Basic " + Convert.ToBase64String( Encoding.UTF8.GetBytes( $"{user}:{password}" ) );

		static string Md5( string s ) => Convert.ToHexString( System.Security.Cryptography.MD5.HashData( Encoding.UTF8.GetBytes( s ) ) ).ToLowerInvariant();
		var ha1 = Md5( $"{user}:{a.realm}:{password}" );
		var ha2 = Md5( $"{method}:{uri}" );

		if ( a.qop is not null && a.qop.Split( ',' ).Any( q => q.Trim() == "auth" ) )
		{
			var nc = (++nonceCount).ToString( "x8" );
			var cnonce = Guid.NewGuid().ToString( "N" )[..16];
			var response = Md5( $"{ha1}:{a.nonce}:{nc}:{cnonce}:auth:{ha2}" );
			return $"Digest username=\"{user}\", realm=\"{a.realm}\", nonce=\"{a.nonce}\", uri=\"{uri}\", response=\"{response}\", qop=auth, nc={nc}, cnonce=\"{cnonce}\"";
		}

		return $"Digest username=\"{user}\", realm=\"{a.realm}\", nonce=\"{a.nonce}\", uri=\"{uri}\", response=\"{Md5( $"{ha1}:{a.nonce}:{ha2}" )}\"";
	}

	/// <summary>
	/// Everything from the camera: RTSP responses and '$'-framed interleaved RTP/RTCP packets.
	/// </summary>
	async Task ReadLoop( Stream stream, CancellationToken ct )
	{
		var buf = new byte[256 * 1024];
		int start = 0, end = 0;

		while ( !ct.IsCancellationRequested )
		{
			// make room
			if ( end == buf.Length )
			{
				if ( start > 0 ) { Buffer.BlockCopy( buf, start, buf, 0, end - start ); end -= start; start = 0; }
				else Array.Resize( ref buf, buf.Length * 2 );
			}

			var n = await stream.ReadAsync( buf.AsMemory( end ), ct );
			if ( n <= 0 ) return;
			received?.Invoke( n );
			end += n;

			while ( end - start > 0 )
			{
				if ( buf[start] == (byte)'$' )
				{
					if ( end - start < 4 ) break;
					var ch = buf[start + 1];
					var len = (buf[start + 2] << 8) | buf[start + 3];
					if ( end - start < 4 + len ) break;
					var packet = buf.AsSpan( start + 4, len );
					if ( (ch & 1) == 0 )
					{
						if ( TrackFor( ch, packet ) is { } track ) track.OnRtp( packet, clock.Elapsed.TotalSeconds );
					}
					else if ( channels.TryGetValue( ch - 1, out var rtcpTrack ) ) rtcpTrack.OnRtcp( packet );
					start += 4 + len;
					continue;
				}

				// text: a response (or a request from the server) up to the blank line, then its body
				var headerEnd = IndexOf( buf, start, end, "\r\n\r\n"u8 );
				if ( headerEnd < 0 )
				{
					if ( end - start > 64 * 1024 ) start = end; // garbage
					break;
				}

				var headerText = Encoding.ASCII.GetString( buf, start, headerEnd - start );
				var lines = headerText.Split( "\r\n" );
				var response = new Response();
				foreach ( var line in lines.Skip( 1 ) )
				{
					var colon = line.IndexOf( ':' );
					if ( colon > 0 ) response.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
				}

				var bodyLength = int.TryParse( response.Headers.GetValueOrDefault( "Content-Length" ), out var cl ) ? cl : 0;
				if ( end - (headerEnd + 4) < bodyLength ) break;
				response.Body = Encoding.UTF8.GetString( buf, headerEnd + 4, bodyLength );
				start = headerEnd + 4 + bodyLength;

				if ( !lines[0].StartsWith( "RTSP/" ) ) continue; // a request from the server - ignore

				var status = lines[0].Split( ' ', 3 );
				response.Status = status.Length > 1 && int.TryParse( status[1], out var st ) ? st : 0;
				response.Reason = status.Length > 2 ? status[2] : "";

				// Line the tracks up before any of their packets are handled (they follow right after)
				if ( response.Headers.TryGetValue( "RTP-Info", out var rtpInfo ) ) ApplyRtpInfo( rtpInfo );

				if ( int.TryParse( response.Headers.GetValueOrDefault( "CSeq" ), out var id ) && waiting.TryGetValue( id, out var tcs ) )
					tcs.TrySetResult( response );
			}

			if ( start == end ) start = end = 0;
		}
	}

	readonly HashSet<int> checkedChannels = new();

	/// <summary>
	/// The track an RTP channel carries. Checked once per channel against the packet's payload type: VRCDN's SETUP
	/// answers name each track's channels swapped (video announced on 2-3 arrives on 0-1).
	/// </summary>
	Track TrackFor( int ch, ReadOnlySpan<byte> packet )
	{
		channels.TryGetValue( ch, out var track );
		if ( packet.Length < 2 || !checkedChannels.Add( ch ) ) return track;

		var pt = packet[1] & 0x7F;
		if ( track is not null && track.Media.PayloadType == pt ) return track;
		var byType = tracks.Where( t => t.Media.PayloadType == pt ).ToList();
		if ( byType.Count != 1 ) return track;

		// swap the channel pairs round: whatever was on this one moves to the byType track's old one
		var other = channels.FirstOrDefault( kv => kv.Value == byType[0] && kv.Key != ch ).Key;
		if ( track is not null && channels.ContainsKey( other ) ) channels[other] = track;
		channels[ch] = byType[0];
		Log.Info( $"[bimp] rtsp: channel {ch} carries {byType[0].Name} (payload type {pt}), not what SETUP said" );
		return byType[0];
	}

	/// <summary> RTP-Info: url=...;seq=...;rtptime=..., one per track - the RTP time of the play start. </summary>
	void ApplyRtpInfo( string header )
	{
		foreach ( var entry in header.Split( ',' ) )
		{
			string url = null;
			long? rtptime = null;
			foreach ( var part in entry.Split( ';' ) )
			{
				var kv = part.Trim().Split( '=', 2 );
				if ( kv.Length != 2 ) continue;
				if ( kv[0] == "url" ) url = kv[1];
				else if ( kv[0] == "rtptime" && long.TryParse( kv[1], out var t ) ) rtptime = t;
			}
			if ( rtptime is null ) continue;

			var track = tracks.FirstOrDefault( t => url is not null && t.Media.Control is not null && (url.EndsWith( t.Media.Control ) || url == t.Media.Control) )
				?? (tracks.Count == 1 ? tracks[0] : null);
			track?.SetOrigin( rtptime.Value );
		}
	}

	static int IndexOf( byte[] buf, int start, int end, ReadOnlySpan<byte> pattern )
	{
		var i = buf.AsSpan( start, end - start ).IndexOf( pattern );
		return i < 0 ? -1 : start + i;
	}

	/// <summary>
	/// The body of the long POST: requests as they're queued. The declared length is just large, so the connection
	/// stays open for the session.
	/// <para>
	/// HttpClient holds back body writes that fit its 4 KB connection buffer until the body ends - which a tunnel's
	/// never does - and a custom HttpContent that flushes needs a type the sandbox doesn't allow. A single write of
	/// over 8 KB goes straight to the socket, so each request goes up as one write, after enough CRLFs (which RTSP
	/// servers skip between messages) to make it that big.
	/// </para>
	/// </summary>
	sealed class UpstreamStream : Stream
	{
		public const int DeclaredLength = 100_000_000;

		/// <summary> base64 of 9216 bytes of CRLF (a multiple of 3, so it decodes on its own). </summary>
		static readonly byte[] Padding = Encoding.ASCII.GetBytes( Convert.ToBase64String( Encoding.ASCII.GetBytes( string.Concat( Enumerable.Repeat( "\r\n", 4608 ) ) ) ) );

		public static byte[] Padded( byte[] request )
		{
			var result = new byte[Padding.Length + request.Length];
			Padding.CopyTo( result, 0 );
			request.CopyTo( result, Padding.Length );
			return result;
		}

		readonly System.Threading.Channels.Channel<byte[]> queue = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
		byte[] current;
		int at, total, queued;

		public bool Fits( int length ) => queued + length <= DeclaredLength;

		public void Send( byte[] data )
		{
			queued += data.Length;
			queue.Writer.TryWrite( data );
		}

		public void Complete() => queue.Writer.TryComplete();

		public override async ValueTask<int> ReadAsync( Memory<byte> buffer, CancellationToken ct = default )
		{
			while ( current is null || at >= current.Length )
			{
				if ( total >= DeclaredLength || !await queue.Reader.WaitToReadAsync( ct ) ) return 0;
				queue.Reader.TryRead( out current );
				at = 0;
			}
			var n = Math.Min( Math.Min( buffer.Length, current.Length - at ), DeclaredLength - total );
			current.AsMemory( at, n ).CopyTo( buffer );
			at += n;
			total += n;
			return n;
		}

		public override Task<int> ReadAsync( byte[] buffer, int offset, int count, CancellationToken ct ) => ReadAsync( buffer.AsMemory( offset, count ), ct ).AsTask();
		public override int Read( byte[] buffer, int offset, int count ) => throw new NotSupportedException();
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => total; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException();
		public override void SetLength( long value ) => throw new NotSupportedException();
		public override void Write( byte[] buffer, int offset, int count ) => throw new NotSupportedException();
	}

	//
	// SDP
	//

	sealed class Sdp
	{
		public string Control;
		public readonly List<MediaDesc> Media = new();

		public static Sdp Parse( string text )
		{
			var sdp = new Sdp();
			MediaDesc current = null;
			foreach ( var raw in text.Split( '\n' ) )
			{
				var line = raw.Trim();
				if ( line.Length < 2 || line[1] != '=' ) continue;
				var value = line[2..];

				if ( line[0] == 'm' )
				{
					var p = value.Split( ' ' );
					current = new MediaDesc { Type = p[0], PayloadType = p.Length > 3 && int.TryParse( p[3], out var pt ) ? pt : -1 };
					// static payload types have no rtpmap
					if ( current.PayloadType == 0 ) { current.Encoding = "PCMU"; current.ClockRate = 8000; }
					if ( current.PayloadType == 8 ) { current.Encoding = "PCMA"; current.ClockRate = 8000; }
					if ( current.PayloadType == 26 ) { current.Encoding = "JPEG"; current.ClockRate = 90000; }
					sdp.Media.Add( current );
				}
				else if ( line[0] == 'a' )
				{
					if ( value.StartsWith( "control:" ) )
					{
						if ( current is null ) sdp.Control = value[8..];
						else current.Control = value[8..];
					}
					else if ( value.StartsWith( "rtpmap:" ) && current is not null )
					{
						// rtpmap:96 H264/90000 or rtpmap:97 mpeg4-generic/48000/2
						var p = value[7..].Split( ' ', 2 );
						if ( p.Length == 2 && int.TryParse( p[0], out var pt ) && pt == current.PayloadType )
						{
							var enc = p[1].Split( '/' );
							current.Encoding = enc[0];
							if ( enc.Length > 1 && int.TryParse( enc[1], out var rate ) ) current.ClockRate = rate;
							if ( enc.Length > 2 && int.TryParse( enc[2], out var ch ) ) current.Channels = ch;
						}
					}
					else if ( value.StartsWith( "fmtp:" ) && current is not null )
					{
						var p = value[5..].Split( ' ', 2 );
						if ( p.Length == 2 )
						{
							foreach ( var kv in p[1].Split( ';' ) )
							{
								var pair = kv.Trim().Split( '=', 2 );
								if ( pair.Length == 2 ) current.Fmtp[pair[0].Trim().ToLowerInvariant()] = pair[1].Trim();
							}
						}
					}
				}
			}
			return sdp;
		}
	}

	sealed class MediaDesc
	{
		public string Type;
		public int PayloadType;
		public string Encoding = "";
		public int ClockRate = 90000;
		public int Channels = 1;
		public string Control;
		public readonly Dictionary<string, string> Fmtp = new();
	}

	//
	// RTP depacketizing
	//

	abstract class Track
	{
		public MediaDesc Media;
		protected readonly LiveSegmenter Sink;
		long? origin;
		double originTime;
		long lastTs = -1, wraps;
		protected int LastSeq = -1;

		protected Track( MediaDesc media, LiveSegmenter sink )
		{
			Media = media;
			Sink = sink;
		}

		public abstract string Name { get; }

		/// <summary> From RTP-Info: this RTP time is stream time 0. </summary>
		public void SetOrigin( long rtptime )
		{
			origin = rtptime;
			originTime = 0;
		}

		/// <summary> RTP timestamp to stream time in 90 kHz units. </summary>
		protected long Time( uint ts, double arrival )
		{
			// unwrap 32 bits
			long t = ts;
			if ( lastTs >= 0 && t < lastTs - 0x80000000L ) wraps++;
			lastTs = t;
			t += wraps << 32;

			if ( origin is null )
			{
				// no RTP-Info: line tracks up by when their first packet arrived
				origin = t;
				originTime = arrival;
			}
			return (long)(originTime * 90000) + (t - origin.Value) * 90000 / Media.ClockRate;
		}

		public void OnRtp( ReadOnlySpan<byte> p, double arrival )
		{
			if ( p.Length < 12 || (p[0] >> 6) != 2 ) return;
			var padding = (p[0] & 0x20) != 0;
			var extension = (p[0] & 0x10) != 0;
			var csrc = p[0] & 0x0F;
			var marker = (p[1] & 0x80) != 0;
			var seq = (p[2] << 8) | p[3];
			var ts = (uint)((p[4] << 24) | (p[5] << 16) | (p[6] << 8) | p[7]);

			var at = 12 + csrc * 4;
			if ( extension )
			{
				if ( at + 4 > p.Length ) return;
				at += 4 + ((p[at + 2] << 8) | p[at + 3]) * 4;
			}
			var end = p.Length - (padding ? p[^1] : 0);
			if ( at >= end ) return;

			var lost = LastSeq >= 0 && seq != ((LastSeq + 1) & 0xFFFF);
			LastSeq = seq;
			Payload( p[at..end], ts, marker, lost, arrival );
		}

		protected abstract void Payload( ReadOnlySpan<byte> data, uint ts, bool marker, bool lost, double arrival );

		/// <summary>
		/// RTCP from the server: sender reports tie an RTP time to the sender's wall clock (NTP), which is how the
		/// latency readout knows when a frame was sent.
		/// </summary>
		public void OnRtcp( ReadOnlySpan<byte> p )
		{
			var at = 0;
			while ( at + 8 <= p.Length )
			{
				var length = (((p[at + 2] << 8) | p[at + 3]) + 1) * 4;
				if ( p[at + 1] == 200 && at + 20 <= p.Length && origin is not null )
				{
					var ntpSeconds = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian( p[(at + 8)..] );
					var ntpFraction = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian( p[(at + 12)..] );
					var rtp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian( p[(at + 16)..] );
					var unix = ntpSeconds - 2208988800.0 + ntpFraction / 4294967296.0;
					OnSenderReport( StreamTimeOf( rtp ), unix );
				}
				at += length;
			}
		}

		/// <summary> Like <see cref="Time"/>, without moving the unwrap state (a report's time can be older). </summary>
		long StreamTimeOf( uint ts )
		{
			long t = ts + (wraps << 32);
			if ( lastTs >= 0 && ts < lastTs - 0x80000000L ) t += 1L << 32;
			else if ( lastTs >= 0 && ts > lastTs + 0x80000000L ) t -= 1L << 32;
			return (long)(originTime * 90000) + (t - origin.Value) * 90000 / Media.ClockRate;
		}

		/// <summary> Video's sender reports give the latency readout; audio's line the audio up with it (lip sync). </summary>
		protected virtual bool IsVideo => false;

		void OnSenderReport( long streamTime, double unixSeconds ) => Sink.SetWallClock( streamTime, unixSeconds, IsVideo );
	}

	/// <summary> RFC 6184: single NAL units, STAP-A aggregates and FU-A fragments. </summary>
	sealed class H264Track : Track
	{
		readonly List<byte[]> nals = new();
		List<byte> fragment;
		uint auTs;
		bool haveAu;

		public override string Name => Sink.VideoInfo ?? "H.264";

		protected override bool IsVideo => true;

		public H264Track( MediaDesc media, LiveSegmenter sink ) : base( media, sink )
		{
			sink.ExpectVideo();
			// parameter sets from the SDP, in case the camera doesn't repeat them in band
			if ( media.Fmtp.TryGetValue( "sprop-parameter-sets", out var sets ) )
			{
				byte[] sps = null, pps = null;
				foreach ( var b64 in sets.Split( ',' ) )
				{
					try
					{
						var nal = Convert.FromBase64String( b64.Trim() );
						if ( H264.NalType( nal ) == H264.NalSps ) sps = nal;
						else if ( H264.NalType( nal ) == H264.NalPps ) pps = nal;
					}
					catch ( FormatException ) { }
				}
				sink.SetParameterSets( sps, pps );
			}
		}

		protected override void Payload( ReadOnlySpan<byte> d, uint ts, bool marker, bool lost, double arrival )
		{
			// a new timestamp means the previous access unit is complete (if its marker got lost)
			if ( haveAu && ts != auTs ) Emit( arrival );
			auTs = ts;
			haveAu = true;
			if ( lost ) fragment = null;

			var type = d[0] & 0x1F;
			if ( type is >= 1 and <= 23 )
			{
				nals.Add( d.ToArray() );
			}
			else if ( type == 24 ) // STAP-A
			{
				var i = 1;
				while ( i + 2 <= d.Length )
				{
					var size = (d[i] << 8) | d[i + 1];
					i += 2;
					if ( i + size > d.Length ) break;
					nals.Add( d.Slice( i, size ).ToArray() );
					i += size;
				}
			}
			else if ( type == 28 && d.Length > 2 ) // FU-A
			{
				var header = d[1];
				if ( (header & 0x80) != 0 )
				{
					fragment = new List<byte> { (byte)((d[0] & 0xE0) | (header & 0x1F)) };
				}
				if ( fragment is not null )
				{
					for ( int i = 2; i < d.Length; i++ ) fragment.Add( d[i] );
					if ( (header & 0x40) != 0 )
					{
						nals.Add( fragment.ToArray() );
						fragment = null;
					}
				}
			}

			if ( marker ) Emit( arrival );
		}

		void Emit( double arrival )
		{
			haveAu = false;
			if ( nals.Count == 0 ) return;
			var t = Time( auTs, arrival );
			// RTP carries presentation times only; cameras don't use B-frames, so decode time = presentation time
			Sink.AddVideo( t, t, nals.ToList() );
			nals.Clear();
		}
	}

	/// <summary>
	/// AV1 (the AOM RTP payload format): an aggregation header (Z: the first OBU element continues the last packet's,
	/// Y: the last continues in the next one, W: element count, the last without a length), then OBU elements.
	/// OBUs travel without size fields; they get them back here, which is how MP4 stores them.
	/// </summary>
	sealed class Av1Track : Track
	{
		readonly List<byte> unit = new();
		List<byte> fragment;
		uint unitTs;
		bool haveUnit;

		public override string Name => Sink.VideoInfo ?? "AV1";

		protected override bool IsVideo => true;

		public Av1Track( MediaDesc media, LiveSegmenter sink ) : base( media, sink )
		{
			sink.ExpectVideo();
		}

		protected override void Payload( ReadOnlySpan<byte> d, uint ts, bool marker, bool lost, double arrival )
		{
			if ( haveUnit && ts != unitTs ) Emit( arrival );
			unitTs = ts;
			haveUnit = true;
			if ( lost ) fragment = null;
			if ( d.Length < 2 ) return;

			var z = (d[0] & 0x80) != 0;
			var y = (d[0] & 0x40) != 0;
			var w = (d[0] >> 4) & 3;
			var i = 1;
			var element = 0;
			while ( i < d.Length )
			{
				element++;
				int size;
				if ( w == 0 || element < w )
				{
					var (length, n) = Resolver.Media.Av1.Leb128( d[i..] );
					if ( n == 0 ) break;
					i += n;
					size = (int)Math.Min( length, (ulong)(d.Length - i) );
				}
				else size = d.Length - i;

				var part = d.Slice( i, size );
				i += size;
				var continues = i >= d.Length && y;

				if ( element == 1 && z )
				{
					if ( fragment is null ) continue; // its start was lost
					Append( fragment, part );
					if ( !continues ) { Finish( fragment.ToArray() ); fragment = null; }
				}
				else if ( continues )
				{
					fragment = new List<byte>( size * 2 );
					Append( fragment, part );
				}
				else Finish( part );
			}

			if ( marker ) Emit( arrival );
		}

		static void Append( List<byte> list, ReadOnlySpan<byte> data )
		{
			foreach ( var b in data ) list.Add( b );
		}

		/// <summary> One whole OBU: into the temporal unit with a size field (dropping delimiters and padding). </summary>
		void Finish( ReadOnlySpan<byte> obu )
		{
			if ( obu.Length < 1 ) return;
			var type = (obu[0] >> 3) & 0xF;
			if ( type is Resolver.Media.Av1.ObuTemporalDelimiter or Resolver.Media.Av1.ObuTileList or Resolver.Media.Av1.ObuPadding ) return;
			if ( (obu[0] & 0x02) != 0 ) { Append( unit, obu ); return; }
			var headerLength = (obu[0] & 0x04) != 0 ? 2 : 1;
			if ( obu.Length < headerLength ) return;
			Resolver.Media.Av1.AppendSized( unit, obu[..headerLength], obu[headerLength..] );
		}

		void Emit( double arrival )
		{
			haveUnit = false;
			if ( unit.Count == 0 ) return;
			Sink.AddAv1( Time( unitTs, arrival ), unit.ToArray() );
			unit.Clear();
		}
	}

	/// <summary> RFC 2435 Motion JPEG: each frame reassembled into a JPEG and shown as it arrives. </summary>
	sealed class MjpegTrack : Track
	{
		readonly RtpJpeg jpeg = new();

		public override string Name => Sink.VideoInfo ?? "Motion JPEG";

		public MjpegTrack( MediaDesc media, LiveSegmenter sink ) : base( media, sink )
		{
			sink.ExpectMjpeg();
		}

		protected override bool IsVideo => true;

		protected override void Payload( ReadOnlySpan<byte> d, uint ts, bool marker, bool lost, double arrival )
		{
			var t = Time( ts, arrival );
			if ( jpeg.Add( d, ts, marker, lost ) is { } frame ) Sink.AddJpeg( t, frame );
		}
	}

	/// <summary> RFC 3640 AAC (mpeg4-generic, AAC-hbr): AU headers, then the access units. </summary>
	sealed class AacTrack : Track
	{
		readonly int sizeLength, indexLength;
		readonly AacConfig config;

		public override string Name => $"AAC {config?.SampleRate}Hz";

		public AacTrack( MediaDesc media, LiveSegmenter sink ) : base( media, sink )
		{
			sizeLength = int.TryParse( media.Fmtp.GetValueOrDefault( "sizelength" ), out var s ) ? s : 13;
			indexLength = int.TryParse( media.Fmtp.GetValueOrDefault( "indexlength" ), out var i ) ? i : 3;
			if ( media.Fmtp.TryGetValue( "config", out var hex ) )
			{
				try { config = AacConfig.FromAsc( Convert.FromHexString( hex ) ); } catch ( FormatException ) { }
			}
			if ( config is not null ) sink.SetAacConfig( config );
		}

		protected override void Payload( ReadOnlySpan<byte> d, uint ts, bool marker, bool lost, double arrival )
		{
			if ( config is null || d.Length < 2 ) return;
			var headerBits = (d[0] << 8) | d[1];
			var headerBytes = (headerBits + 7) / 8;
			var per = sizeLength + indexLength;
			var count = per > 0 ? headerBits / per : 0;
			var dataAt = 2 + headerBytes;
			var baseTime = Time( ts, arrival );

			for ( int k = 0; k < count; k++ )
			{
				var size = ReadBits( d.Slice( 2, headerBytes ), k * per, sizeLength );
				if ( dataAt + size > d.Length ) break;
				var pts = baseTime + (long)k * 1024 * 90000 / config.SampleRate;
				Sink.AddAudio( pts, d.Slice( dataAt, size ).ToArray() );
				dataAt += size;
			}
		}

		static int ReadBits( ReadOnlySpan<byte> d, int bit, int count )
		{
			var v = 0;
			for ( int i = 0; i < count; i++, bit++ )
				v = (v << 1) | ((d[bit >> 3] >> (7 - (bit & 7))) & 1);
			return v;
		}
	}

	/// <summary> G.711 mu-law / A-law: decoded here, played through a SoundStream beside the video. </summary>
	sealed class G711Track : Track
	{
		readonly bool alaw;

		public override string Name => alaw ? "G.711 A-law" : "G.711 mu-law";

		public G711Track( MediaDesc media, LiveSegmenter sink ) : base( media, sink )
		{
			alaw = media.Encoding == "PCMA" || media.PayloadType == 8;
			if ( media.ClockRate <= 0 || media.ClockRate == 90000 ) media.ClockRate = 8000;
		}

		protected override void Payload( ReadOnlySpan<byte> d, uint ts, bool marker, bool lost, double arrival )
		{
			Sink.AddPcm( Time( ts, arrival ), G711.Decode( d, alaw ), Media.ClockRate );
		}
	}
}
