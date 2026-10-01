using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bimp.Resolver.Extractors;

/// <summary>
/// dailymotion.com/video/{id}, dailymotion.com/embed/video/{id} and dai.ly/{id}: the player metadata's HLS playlist
/// (a finished video is remuxed from a start time, a live one is followed at its edge).
/// </summary>
public sealed class DailymotionExtractor : SiteExtractor
{
	public override string Key => "dailymotion";
	protected override string[] Hosts => new[] { "dailymotion.com", "dai.ly" };

	// ids are an "x" and a few base 36 digits; page urls can carry a slug after them ("x93tj4e_dragon-ball-daima")
	static readonly Regex PagePattern = new( "(?:^|/)video/(x[a-z0-9]{4,10})(?:[_/?]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled );
	static readonly Regex ShortPattern = new( "^/?(x[a-z0-9]{4,10})/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled );

	protected override string ParseId( Uri url )
	{
		var path = url.AbsolutePath;
		var m = url.Host.EndsWith( "dai.ly", StringComparison.OrdinalIgnoreCase ) ? ShortPattern.Match( path ) : PagePattern.Match( path );
		return m.Success ? m.Groups[1].Value : null;
	}

	protected override async Task<SiteMedia> FetchAsync( string id, CancellationToken ct )
	{
		var j = await GetJson( $"https://www.dailymotion.com/player/metadata/video/{id}", ct );
		var hls = j?["qualities"]?["auto"].Items().Select( q => q["url"].Str() ).FirstOrDefault( u => !string.IsNullOrEmpty( u ) );

		if ( hls is null )
		{
			// the player's own explanation ("Channel offline.", "This video has been deleted", a country block...)
			var why = j?["error"]?["title"].Str() ?? j?["error"]?["raw_message"].Str();
			throw new ResolveException( why is null ? "Dailymotion didn't offer a stream for that video." : $"Dailymotion: {why}" );
		}

		if ( j["protected_delivery"].Bool() )
			throw new ResolveException( "That Dailymotion video is DRM protected." );

		// Dailymotion's CDN (Cloudflare) answers 403 to the s&box HTTP client whatever headers it sends, while curl and
		// browsers get 200 (measured 2026-10-01; Referer/Origin can't be set from the sandbox). Say so instead of failing
		// later with a bare 403 - and play it if that ever changes.
		if ( !await CanFetch( hls, ct ) )
			throw new ResolveException( "Dailymotion's servers refuse connections from s&box, so this video can't be played." );

		return new SiteMedia
		{
			Title = j["title"].Str(),
			Duration = j["duration"].Float(),
			IsLive = j["mode"].Str() == "live" || j["stream_type"].Str() == "live",
			Hls = hls,
		};
	}

	static async Task<bool> CanFetch( string url, CancellationToken ct )
	{
		try
		{
			using var r = await Http.RequestAsync( url, cancellationToken: ct );
			return (int)r.StatusCode is >= 200 and < 300;
		}
		catch ( Exception e ) when ( e is not OperationCanceledException )
		{
			return false;
		}
	}
}
