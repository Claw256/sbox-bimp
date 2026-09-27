using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bimp.Resolver.Extractors;

/// <summary>
/// twitch.tv/{channel} (live), twitch.tv/videos/{id} (VOD), twitch.tv/{channel}/clip/{slug} and clips.twitch.tv/{slug}.
/// Uses the web player's public GQL client id and playback access tokens, then the usher HLS playlists (H.264 TS).
/// </summary>
public sealed class TwitchExtractor : SiteExtractor
{
	public override string Key => "twitch";
	protected override string[] Hosts => new[] { "twitch.tv" };

	const string Gql = "https://gql.twitch.tv/gql";
	const string ClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
	static readonly Dictionary<string, string> Headers = new() { ["Client-ID"] = ClientId };

	static readonly string[] NotChannels = { "directory", "videos", "settings", "search", "downloads", "p", "jobs", "turbo", "subscriptions", "inventory", "wallet", "friends", "messages" };
	static readonly Regex Login = new( "^[a-z0-9_]{2,25}$", RegexOptions.IgnoreCase | RegexOptions.Compiled );

	/// <summary> "live:{login}", "vod:{id}" or "clip:{slug}". </summary>
	protected override string ParseId( Uri url )
	{
		var p = url.AbsolutePath.Trim( '/' ).Split( '/', StringSplitOptions.RemoveEmptyEntries );
		var host = url.Host.ToLowerInvariant();
		if ( host.StartsWith( "clips." ) ) return p.Length >= 1 ? $"clip:{p[0]}" : null;
		if ( p.Length >= 2 && p[0] == "videos" && long.TryParse( p[1], out _ ) ) return $"vod:{p[1]}";
		if ( p.Length >= 3 && p[1] == "clip" ) return $"clip:{p[2]}";
		if ( p.Length >= 3 && p[1] == "video" && long.TryParse( p[2], out _ ) ) return $"vod:{p[2]}";
		if ( p.Length >= 1 && Login.IsMatch( p[0] ) && !NotChannels.Contains( p[0].ToLowerInvariant() ) ) return $"live:{p[0].ToLowerInvariant()}";
		return null;
	}

	protected override async Task<SiteMedia> FetchAsync( string id, CancellationToken ct )
	{
		var kind = id[..id.IndexOf( ':' )];
		var value = id[(kind.Length + 1)..];
		return kind switch
		{
			"live" => await Live( value, ct ),
			"vod" => await Vod( value, ct ),
			_ => await Clip( value, ct ),
		};
	}

	static Task<JsonNode> Query( string query, JsonObject variables, CancellationToken ct )
		=> PostJson( Gql, new JsonObject { ["query"] = query, ["variables"] = variables }, ct, Headers );

	static async Task<SiteMedia> Live( string login, CancellationToken ct )
	{
		var j = await Query( "query($login: String!) { user(login: $login) { displayName stream { title } } streamPlaybackAccessToken(channelName: $login, params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: \"site\"}) { value signature } }",
			new JsonObject { ["login"] = login }, ct );
		var user = j?["data"]?["user"];
		if ( user is null ) throw new ResolveException( "That Twitch channel doesn't exist." );
		if ( user["stream"] is not JsonObject stream ) throw new ResolveException( $"{user["displayName"].Str() ?? login} isn't live right now." );
		var token = j["data"]["streamPlaybackAccessToken"];
		return new SiteMedia
		{
			Title = $"{user["displayName"].Str()}: {stream["title"].Str()}",
			IsLive = true,
			Hls = $"https://usher.ttvnw.net/api/channel/hls/{login}.m3u8?{Usher( token )}",
		};
	}

	static async Task<SiteMedia> Vod( string id, CancellationToken ct )
	{
		var j = await Query( "query($id: ID!) { video(id: $id) { title lengthSeconds owner { displayName } } videoPlaybackAccessToken(id: $id, params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: \"site\"}) { value signature } }",
			new JsonObject { ["id"] = id }, ct );
		var video = j?["data"]?["video"] ?? throw new ResolveException( "That Twitch video doesn't exist (or is subscriber only)." );
		return new SiteMedia
		{
			Title = $"{video["owner"]?["displayName"].Str()}: {video["title"].Str()}",
			Duration = video["lengthSeconds"].Float(),
			Hls = $"https://usher.ttvnw.net/vod/{id}.m3u8?{Usher( j["data"]["videoPlaybackAccessToken"] )}",
		};
	}

	static async Task<SiteMedia> Clip( string slug, CancellationToken ct )
	{
		var j = await Query( "query($slug: ID!) { clip(slug: $slug) { title durationSeconds broadcaster { displayName } videoQualities { quality sourceURL } playbackAccessToken(params: {platform: \"web\", playerBackend: \"mediaplayer\", playerType: \"site\"}) { value signature } } }",
			new JsonObject { ["slug"] = slug }, ct );
		var clip = j?["data"]?["clip"] ?? throw new ResolveException( "That Twitch clip doesn't exist." );
		var token = clip["playbackAccessToken"];
		var query = $"sig={token?["signature"].Str()}&token={Uri.EscapeDataString( token?["value"].Str() ?? "" )}";
		var m = new SiteMedia { Title = $"{clip["broadcaster"]?["displayName"].Str()}: {clip["title"].Str()}", Duration = clip["durationSeconds"].Float() };
		foreach ( var q in clip["videoQualities"].Items() )
		{
			var url = q["sourceURL"].Str();
			if ( string.IsNullOrEmpty( url ) ) continue;
			m.Files.Add( new MediaFormat { Id = q["quality"].Str(), Url = $"{url}?{query}", Container = "mp4", VideoCodec = "h264", AudioCodec = "aac", Height = q["quality"].Int() } );
		}
		return m;
	}

	static string Usher( JsonNode token )
	{
		if ( token?["value"].Str() is not { } value ) throw new ResolveException( "Twitch didn't hand out a playback token." );
		return $"sig={token["signature"].Str()}&token={Uri.EscapeDataString( value )}&allow_source=true&allow_audio_only=true&player=twitchweb&p={Random.Shared.Next( 1000000, 9999999 )}";
	}
}

/// <summary>
/// kick.com/{channel} (live) and kick.com/{channel}/videos/{uuid} (VOD): the public channel and video APIs, then
/// their HLS (Amazon IVS, H.264 TS).
/// </summary>
public sealed class KickExtractor : SiteExtractor
{
	public override string Key => "kick";
	protected override string[] Hosts => new[] { "kick.com" };

	static readonly Regex Slug = new( "^[a-z0-9_-]{2,40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled );
	static readonly Regex Uuid = new( "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.Compiled );
	static readonly string[] NotChannels = { "categories", "category", "search", "following", "browse", "dashboard", "terms-of-service", "privacy-policy" };

	/// <summary> "live:{slug}" or "vod:{uuid}". </summary>
	protected override string ParseId( Uri url )
	{
		var p = url.AbsolutePath.Trim( '/' ).Split( '/', StringSplitOptions.RemoveEmptyEntries );
		var video = p.FirstOrDefault( s => Uuid.IsMatch( s ) );
		if ( video is not null && p.Any( s => s is "videos" or "video" ) ) return $"vod:{video}";
		if ( p.Length == 1 && Slug.IsMatch( p[0] ) && !NotChannels.Contains( p[0].ToLowerInvariant() ) ) return $"live:{p[0].ToLowerInvariant()}";
		return null;
	}

	protected override async Task<SiteMedia> FetchAsync( string id, CancellationToken ct )
	{
		var value = id[(id.IndexOf( ':' ) + 1)..];
		if ( id.StartsWith( "vod:" ) )
		{
			var v = await GetJson( $"https://kick.com/api/v1/video/{value}", ct );
			var source = v?["source"].Str() ?? throw new ResolveException( "That Kick video can't be played." );
			return new SiteMedia
			{
				Title = v["livestream"]?["session_title"].Str() ?? "Kick video",
				Duration = (v["livestream"]?["duration"]).Long() / 1000f,
				Hls = source,
			};
		}

		var c = await GetJson( $"https://kick.com/api/v2/channels/{value}", ct );
		if ( c?["livestream"] is not JsonObject live || c["playback_url"].Str() is not { } playback )
			throw new ResolveException( $"{c?["user"]?["username"].Str() ?? value} isn't live right now." );
		return new SiteMedia { Title = $"{c["user"]?["username"].Str() ?? value}: {live["session_title"].Str()}", IsLive = true, Hls = playback };
	}
}

/// <summary> x.com / twitter.com/{user}/status/{id}: the embed (syndication) API's mp4 variants. </summary>
public sealed class XExtractor : SiteExtractor
{
	public override string Key => "x";
	protected override string[] Hosts => new[] { "x.com", "twitter.com", "fxtwitter.com", "vxtwitter.com" };

	static readonly Regex Status = new( "/status(?:es)?/(\\d{5,25})", RegexOptions.Compiled );
	static readonly Regex Size = new( "/(\\d{2,5})x(\\d{2,5})/", RegexOptions.Compiled );

	protected override string ParseId( Uri url )
	{
		var m = Status.Match( url.AbsolutePath );
		return m.Success ? m.Groups[1].Value : null;
	}

	protected override async Task<SiteMedia> FetchAsync( string id, CancellationToken ct )
	{
		// any token works; it's just required
		var j = await GetJson( $"https://cdn.syndication.twimg.com/tweet-result?id={id}&token=a&lang=en", ct );
		if ( j?["__typename"].Str() == "TweetTombstone" ) throw new ResolveException( "That post isn't available." );
		var video = j?["mediaDetails"].Items().FirstOrDefault( d => d["video_info"] is not null )?["video_info"]
			?? throw new ResolveException( "That post has no video." );

		var text = j["text"].Str() ?? "";
		var m = new SiteMedia
		{
			Title = $"{j["user"]?["name"].Str()}: {(text.Length > 80 ? text[..80] + "..." : text)}",
			Duration = video["duration_millis"].Long() / 1000f,
		};
		foreach ( var v in video["variants"].Items() )
		{
			var url = v["url"].Str();
			if ( v["content_type"].Str() == "application/x-mpegURL" ) { m.Hls ??= url; continue; }
			if ( v["content_type"].Str() != "video/mp4" || url is null ) continue;
			var size = Size.Match( url );
			m.Files.Add( new MediaFormat { Id = url, Url = url, Container = "mp4", VideoCodec = "h264", AudioCodec = "aac", Height = size.Success ? int.Parse( size.Groups[2].Value ) : 0, Bitrate = v["bitrate"].Long() } );
		}
		return m;
	}
}

/// <summary> archive.org/details/{id}: the item's best mp4 / webm (or, for audio items, mp3 / ogg) file. </summary>
public sealed class ArchiveExtractor : SiteExtractor
{
	public override string Key => "archive";
	protected override string[] Hosts => new[] { "archive.org" };

	static readonly string[] VideoExtensions = { ".mp4", ".webm", ".m4v" };
	static readonly string[] AudioExtensions = { ".mp3", ".ogg", ".m4a", ".flac", ".wav" };

	/// <summary> "{item}" or "{item}/{file}". </summary>
	protected override string ParseId( Uri url )
	{
		var p = url.AbsolutePath.Trim( '/' ).Split( '/', StringSplitOptions.RemoveEmptyEntries );
		if ( p.Length < 2 || p[0] is not ("details" or "embed" or "download") ) return null;
		return p.Length >= 3 && p[0] == "download" ? $"{p[1]}/{string.Join( '/', p.Skip( 2 ).Select( Uri.UnescapeDataString ) )}" : p[1];
	}

	protected override async Task<SiteMedia> FetchAsync( string id, CancellationToken ct )
	{
		var item = id.Split( '/' )[0];
		var wanted = id.Length > item.Length ? id[(item.Length + 1)..] : null;
		var j = await GetJson( $"https://archive.org/metadata/{item}", ct );
		if ( j?["files"] is not JsonArray files ) throw new ResolveException( "That archive.org item doesn't exist (or is dark)." );

		var candidates = files.Select( f => (name: f?["name"].Str(), size: f?["size"].Long() ?? 0, length: Length( f?["length"].Str() ), height: f?["height"].Int() ?? 0) )
			.Where( f => f.name is not null && (wanted is null || f.name == wanted) )
			.ToList();
		string Ext( string n ) => System.IO.Path.GetExtension( n ).ToLowerInvariant();
		var videos = candidates.Where( f => VideoExtensions.Contains( Ext( f.name ) ) ).ToList();
		var audio = candidates.Where( f => AudioExtensions.Contains( Ext( f.name ) ) ).ToList();

		var m = new SiteMedia { Title = j["metadata"]?["title"].Str() ?? item };
		var chosen = videos.Count > 0 ? videos : audio;
		if ( chosen.Count == 0 ) throw new ResolveException( "That archive.org item has no video or audio file the engine can play." );
		m.AudioOnly = videos.Count == 0;
		foreach ( var f in chosen )
		{
			var url = $"https://archive.org/download/{item}/{string.Join( '/', f.name.Split( '/' ).Select( Uri.EscapeDataString ) )}";
			var ext = Ext( f.name ).TrimStart( '.' );
			m.Files.Add( new MediaFormat { Id = f.name, Url = url, Container = ext, VideoCodec = m.AudioOnly ? null : "h264", AudioCodec = "aac", Height = f.height, Bitrate = f.size, Size = f.size } );
			m.Duration = MathF.Max( m.Duration, f.length );
		}
		return m;
	}

	/// <summary> "596.47" or "9:56" / "1:02:03". </summary>
	static float Length( string s )
	{
		if ( string.IsNullOrEmpty( s ) ) return 0;
		if ( !s.Contains( ':' ) ) return float.TryParse( s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f ) ? f : 0;
		return s.Split( ':' ).Aggregate( 0f, ( t, part ) => t * 60 + (float.TryParse( part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v ) ? v : 0) );
	}
}

/// <summary>
/// {artist}.bandcamp.com/track/{slug} (and /album/{slug}: its first track): the page's player data, mp3-128 stream.
/// </summary>
public sealed class BandcampExtractor : SiteExtractor
{
	public override string Key => "bandcamp";
	protected override string[] Hosts => new[] { "bandcamp.com" };

	static readonly Regex TrAlbum = new( "data-tralbum=\"([^\"]+)\"", RegexOptions.Compiled );

	protected override string ParseId( Uri url )
	{
		var p = url.AbsolutePath.Trim( '/' ).Split( '/', StringSplitOptions.RemoveEmptyEntries );
		if ( p.Length < 2 || p[0] is not ("track" or "album") || url.Host.Equals( "bandcamp.com", StringComparison.OrdinalIgnoreCase ) ) return null;
		return $"https://{url.Host.ToLowerInvariant()}/{p[0]}/{p[1]}";
	}

	protected override async Task<SiteMedia> FetchAsync( string id, CancellationToken ct )
	{
		using var r = await Http.RequestAsync( id, cancellationToken: ct );
		if ( (int)r.StatusCode == 404 ) throw new ResolveException( "That Bandcamp page doesn't exist." );
		if ( !r.IsSuccessStatusCode ) throw new ResolveException( $"Bandcamp refused the request ({(int)r.StatusCode})." );
		var html = await r.Content.ReadAsStringAsync( ct );
		var m = TrAlbum.Match( html );
		if ( !m.Success ) throw new ResolveException( "Couldn't read that Bandcamp page." );
		var data = JsonNode.Parse( System.Net.WebUtility.HtmlDecode( m.Groups[1].Value ) );
		var track = data?["trackinfo"].Items().FirstOrDefault( t => t["file"]?["mp3-128"] is not null )
			?? throw new ResolveException( "That Bandcamp release has no free stream (or needs buying)." );
		var url = track["file"]["mp3-128"].Str();
		return new SiteMedia
		{
			Title = $"{data["artist"].Str()} - {track["title"].Str()}",
			Duration = track["duration"].Float(),
			AudioOnly = true,
			Files = { new MediaFormat { Id = "mp3-128", Url = url, Container = "mp3", AudioCodec = "mp3", Bitrate = 128000 } },
		};
	}
}

/// <summary>
/// Any .m3u8 link: a finished playlist plays as a seekable video, one that's still growing as a live stream.
/// </summary>
public sealed class HlsExtractor : IExtractor
{
	public string Key => "hls";

	static readonly string[] Extensions = { ".m3u8", ".m3u" };

	public static bool IsPlaylist( Uri url )
		=> (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && Extensions.Contains( System.IO.Path.GetExtension( url.AbsolutePath ).ToLowerInvariant() );

	public bool CanHandle( Uri url ) => IsPlaylist( url );

	public async Task<MediaInfo> GetInfoAsync( Uri url, bool audioOnly, CancellationToken ct )
	{
		var (live, duration, _) = await Live.HlsReader.ProbeAsync( url, ct );
		var name = Uri.UnescapeDataString( System.IO.Path.GetFileNameWithoutExtension( url.AbsolutePath ) );
		return new MediaInfo
		{
			Extractor = Key,
			Id = url.ToString(),
			Title = name is "index" or "master" or "playlist" or "" ? url.Host : $"{url.Host} - {name}",
			Duration = live ? 0 : (float)duration,
			AudioOnly = audioOnly,
			IsLive = live,
			Merged = !live,
		};
	}

	public async Task<StreamPlan> GetStreamsAsync( string id, StreamRequest request, CancellationToken ct )
	{
		var (live, _, _) = await Live.HlsReader.ProbeAsync( new Uri( id ), ct );
		return new StreamPlan { Kind = live ? StreamKind.Live : StreamKind.Hls, DirectUrl = id };
	}
}
