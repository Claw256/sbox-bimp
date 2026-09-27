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
public sealed class YouTubeExtractor : IExtractor
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

	public async Task<MediaInfo> GetInfoAsync( Uri url, bool audioOnly, CancellationToken ct )
	{
		var id = ParseVideoId( url, out var playlistId );
		if ( id is null && !string.IsNullOrEmpty( playlistId ) )
			id = await FirstPlaylistVideo( playlistId, ct );
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
			AudioTracks = FormatSelector.AudioTracks( formats ),
		};
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
		string firstReason = null;

		foreach ( var client in InnerTubeClients.Ordered( MediaSettings.YouTubeClients ) )
		{
			ct.ThrowIfCancellationRequested();

			JsonNode player;
			try
			{
				player = await PostPlayer( client, id, visitor, ct );
			}
			catch ( Exception e ) when ( e is not OperationCanceledException )
			{
				firstReason ??= $"YouTube request failed: {e.Message}";
				continue;
			}

			var status = player?["playabilityStatus"]?["status"].Str();
			if ( status != "OK" )
			{
				var reason = player?["playabilityStatus"]?["reason"].Str() ?? status ?? "unknown error";
				Log.Trace( $"[bimp] youtube {client.Key}: {status} {reason}" );
				firstReason ??= reason;
				continue;
			}

			// live: its HLS playlist (H.264 TS), followed like any live HLS stream
			if ( player["videoDetails"]?["isLive"].Bool() == true )
			{
				if ( player["streamingData"]?["hlsManifestUrl"].Str() is { } hls ) return (player, new StreamPlan { Kind = StreamKind.Live, DirectUrl = hls });
				firstReason ??= "YouTube didn't offer this live stream.";
				continue;
			}

			var formats = ParseFormats( player );
			if ( formats.Count == 0 )
			{
				firstReason ??= "YouTube didn't return any playable formats.";
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
				firstReason ??= e.Message;
				continue;
			}

			var probe = plan.Audio?.Url ?? plan.Video?.Url ?? plan.DirectUrl;
			if ( !await CanDownload( probe, ct ) )
			{
				Log.Trace( $"[bimp] youtube {client.Key}: urls refused, trying the next client" );
				firstReason ??= "YouTube refused the stream.";
				continue;
			}

			return (player, plan);
		}

		throw new ResolveException( FriendlyReason( firstReason ) );
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
				Log.Info( $"[bimp] youtube {id} {client.Key}: {player?["playabilityStatus"]?["status"].Str()}, {formats.Count} formats, top video downloads: {downloads}\n{string.Join( "\n", rows )}" );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[bimp] youtube {id} {client.Key}: {e.Message}" );
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
		if ( reason.Contains( "confirm your age", StringComparison.OrdinalIgnoreCase ) ) return "This video is age-restricted.";
		if ( reason.Contains( "not a bot", StringComparison.OrdinalIgnoreCase ) ) return "YouTube is blocking requests right now (bot check). Try again later.";
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

		using var content = new StringContent( body.ToJsonString(), Encoding.UTF8, "application/json" );
		using var response = await Http.RequestAsync( url, "POST", content, headers, ct );
		var text = await response.Content.ReadAsStringAsync( ct );
		if ( (int)response.StatusCode != 200 )
			throw new ResolveException( $"YouTube returned {(int)response.StatusCode}" );
		return JsonNode.Parse( text );
	}

	/// <summary> The first video of a playlist, through the "next" endpoint. </summary>
	async Task<string> FirstPlaylistVideo( string playlistId, CancellationToken ct )
	{
		var client = InnerTubeClients.Ordered( MediaSettings.YouTubeClients )[0];
		var visitor = await VisitorData.GetAsync( ct );
		var body = new JsonObject { ["context"] = client.BuildContext(), ["playlistId"] = playlistId };

		try
		{
			var next = await PostJson( NextUrl, body, client, visitor, ct );
			var id = next?.Find( "playlistPanelVideoRenderer" )?["videoId"].Str() ?? next?.Find( "videoId" ).Str();
			if ( id is not null && IdPattern.IsMatch( id ) ) return id;
		}
		catch ( Exception e ) when ( e is not OperationCanceledException )
		{
			Log.Trace( $"[bimp] youtube playlist {playlistId}: {e.Message}" );
		}

		throw new ResolveException( "Couldn't read that YouTube playlist." );
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
			if ( value is not null && sinceFetched < 3600 ) return Task.FromResult( value );
			return pending ??= Fetch();
		}

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
