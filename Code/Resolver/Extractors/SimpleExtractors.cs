using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bimp.Resolver.Extractors;

/// <summary> What a site says about one piece of media. </summary>
public sealed class SiteMedia
{
	public string Title;

	/// <summary> Seconds, 0 if unknown (or live). </summary>
	public float Duration;
	public bool AudioOnly;
	public bool IsLive;

	/// <summary> Progressive files the engine plays straight from the site (mp4, webm, mp3...). </summary>
	public List<MediaFormat> Files = new();

	/// <summary> An HLS playlist (master or media): played when there are no files, and for live media. </summary>
	public string Hls;
}

/// <summary>
/// Shared plumbing for sites: a progressive file when the site has one (played directly, seeked natively), else its
/// HLS playlist - remuxed locally from a start time (VOD, seeks by reopening), or followed at its live edge.
/// </summary>
public abstract class SiteExtractor : IExtractor
{
	public abstract string Key { get; }
	protected abstract string[] Hosts { get; }

	public virtual bool CanHandle( Uri url )
	{
		var host = url.Host.ToLowerInvariant();
		return Hosts.Any( h => host == h || host.EndsWith( "." + h ) ) && ParseId( url ) is not null;
	}

	protected abstract string ParseId( Uri url );

	protected abstract Task<SiteMedia> FetchAsync( string id, CancellationToken ct );

	public async Task<MediaInfo> GetInfoAsync( Uri url, bool audioOnly, CancellationToken ct )
	{
		var id = ParseId( url ) ?? throw new ResolveException( "That link isn't supported." );
		var m = await FetchAsync( id, ct );
		if ( m.Files.Count == 0 && m.Hls is null ) throw new ResolveException( "No playable files found for that link." );

		var heights = m.AudioOnly || audioOnly || m.Files.Count == 0 ? new List<int>()
			: m.Files.Where( f => f.HasVideo ).Select( f => f.Height ).Where( h => h > 0 ).Distinct().OrderByDescending( h => h ).ToList();
		return new MediaInfo
		{
			Extractor = Key,
			Id = id,
			Title = m.Title ?? id,
			Duration = m.IsLive ? 0 : m.Duration,
			AudioOnly = audioOnly || m.AudioOnly,
			IsLive = m.IsLive,
			// HLS VOD is remuxed from a start time, so it seeks by reopening like merged media
			Merged = !m.IsLive && m.Files.Count == 0,
			Qualities = heights.Count > 1 ? heights : new List<int>(),
		};
	}

	public async Task<StreamPlan> GetStreamsAsync( string id, StreamRequest request, CancellationToken ct )
	{
		var m = await FetchAsync( id, ct );
		if ( m.IsLive )
			return new StreamPlan { Kind = StreamKind.Live, DirectUrl = m.Hls ?? m.Files.FirstOrDefault()?.Url ?? throw new ResolveException( "The stream isn't live right now." ) };
		if ( m.Files.Count == 0 )
			return new StreamPlan { Kind = StreamKind.Hls, DirectUrl = m.Hls ?? throw new ResolveException( "No playable files found for that link." ) };

		if ( m.AudioOnly )
		{
			var best = m.Files.OrderByDescending( f => f.Bitrate ).FirstOrDefault() ?? throw new ResolveException( "No playable audio found." );
			return new StreamPlan { Kind = StreamKind.Direct, DirectUrl = best.Url };
		}

		var limit = request.Height > 0 ? request.Height : Math.Clamp( request.MaxHeight, 144, 2160 );
		var pick = m.Files.Where( f => f.Height <= limit ).OrderByDescending( f => f.Height ).ThenByDescending( f => f.Bitrate ).FirstOrDefault()
			?? m.Files.OrderBy( f => f.Height ).FirstOrDefault()
			?? throw new ResolveException( "No playable files found for that link." );
		return new StreamPlan { Kind = StreamKind.Direct, DirectUrl = pick.Url };
	}

	protected static string Https( string url ) => url is not null && url.StartsWith( "//" ) ? "https:" + url : url;

	protected static async Task<JsonNode> GetJson( string url, CancellationToken ct, Dictionary<string, string> headers = null )
	{
		using var r = await Http.RequestAsync( url, headers: headers, cancellationToken: ct );
		return await ReadJson( r, ct );
	}

	protected static async Task<JsonNode> PostJson( string url, JsonNode body, CancellationToken ct, Dictionary<string, string> headers = null )
	{
		using var content = new StringContent( body.ToJsonString(), Encoding.UTF8, "application/json" );
		using var r = await Http.RequestAsync( url, "POST", content, headers, ct );
		return await ReadJson( r, ct );
	}

	static async Task<JsonNode> ReadJson( System.Net.Http.HttpResponseMessage r, CancellationToken ct )
	{
		var status = (int)r.StatusCode;
		if ( status == 404 ) throw new ResolveException( "That media doesn't exist (or is private)." );
		if ( status is 401 or 403 ) throw new ResolveException( "That media is private, or the site refused the request." );
		if ( status != 200 ) throw new ResolveException( $"The site returned {status}." );
		return JsonNode.Parse( await r.Content.ReadAsStringAsync( ct ) );
	}
}

/// <summary>
/// streamable.com/{shortcode}
/// </summary>
public sealed class StreamableExtractor : SiteExtractor
{
	public override string Key => "streamable";
	protected override string[] Hosts => new[] { "streamable.com" };

	static readonly Regex Shortcode = new( "^(?:e/|o/|s/)?([a-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled );

	protected override string ParseId( Uri url )
	{
		var m = Shortcode.Match( url.AbsolutePath.Trim( '/' ) );
		return m.Success ? m.Groups[1].Value : null;
	}

	protected override async Task<SiteMedia> FetchAsync( string id, CancellationToken ct )
	{
		var j = await GetJson( $"https://api.streamable.com/videos/{id}", ct );
		var m = new SiteMedia { Title = j?["title"].Str() };

		if ( j?["files"] is JsonObject files )
		{
			foreach ( var (_, f) in files )
			{
				var url = Https( f?["url"].Str() );
				if ( string.IsNullOrEmpty( url ) ) continue;
				m.Duration = MathF.Max( m.Duration, f["duration"].Float() );
				m.Files.Add( new MediaFormat { Id = url, Url = url, Container = "mp4", VideoCodec = "h264", AudioCodec = "aac", Height = f["height"].Int(), Bitrate = f["bitrate"].Long() } );
			}
		}
		return m;
	}
}

/// <summary>
/// vimeo.com/{id} and player.vimeo.com/video/{id}: its progressive files, else its HLS (newer uploads are often HLS
/// only, with the audio in a separate playlist). Live events play live.
/// </summary>
public sealed class VimeoExtractor : SiteExtractor
{
	public override string Key => "vimeo";
	protected override string[] Hosts => new[] { "vimeo.com" };

	static readonly Regex Id = new( "(?:^|/)(?:video/)?(\\d{5,})(?:/|$)", RegexOptions.Compiled );

	protected override string ParseId( Uri url )
	{
		var m = Id.Match( url.AbsolutePath );
		return m.Success ? m.Groups[1].Value : null;
	}

	protected override async Task<SiteMedia> FetchAsync( string id, CancellationToken ct )
	{
		var j = await GetJson( $"https://player.vimeo.com/video/{id}/config", ct );
		var files = j?["request"]?["files"];
		var m = new SiteMedia
		{
			Title = j?["video"]?["title"].Str(),
			Duration = j?["video"]?["duration"].Float() ?? 0,
			IsLive = j?["video"]?["live_event"] is JsonObject,
		};
		m.Files = files?["progressive"].Items()
			.Select( f => new MediaFormat { Id = f["id"].Str(), Url = f["url"].Str(), Container = "mp4", VideoCodec = "h264", AudioCodec = "aac", Height = f["height"].Int(), Fps = f["fps"].Float() } )
			.Where( f => !string.IsNullOrEmpty( f.Url ) )
			.ToList() ?? new List<MediaFormat>();

		var hls = files?["hls"];
		var cdn = hls?["default_cdn"].Str();
		m.Hls = (cdn is not null ? hls?["cdns"]?[cdn]?["url"].Str() : null) ?? hls?["cdns"]?.Find( "url" ).Str();
		if ( m.IsLive ) m.Files.Clear();
		return m;
	}
}

/// <summary>
/// soundcloud.com/{user}/{track}. Uses the public web client id (scraped from the site's scripts): the progressive
/// mp3 if the track has one, else its HLS AAC.
/// </summary>
public sealed class SoundCloudExtractor : SiteExtractor
{
	public override string Key => "soundcloud";
	protected override string[] Hosts => new[] { "soundcloud.com" };

	static string clientId;
	static readonly Regex ScriptSrc = new( "<script[^>]+src=\"(https://a-v2\\.sndcdn\\.com/assets/[^\"]+\\.js)\"", RegexOptions.Compiled );
	static readonly Regex ClientIdPattern = new( "client_id\\s*:\\s*\"([0-9a-zA-Z]{32})\"", RegexOptions.Compiled );

	protected override string ParseId( Uri url )
	{
		var parts = url.AbsolutePath.Trim( '/' ).Split( '/', StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length < 2 || parts[1] is "sets" or "likes" or "tracks" or "reposts" or "albums" ) return null;
		return $"https://soundcloud.com/{parts[0]}/{parts[1]}";
	}

	static async Task<string> GetClientId( CancellationToken ct, bool refresh = false )
	{
		if ( clientId is not null && !refresh ) return clientId;

		var html = await Http.RequestStringAsync( "https://soundcloud.com/", cancellationToken: ct );
		foreach ( var src in ScriptSrc.Matches( html ).Select( m => m.Groups[1].Value ).Reverse() )
		{
			var js = await Http.RequestStringAsync( src, cancellationToken: ct );
			var m = ClientIdPattern.Match( js );
			if ( m.Success ) return clientId = m.Groups[1].Value;
		}

		throw new ResolveException( "Couldn't talk to SoundCloud right now." );
	}

	protected override async Task<SiteMedia> FetchAsync( string id, CancellationToken ct )
	{
		JsonNode track;
		try
		{
			track = await GetJson( $"https://api-v2.soundcloud.com/resolve?url={Uri.EscapeDataString( id )}&client_id={await GetClientId( ct )}", ct );
		}
		catch ( ResolveException ) when ( clientId is not null )
		{
			// the client id rotates now and then
			track = await GetJson( $"https://api-v2.soundcloud.com/resolve?url={Uri.EscapeDataString( id )}&client_id={await GetClientId( ct, true )}", ct );
		}

		if ( track?["kind"].Str() != "track" )
			throw new ResolveException( "Only single SoundCloud tracks can be played." );

		var m = new SiteMedia { Title = track["title"].Str(), Duration = track["duration"].Long() / 1000f, AudioOnly = true };
		var transcodings = track["media"]?["transcodings"].Items().ToList() ?? new List<JsonNode>();

		foreach ( var t in transcodings.Where( t => t["format"]?["protocol"].Str() == "progressive" ) )
		{
			var url = (await GetJson( $"{t["url"].Str()}?client_id={clientId}", ct ))?["url"].Str();
			if ( string.IsNullOrEmpty( url ) ) continue;
			var mime = t["format"]?["mime_type"].Str() ?? "audio/mpeg";
			m.Files.Add( new MediaFormat { Id = t["preset"].Str(), Url = url, Container = mime.Contains( "mpeg" ) ? "mp3" : "ogg", AudioCodec = mime } );
		}

		if ( m.Files.Count == 0 )
		{
			// HLS: AAC (fMP4) - the mp3 and Opus ones aren't in a container the segmenter reads
			var aac = transcodings
				.Where( t => t["format"]?["protocol"].Str() == "hls" && (t["format"]?["mime_type"].Str() ?? "").Contains( "mp4a" ) )
				.OrderByDescending( t => t["preset"].Str()?.Contains( "160" ) ?? false )
				.FirstOrDefault();
			if ( aac is not null ) m.Hls = (await GetJson( $"{aac["url"].Str()}?client_id={clientId}", ct ))?["url"].Str();
		}

		if ( m.Files.Count == 0 && m.Hls is null )
			throw new ResolveException( "This SoundCloud track can't be streamed (no mp3 or AAC stream)." );
		return m;
	}
}
