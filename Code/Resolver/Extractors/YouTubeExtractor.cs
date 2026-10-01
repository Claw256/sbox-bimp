using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bimp.Resolver.Extractors;

/// <summary>
/// YouTube videos, shorts, music and playlists (first entry), through the InnerTube player API.
/// Only clients whose formats come with plain urls are used, so no player JavaScript is needed.
/// </summary>
public sealed class YouTubeExtractor : IExtractor, IPlaylistExtractor, ICaptionExtractor
{
	public string Key => "yt";

	const string PlayerUrl = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
	const string NextUrl = "https://www.youtube.com/youtubei/v1/next?prettyPrint=false";

	static readonly string[] Hosts = { "youtube.com", "youtu.be", "youtube-nocookie.com" };
	static readonly Regex IdPattern = new( "^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled );

	public bool CanHandle( Uri url )
	{
		var host = url.Host.ToLowerInvariant();
		return Hosts.Any( h => host == h || host.EndsWith( "." + h ) );
	}

	/// <summary>
	/// The video id in a url, or null. Sets <paramref name="playlistId"/> for playlist urls without a video.
	/// </summary>
	public static string ParseVideoId( Uri url, out string playlistId )
	{
		playlistId = null;
		var query = System.Web.HttpUtility.ParseQueryString( url.Query );
		var segments = url.AbsolutePath.Trim( '/' ).Split( '/', StringSplitOptions.RemoveEmptyEntries );

		string id = null;
		if ( url.Host.EndsWith( "youtu.be", StringComparison.OrdinalIgnoreCase ) )
			id = segments.FirstOrDefault();
		else if ( !string.IsNullOrEmpty( query["v"] ) )
			id = query["v"];
		else if ( segments.Length >= 2 && segments[0] is "shorts" or "embed" or "live" or "v" or "e" )
			id = segments[1];

		if ( id is not null && IdPattern.IsMatch( id ) ) return id;

		playlistId = query["list"];
		return null;
	}

	// a channel's live page: /@handle/live, /channel/UC.../live, /c/name/live, /user/name/live
	static readonly Regex ChannelLivePath = new( "^/(?:@[^/]+|channel/[^/]+|c/[^/]+|user/[^/]+)/live/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled );
	static readonly Regex CanonicalWatch = new( "<link rel=\"canonical\" href=\"https://www\\.youtube\\.com/watch\\?v=([A-Za-z0-9_-]{11})\"", RegexOptions.Compiled );

	/// <summary>
	/// The video a channel's live page is showing. The page names it as its canonical link while the channel is live,
	/// and the channel itself when it isn't. The consent cookie skips the EU cookie wall (a page with no video on it).
	/// </summary>
	static async Task<string> ChannelLiveVideo( Uri url, CancellationToken ct )
	{
		string html;
		try
		{
			html = await Http.RequestStringAsync( $"https://www.youtube.com{url.AbsolutePath}", headers: new() { ["Cookie"] = "SOCS=CAI" }, cancellationToken: ct );
		}
		catch ( Exception e ) when ( e is not OperationCanceledException )
		{
			throw new ResolveException( $"Couldn't open that YouTube channel: {e.Message}" );
		}

		var m = CanonicalWatch.Match( html );
		return m.Success ? m.Groups[1].Value : throw new ResolveException( "That YouTube channel isn't live right now." );
	}

	/// <summary> A link's start time (<c>t=90</c>, <c>t=1m30s</c>, <c>start=90</c>) in seconds, 0 if it has none. </summary>
	public static float StartTimeOf( Uri url )
	{
		var query = System.Web.HttpUtility.ParseQueryString( url.Query );
		var value = (query["t"] ?? query["start"])?.Trim().ToLowerInvariant();
		if ( string.IsNullOrEmpty( value ) ) return 0;

		var m = Regex.Match( value, "^(?:(\\d+)h)?(?:(\\d+)m)?(?:(\\d+)s?)?$" );
		if ( !m.Success ) return 0;

		int Part( int g ) => m.Groups[g].Success ? int.Parse( m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture ) : 0;
		return Part( 1 ) * 3600 + Part( 2 ) * 60 + Part( 3 );
	}

	public async Task<MediaInfo> GetInfoAsync( Uri url, bool audioOnly, CancellationToken ct )
	{
		var id = ParseVideoId( url, out var playlistId );
		if ( id is null && !string.IsNullOrEmpty( playlistId ) )
			id = await FirstPlaylistVideo( playlistId, ct );
		if ( id is null && ChannelLivePath.IsMatch( url.AbsolutePath ) )
			id = await ChannelLiveVideo( url, ct );
		if ( id is null )
			throw new ResolveException( "That doesn't look like a YouTube video link." );

		var (player, livePlan) = await Player( id, ct, verifyDownload: false );

		var details = player["videoDetails"];
		if ( livePlan?.Kind == StreamKind.Live )
			return new MediaInfo { Extractor = Key, Id = id, Title = details?["title"].Str() ?? id, AudioOnly = audioOnly, IsLive = true };
		if ( details?["isLiveContent"].Bool() == true && player["streamingData"]?["adaptiveFormats"] is null )
			throw new ResolveException( "This YouTube stream hasn't started, or has only just ended." );

		var formats = ParseFormats( player );
		var thumbs = details?["thumbnail"]?["thumbnails"].Items().ToList();

		// captions (and, when the first client doesn't list them, dubs) come from a client that lists them
		var (captions, otherDubs) = await CaptionsAndDubs( id, ct );
		var dubs = FormatSelector.AudioTracks( formats );
		if ( dubs.Count == 0 ) dubs = otherDubs;

		return new MediaInfo
		{
			Extractor = Key,
			Id = id,
			Title = details?["title"].Str() ?? id,
			Duration = details?["lengthSeconds"].Long() ?? 0,
			AudioOnly = audioOnly,
			Thumbnail = thumbs?.LastOrDefault()?["url"].Str(),
			Merged = true,
			Qualities = audioOnly ? new List<int>() : FormatSelector.Qualities( formats ),
			AudioTracks = dubs,
			CaptionTracks = captions,
			StartAt = StartTimeOf( url ),
		};
	}

	/// <summary>
	/// Clients that list a video's dubs (measured 2026-10-01: visionos and android_vr list only the original audio, ios and
	/// android list every track - and their urls download). Tried after bimp_yt_clients when a dub is asked for.
	/// </summary>
	const string DubClients = "ios,android";

	/// <summary>
	/// Clients whose player responses list caption tracks with urls that download (measured 2026-10-01: visionos lists
	/// none, web is unplayable without a proof-of-origin token). ios hands out json3, the others timedtext XML.
	/// </summary>
	const string CaptionClients = "ios,android_vr,android";

	/// <summary>
	/// The caption tracks of a video, and its dubs as that client lists them (ios lists both). Empty if it has none (or
	/// YouTube didn't say). Never throws.
	/// </summary>
	async Task<(List<MediaCaptionTrack> captions, List<MediaAudioTrack> dubs)> CaptionsAndDubs( string id, CancellationToken ct )
	{
		try
		{
			var visitor = await VisitorData.GetAsync( ct );
			foreach ( var client in InnerTubeClients.Ordered( CaptionClients ) )
			{
				var player = await PostPlayer( client, id, visitor, ct );
				if ( player?["playabilityStatus"]?["status"].Str() != "OK" ) continue;

				var captions = CaptionTrackNodes( player ).Select( t => new MediaCaptionTrack
				{
					Id = t["vssId"].Str(),
					Lang = t["languageCode"].Str(),
					IsAuto = t["kind"].Str() == "asr",
					Name = t["name"]?["simpleText"].Str() ?? t["name"]?["runs"]?[0]?["text"].Str() ?? t["languageCode"].Str(),
				} ).Where( t => !string.IsNullOrEmpty( t.Id ) ).ToList();
				return (captions, FormatSelector.AudioTracks( ParseFormats( player ) ));
			}
		}
		catch ( Exception e ) when ( e is not OperationCanceledException )
		{
			Log.Trace( $"[bimp] youtube {id} captions: {e.Message}" );
		}

		return (new List<MediaCaptionTrack>(), new List<MediaAudioTrack>());
	}

	static List<JsonNode> CaptionTrackNodes( JsonNode player )
		=> player?["captions"]?.Find( "captionTracks" ).Items().ToList() ?? new List<JsonNode>();

	/// <summary> One caption track's file (json3 or timedtext XML), fetched the way this client gets its streams. Null if no client could. </summary>
	public async Task<string> GetCaptionsAsync( string id, string trackId, CancellationToken ct )
	{
		var visitor = await VisitorData.GetAsync( ct );
		foreach ( var client in InnerTubeClients.Ordered( CaptionClients ) )
		{
			try
			{
				var player = await PostPlayer( client, id, visitor, ct );
				var baseUrl = CaptionTrackNodes( player ).FirstOrDefault( t => t["vssId"].Str() == trackId )?["baseUrl"].Str();
				if ( string.IsNullOrEmpty( baseUrl ) ) continue;

				using var r = await Http.RequestAsync( baseUrl + "&fmt=json3", cancellationToken: ct );
				if ( (int)r.StatusCode != 200 ) continue;

				var body = await r.Content.ReadAsStringAsync( ct );
				if ( !string.IsNullOrWhiteSpace( body ) ) return body;
			}
			catch ( Exception e ) when ( e is not OperationCanceledException )
			{
				Log.Trace( $"[bimp] youtube {id} captions {client.Key}: {e.Message}" );
			}
		}

		return null;
	}

	public async Task<StreamPlan> GetStreamsAsync( string id, StreamRequest request, CancellationToken ct )
	{
		var (player, plan) = await Player( id, ct, verifyDownload: true, request );
		if ( plan.Kind == StreamKind.Live ) return plan;

		// 4K above 30 fps is more than the engine's VP9 decoder keeps up with: play the AV1 copy if there's one, for as
		// long as YouTube serves it (~88 MB), then carry on with VP9 1440p60 (see WebmSegmenter)
		var formats = ParseFormats( player );
		if ( MediaSettings.PreferAv1 && plan.Kind == StreamKind.Merge
			&& formats.FirstOrDefault( f => f.Id == plan.Video.FormatId ) is { } vp9 && FormatSelector.TooHeavyForVp9( vp9 ) )
		{
			var lower = FormatSelector.SwitchLike( formats, vp9 );
			if ( await Av1Video( id, vp9, ct ) is { } av1 )
			{
				plan.FallbackVideo = plan.Video;
				plan.Video = av1;
				plan.SwitchVideo = lower?.ToFile();
			}
			else if ( lower is not null )
			{
				// no AV1 we can use (some videos only have HDR AV1): VP9 4K60 froze outright (measured), 1440p60 plays smoothly
				Log.Info( $"[bimp] youtube {id}: no usable AV1 copy - playing {lower.Height}p{lower.Fps:0} instead of {vp9.Height}p{vp9.Fps:0}" );
				plan.Video = lower.ToFile();
			}
		}
		return plan;
	}

	/// <summary> Clients that get YouTube's AV1 formats (visionos doesn't). The audio still comes from the first client. </summary>
	const string Av1Clients = "android_vr,android";

	/// <summary>
	/// The AV1 (fragmented MP4) copy of a VP9 format - same height and frame rate, 8-bit SDR - from a client whose
	/// urls actually download. Null if there's none.
	/// </summary>
	async Task<StreamFile> Av1Video( string id, MediaFormat like, CancellationToken ct )
	{
		var visitor = await VisitorData.GetAsync( ct );
		foreach ( var client in InnerTubeClients.Ordered( Av1Clients ) )
		{
			try
			{
				var player = await PostPlayer( client, id, visitor, ct );
				if ( player?["playabilityStatus"]?["status"].Str() != "OK" ) continue;
				if ( FormatSelector.Av1Like( ParseFormats( player ), like ) is not { } av1 ) continue;

				// a 1 byte probe passes for urls that are refused later - ask for as much as the header read will
				if ( !await CanDownload( av1.Url, ct, Media.WebmSource.ProbeBytes ) ) continue;

				Log.Info( $"[bimp] youtube {id}: {like.Height}p{like.Fps:0} as AV1 (itag {av1.Id} from {client.Key}) - the engine's VP9 decoder can't keep up" );
				return av1.ToFile();
			}
			catch ( Exception e ) when ( e is not OperationCanceledException )
			{
				Log.Trace( $"[bimp] youtube {client.Key} av1: {e.Message}" );
			}
		}
		return null;
	}

	/// <summary>
	/// Ask each InnerTube client in turn, until one gives playable formats (and, with verifyDownload, one whose
	/// files can actually be downloaded - some clients get urls that need a proof-of-origin token).
	/// </summary>
	async Task<(JsonNode player, StreamPlan plan)> Player( string id, CancellationToken ct, bool verifyDownload, StreamRequest request = null )
	{
		var visitor = await VisitorData.GetAsync( ct );

		// the most informative failure across clients: what YouTube said about the video beats a transport error or a refused url
		string bestReason = null;
		var bestRank = -1;
		void Note( string reason, int rank )
		{
			if ( rank > bestRank ) { bestReason = reason; bestRank = rank; }
		}

		// a dub needs a client that lists it: those come after the configured ones, and a plan without the dub is only
		// kept as the fallback
		var lang = request?.Language;
		var clients = InnerTubeClients.Ordered( MediaSettings.YouTubeClients );
		if ( !string.IsNullOrWhiteSpace( lang ) ) clients.AddRange( InnerTubeClients.Ordered( DubClients ).Where( c => !clients.Contains( c ) ) );
		(JsonNode player, StreamPlan plan)? withoutDub = null;

		// a bot check is often tied to a stale visitor id: retry the whole chain once with a fresh one
		for ( var attempt = 0; attempt < 2; attempt++ )
		{
			if ( attempt > 0 )
			{
				Log.Trace( $"[bimp] youtube {id}: bot check, retrying with a fresh visitor id" );
				VisitorData.Invalidate();
				visitor = await VisitorData.GetAsync( ct );
				bestReason = null;
				bestRank = -1;
			}

			foreach ( var client in clients )
			{
				ct.ThrowIfCancellationRequested();

				JsonNode player;
				try
				{
					player = await PostPlayer( client, id, visitor, ct );
				}
				catch ( Exception e ) when ( e is not OperationCanceledException )
				{
					Note( $"YouTube request failed: {e.Message}", 0 );
					continue;
				}

				var status = player?["playabilityStatus"]?["status"].Str();
				if ( status != "OK" )
				{
					var reason = player?["playabilityStatus"]?["reason"].Str() ?? status ?? "unknown error";
					Log.Trace( $"[bimp] youtube {client.Key}: {status} {reason}" );
					Note( reason, 3 );
					continue;
				}

				// live: its HLS playlist (H.264 TS), followed like any live HLS stream
				if ( player["videoDetails"]?["isLive"].Bool() == true )
				{
					if ( player["streamingData"]?["hlsManifestUrl"].Str() is { } hls ) return (player, new StreamPlan { Kind = StreamKind.Live, DirectUrl = hls });
					Note( "YouTube didn't offer this live stream.", 1 );
					continue;
				}

				var formats = ParseFormats( player );
				if ( formats.Count == 0 )
				{
					Note( "YouTube didn't return any playable formats.", 1 );
					continue;
				}

				if ( !verifyDownload ) return (player, null);

				StreamPlan plan;
				try
				{
					plan = FormatSelector.Plan( formats, request );
				}
				catch ( ResolveException e )
				{
					Note( e.Message, 2 );
					continue;
				}

				var probe = plan.Audio?.Url ?? plan.Video?.Url ?? plan.DirectUrl;
				if ( !await CanDownload( probe, ct ) )
				{
					Log.Trace( $"[bimp] youtube {client.Key}: urls refused, trying the next client" );
					Note( "YouTube refused the stream.", 1 );
					continue;
				}

				if ( !FormatSelector.HasLanguage( formats, lang ) )
				{
					Log.Trace( $"[bimp] youtube {client.Key}: no {lang} audio, trying the next client" );
					withoutDub ??= (player, plan);
					continue;
				}

				if ( !string.IsNullOrWhiteSpace( lang ) ) Log.Info( $"[bimp] youtube {id}: {lang} audio from {client.Key}" );
				return (player, plan);
			}

			// nobody has the dub: the original audio it is
			if ( withoutDub is { } original ) return original;

			if ( bestReason is null || !bestReason.Contains( "not a bot", StringComparison.OrdinalIgnoreCase ) ) break;
		}

		throw new ResolveException( FriendlyReason( bestReason ) );
	}

	/// <summary> Log the formats each InnerTube client gets for a video, and whether its urls download. </summary>
	[ConCmd( "bimp_yt_formats", Help = "Log the formats each YouTube client gets for a video id (and whether they download)" )]
	public static void FormatsCmd( string id, string clients = "visionos,android_vr,ios,android" ) => _ = LogFormats( id, clients );

	static async Task LogFormats( string id, string clients )
	{
		var visitor = await VisitorData.GetAsync( CancellationToken.None );
		foreach ( var client in InnerTubeClients.Ordered( clients ) )
		{
			try
			{
				var player = await PostPlayer( client, id, visitor, CancellationToken.None );
				var formats = ParseFormats( player );
				var top = formats.Where( f => f.HasVideo ).OrderByDescending( f => f.Height ).FirstOrDefault();
				var downloads = top is not null && await CanDownload( top.Url, CancellationToken.None );
				var rows = formats.Where( f => f.HasVideo && f.Height >= 1080 ).OrderByDescending( f => f.Height ).ThenByDescending( f => f.Bitrate )
					.Select( f => $"{f.Id,4} {f.Container,-5} {f.VideoCodec,-16} {f.Height,4}p {f.Fps,3:0}fps {f.Bitrate / 1000,6}kb/s{(f.Hdr ? " HDR" : "")}" );
				var tracks = FormatSelector.AudioTracks( formats );
				Log.Info( $"[bimp] youtube {id} {client.Key}: {player?["playabilityStatus"]?["status"].Str()}, {formats.Count} formats, {tracks.Count} audio tracks, top video downloads: {downloads}\n{string.Join( "\n", rows )}" );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[bimp] youtube {id} {client.Key}: {e.Message}" );
			}
		}
	}

	/// <summary> [probe] A live stream's HLS playlist: does it carry low latency tags, how long are its segments? </summary>
	[ConCmd( "bimp_yt_live", Help = "[probe] Log what a YouTube live stream's HLS manifest offers (variants, segment length, low latency tags)" )]
	public static void LiveCmd( string id ) => _ = LogLive( id );

	static async Task LogLive( string id )
	{
		try
		{
			var (player, plan) = await new YouTubeExtractor().Player( id, CancellationToken.None, verifyDownload: false );
			if ( plan?.Kind != StreamKind.Live ) { Log.Info( $"[bimp] youtube {id}: not live" ); return; }

			var master = await Http.RequestStringAsync( plan.DirectUrl );
			var lines = master.Split( '\n' ).Select( l => l.Trim() ).ToList();
			var variants = lines.Where( l => l.StartsWith( "#EXT-X-STREAM-INF" ) ).Select( l => Regex.Match( l, "RESOLUTION=(\\d+x\\d+)" ).Groups[1].Value + " " + Regex.Match( l, "CODECS=\"([^\"]*)\"" ).Groups[1].Value ).ToList();
			var first = lines.FirstOrDefault( l => l.StartsWith( "http" ) );
			var media = first is null ? "" : await Http.RequestStringAsync( first );
			var tags = Regex.Matches( media, "#EXT[A-Z0-9-]*" ).Select( m => m.Value ).GroupBy( t => t ).Select( g => $"{g.Key}x{g.Count()}" );
			var head = string.Join( " / ", media.Split( '\n' ).Take( 14 ).Select( l => l.Length > 110 ? l[..110] : l.Trim() ) );
			Log.Info( $"[bimp] youtube live {id}: master tags: {string.Join( ",", lines.Where( l => l.StartsWith( "#EXT" ) ).Select( l => l.Split( ':' )[0] ).Distinct() )}\nvariants ({variants.Count}): {string.Join( " | ", variants.Take( 8 ) )}\nmedia playlist tags: {string.Join( ", ", tags )}\nhead: {head}" );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[bimp] youtube live {id}: {e.Message}" );
		}
	}

	/// <summary> [probe] Which clients' player responses list caption tracks, and do the caption urls download? </summary>
	[ConCmd( "bimp_yt_captions", Help = "[probe] Log the caption tracks each YouTube client lists for a video id, and whether the first one downloads" )]
	public static void CaptionsCmd( string id, string clients = "visionos,android_vr,ios,android,web" ) => _ = LogCaptions( id, clients );

	static async Task LogCaptions( string id, string clients )
	{
		var visitor = await VisitorData.GetAsync( CancellationToken.None );
		var all = InnerTubeClients.All.Append( InnerTubeClients.Web ).ToList();
		foreach ( var key in clients.Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var client = all.FirstOrDefault( c => c.Key == key );
			if ( client is null ) continue;
			try
			{
				var player = await PostPlayer( client, id, visitor, CancellationToken.None );
				var tracks = player?["captions"]?.Find( "captionTracks" ).Items().ToList() ?? new List<JsonNode>();
				var list = string.Join( ", ", tracks.Select( t => $"{t["vssId"].Str()}{(t["kind"].Str() == "asr" ? "(asr)" : "")}" ) );
				var result = "";
				if ( tracks.Count > 0 && tracks[0]["baseUrl"].Str() is { } baseUrl )
				{
					using var r = await Http.RequestAsync( baseUrl + "&fmt=json3" );
					var body = await r.Content.ReadAsStringAsync();
					result = $" | first track: {(int)r.StatusCode}, {body.Length} chars: {body[..Math.Min( 160, body.Length )]}";
				}
				Log.Info( $"[bimp] youtube {id} {client.Key}: {player?["playabilityStatus"]?["status"].Str()}, {tracks.Count} caption tracks [{list}]{result}" );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[bimp] youtube {id} {key} captions: {e.Message}" );
			}
		}
	}

	/// <summary> [probe] Download the first megabytes of one format to FileSystem.Data lab/, as a client gets it. </summary>
	[ConCmd( "bimp_yt_fetch", Help = "[probe] Download part of a YouTube format: id itag client megabytes [from MB] [chunks of 8 MB: 1 = one request]" )]
	public static void FetchCmd( string id, string itag, string client = "android_vr", int megabytes = 40, int fromMegabytes = 0, int chunked = 0 ) => _ = Fetch( id, itag, client, megabytes, fromMegabytes, chunked != 0 );

	static async Task Fetch( string id, string itag, string clientKey, int megabytes, int fromMegabytes, bool chunked )
	{
		try
		{
			var client = InnerTubeClients.Ordered( clientKey )[0];
			var player = await PostPlayer( client, id, await VisitorData.GetAsync( CancellationToken.None ), CancellationToken.None );
			var f = ParseFormats( player ).FirstOrDefault( x => x.Id == itag ) ?? throw new ResolveException( $"no itag {itag}" );
			var timer = System.Diagnostics.Stopwatch.StartNew();
			long from = fromMegabytes * 1048576L, to = from + megabytes * 1048576L, got = 0;
			var statuses = new List<int>();
			for ( var pos = from; pos < to; )
			{
				var end = chunked ? Math.Min( to, pos + 8 * 1048576 ) : to;
				using var r = await Http.RequestAsync( f.Url, headers: new() { ["Range"] = $"bytes={pos}-{end - 1}" } );
				statuses.Add( (int)r.StatusCode );
				if ( (int)r.StatusCode != 206 ) break;
				var data = await r.Content.ReadAsByteArrayAsync();
				if ( fromMegabytes == 0 && pos == 0 )
				{
					FileSystem.Data.CreateDirectory( "lab" );
					FileSystem.Data.WriteAllBytes( $"lab/yt_{id}_{itag}.{f.Container}", data );
				}
				got += data.Length;
				pos = end;
			}
			Log.Info( $"[bimp] fetched {got / 1048576.0:0.0} MB from {fromMegabytes} MB of {id} itag {itag} ({f.VideoCodec ?? f.AudioCodec}, {f.Size / 1048576} MB) with {client.Key} in {timer.Elapsed.TotalSeconds:0.0}s: {string.Join( " ", statuses )}" );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[bimp] fetch {id} {itag}: {e.Message}" );
		}
	}

	static string FriendlyReason( string reason )
	{
		if ( string.IsNullOrWhiteSpace( reason ) ) return "YouTube couldn't play this video.";
		bool Has( string text ) => reason.Contains( text, StringComparison.OrdinalIgnoreCase );
		if ( Has( "confirm your age" ) || Has( "age-restricted" ) || Has( "inappropriate for some users" ) ) return "This video is age-restricted.";
		if ( Has( "not a bot" ) ) return "YouTube is blocking requests right now (bot check). Try again later.";
		if ( Has( "private video" ) ) return "This video is private.";
		if ( Has( "members-only" ) || Has( "members only" ) || Has( "join this channel" ) ) return "This video is for channel members only.";
		if ( Has( "not available in your country" ) || Has( "blocked it in your country" ) || Has( "uploader has not made this video available" ) ) return "This video isn't available in your region.";
		if ( Has( "premieres in" ) || Has( "will begin in" ) || Has( "scheduled" ) ) return "This live stream or premiere hasn't started yet.";
		if ( Has( "copyright" ) ) return "This video was removed over a copyright claim.";
		if ( Has( "video unavailable" ) || Has( "is unavailable" ) || Has( "no longer available" ) || Has( "has been removed" ) || Has( "does not exist" ) ) return "This video is unavailable or was removed.";
		if ( Has( "sign in" ) ) return "YouTube wants a sign-in for this video (private, age-restricted or members-only).";
		return reason.StartsWith( "YouTube" ) ? reason : $"YouTube: {reason}";
	}

	static async Task<bool> CanDownload( string url, CancellationToken ct, int bytes = 1 )
	{
		if ( string.IsNullOrEmpty( url ) ) return false;
		try
		{
			using var r = await Http.RequestAsync( url, headers: new() { ["Range"] = $"bytes=0-{bytes - 1}" }, cancellationToken: ct );
			return (int)r.StatusCode is 200 or 206;
		}
		catch ( Exception e ) when ( e is not OperationCanceledException )
		{
			return false;
		}
	}

	static async Task<JsonNode> PostPlayer( InnerTubeClient client, string videoId, string visitor, CancellationToken ct )
	{
		var context = client.BuildContext();
		if ( !string.IsNullOrEmpty( visitor ) ) context["client"]["visitorData"] = visitor;

		var body = new JsonObject
		{
			["context"] = context,
			["videoId"] = videoId,
			["playbackContext"] = new JsonObject { ["contentPlaybackContext"] = new JsonObject { ["html5Preference"] = "HTML5_PREF_WANTS" } },
			["contentCheckOk"] = true,
			["racyCheckOk"] = true,
		};

		return await PostJson( PlayerUrl, body, client, visitor, ct );
	}

	static async Task<JsonNode> PostJson( string url, JsonObject body, InnerTubeClient client, string visitor, CancellationToken ct )
	{
		var headers = new Dictionary<string, string>
		{
			["X-YouTube-Client-Name"] = client.Id.ToString(),
			["X-YouTube-Client-Version"] = client.Version,
		};
		if ( !string.IsNullOrEmpty( visitor ) ) headers["X-Goog-Visitor-Id"] = visitor;

		// one retry for a transient failure (network error, 429, 5xx); a 4xx is final. No await inside a catch (sandbox).
		var json = body.ToJsonString();
		var status = 0;
		string failure = null;
		for ( var attempt = 0; attempt < 2; attempt++ )
		{
			if ( attempt > 0 ) await GameTask.Delay( 400, ct );

			try
			{
				using var content = new StringContent( json, Encoding.UTF8, "application/json" );
				using var response = await Http.RequestAsync( url, "POST", content, headers, ct );
				status = (int)response.StatusCode;
				var text = await response.Content.ReadAsStringAsync( ct );
				if ( status == 200 ) return JsonNode.Parse( text );
				failure = $"YouTube returned {status}";
				if ( status != 429 && status < 500 ) break;
			}
			catch ( Exception e ) when ( e is not OperationCanceledException and not ResolveException )
			{
				failure = $"YouTube request failed: {e.Message}";
			}
		}

		throw new ResolveException( failure ?? "YouTube didn't answer." );
	}

	/// <summary> The first video of a playlist, through the "next" endpoint. </summary>
	async Task<string> FirstPlaylistVideo( string playlistId, CancellationToken ct )
	{
		var next = await PlaylistNext( playlistId, null, ct );
		var id = next?.Find( "playlistPanelVideoRenderer" )?["videoId"].Str() ?? next?.Find( "videoId" ).Str();
		if ( id is not null && IdPattern.IsMatch( id ) ) return id;

		throw new ResolveException( "Couldn't read that YouTube playlist." );
	}

	async Task<JsonNode> PlaylistNext( string playlistId, string videoId, CancellationToken ct )
	{
		var client = InnerTubeClients.Web;
		var visitor = await VisitorData.GetAsync( ct );
		var context = client.BuildContext();
		if ( !string.IsNullOrEmpty( visitor ) ) context["client"]["visitorData"] = visitor;
		var body = new JsonObject { ["context"] = context, ["playlistId"] = playlistId };
		if ( videoId is not null ) body["videoId"] = videoId;

		try
		{
			return await PostJson( NextUrl, body, client, visitor, ct );
		}
		catch ( Exception e ) when ( e is not OperationCanceledException )
		{
			Log.Trace( $"[bimp] youtube playlist {playlistId}: {e.Message}" );
			throw new ResolveException( "Couldn't read that YouTube playlist." );
		}
	}

	/// <summary>
	/// The videos of a playlist link (<c>playlist?list=</c>), or of a Mix / radio link (a video with an <c>RD...</c> list:
	/// that video, then what the mix brings). Null for anything else - a video from an ordinary playlist plays alone.
	/// The "next" endpoint answers with the playlist's panel, one renderer per video (long playlists are cut off there).
	/// </summary>
	public async Task<List<PlaylistEntry>> GetPlaylistAsync( Uri url, int max, CancellationToken ct )
	{
		var videoId = ParseVideoId( url, out var playlistId );
		if ( videoId is not null )
		{
			playlistId = System.Web.HttpUtility.ParseQueryString( url.Query )["list"];
			if ( playlistId is null || !playlistId.StartsWith( "RD", StringComparison.Ordinal ) ) return null;
		}
		else if ( string.IsNullOrEmpty( playlistId ) ) return null;

		var next = await PlaylistNext( playlistId, videoId, ct );
		var entries = new List<PlaylistEntry>();
		var ids = new List<string>();

		foreach ( var r in next.FindAll( "playlistPanelVideoRenderer" ) )
		{
			var id = r["videoId"].Str();
			if ( id is null || !IdPattern.IsMatch( id ) || ids.Contains( id ) ) continue;

			var title = r["title"]?["simpleText"].Str() ?? r["title"]?["runs"]?[0]?["text"].Str() ?? id;
			entries.Add( new PlaylistEntry( $"https://www.youtube.com/watch?v={id}", title, ParseClock( r["lengthText"]?["simpleText"].Str() ) ) );
			ids.Add( id );
		}

		if ( videoId is not null )
		{
			// a mix starts with the video it was made from - whatever the panel shows before it is history
			var at = ids.IndexOf( videoId );
			if ( at > 0 ) entries.RemoveRange( 0, at );
			else if ( at < 0 ) entries.Insert( 0, new PlaylistEntry( url.ToString(), videoId, 0 ) );
			// the link itself, so its start time (t=) still applies
			entries[0] = entries[0] with { Url = url.ToString() };
		}

		if ( entries.Count == 0 ) throw new ResolveException( "That YouTube playlist is empty or private." );
		return entries.Take( max ).ToList();
	}

	/// <summary> "1:02:03" / "3:45" in seconds, 0 if it isn't one. </summary>
	static float ParseClock( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) return 0;

		double seconds = 0;
		foreach ( var part in text.Split( ':' ) )
		{
			if ( !int.TryParse( part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n ) ) return 0;
			seconds = seconds * 60 + n;
		}
		return (float)seconds;
	}

	static readonly Regex MimePattern = new( "^(?<type>audio|video)/(?<container>[a-z0-9]+)(?:;\\s*codecs=\"(?<codecs>[^\"]*)\")?", RegexOptions.Compiled );

	public static List<MediaFormat> ParseFormats( JsonNode player )
	{
		var result = new List<MediaFormat>();
		var sd = player?["streamingData"];
		if ( sd is null ) return result;

		foreach ( var f in sd["formats"].Items().Concat( sd["adaptiveFormats"].Items() ) )
		{
			var url = f["url"].Str();
			if ( string.IsNullOrEmpty( url ) ) continue; // ciphered - would need the player's JavaScript

			var m = MimePattern.Match( f["mimeType"].Str() ?? "" );
			if ( !m.Success ) continue;

			var codecs = m.Groups["codecs"].Value.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
			string vcodec = null, acodec = null;
			if ( m.Groups["type"].Value == "audio" )
			{
				acodec = codecs.FirstOrDefault() ?? "unknown";
			}
			else
			{
				vcodec = codecs.FirstOrDefault() ?? "unknown";
				if ( codecs.Length > 1 ) acodec = codecs[1];
			}

			var transfer = f["colorInfo"]?["transferCharacteristics"].Str() ?? "";
			var track = f["audioTrack"];
			var trackId = track?["id"].Str();

			result.Add( new MediaFormat
			{
				Id = f["itag"].Str(),
				Url = url,
				Container = m.Groups["container"].Value,
				VideoCodec = vcodec?.ToLowerInvariant(),
				AudioCodec = acodec?.ToLowerInvariant(),
				Height = f["height"].Int(),
				Fps = f["fps"].Float(),
				Bitrate = f["averageBitrate"].Long() is > 0 and var avg ? avg : f["bitrate"].Long(),
				Size = f["contentLength"].Long(),
				Hdr = (f["qualityLabel"].Str() ?? "").Contains( "HDR" ) || transfer.Contains( "2084" ) || transfer.Contains( "B67" ),
				Drc = f["isDrc"].Bool(),
				Language = trackId?.Split( '.' )[0],
				LanguageName = track?["displayName"].Str(),
				LanguageDefault = track?["audioIsDefault"].Bool() ?? false,
			} );
		}

		return result;
	}

	/// <summary>
	/// YouTube's anonymous visitor id. Without it some clients get "Sign in to confirm you're not a bot".
	/// Fetched once from an embed page and reused for an hour.
	/// </summary>
	static class VisitorData
	{
		static string value;
		static RealTimeSince sinceFetched;
		static Task<string> pending;

		static readonly Regex Pattern = new( "\"VISITOR_DATA\":\"([^\"]+)\"", RegexOptions.Compiled );

		public static Task<string> GetAsync( CancellationToken ct )
		{
			// a failed fetch (empty) is retried after a minute, not an hour
			if ( value is not null && sinceFetched < (value.Length > 0 ? 3600 : 60) ) return Task.FromResult( value );
			return pending ??= Fetch();
		}

		/// <summary> Drop the cached id so the next GetAsync fetches a fresh one (after a bot check). </summary>
		public static void Invalidate() => value = null;

		static async Task<string> Fetch()
		{
			try
			{
				var html = await Http.RequestStringAsync( "https://www.youtube.com/embed/aqz-KE-bpKQ" );
				var m = Pattern.Match( html );
				value = m.Success ? m.Groups[1].Value : "";
			}
			catch ( Exception e )
			{
				Log.Warning( $"[bimp] Couldn't get YouTube visitor data: {e.Message}" );
				value = "";
			}
			finally
			{
				pending = null;
			}

			sinceFetched = 0;
			return value;
		}
	}
}
