using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bimp.Resolver.Extractors;

/// <summary>
/// The parts of YouTube Music links that aren't a video or a playlist id: album pages (<c>/browse/MPREb_...</c>) and
/// artist pages (<c>/channel/UC...</c>) have no list in the url, so the playlist behind them is read from the page's
/// "browse" answer. Measured 2026-10-16: only the WEB_REMIX client gets one (WEB answers 500).
/// </summary>
static class YouTubeMusic
{
	/// <summary> The music site's own client. Not in <see cref="InnerTubeClients.All"/>: only used for "browse". </summary>
	public static readonly InnerTubeClient Client = new()
	{
		Key = "web_remix",
		Name = "WEB_REMIX",
		Version = "1.20250310.01.00",
		Id = 67,
	};

	static readonly Regex AlbumPath = new( "^/browse/(MPREb_[A-Za-z0-9_-]+)/?$", RegexOptions.Compiled );
	static readonly Regex ArtistPath = new( "^/channel/(UC[A-Za-z0-9_-]+)/?$", RegexOptions.Compiled );
	static readonly Regex PlaylistBrowsePath = new( "^/browse/VL([A-Za-z0-9_-]+)/?$", RegexOptions.Compiled );

	/// <summary> The id of an album or artist page to ask "browse" about, or null for any other url. </summary>
	public static string BrowseIdOf( Uri url )
	{
		if ( !YouTubeExtractor.IsMusicUrl( url ) ) return null;

		var m = AlbumPath.Match( url.AbsolutePath );
		if ( !m.Success ) m = ArtistPath.Match( url.AbsolutePath );
		return m.Success ? m.Groups[1].Value : null;
	}

	/// <summary> The playlist id of a <c>/browse/VL...</c> link (a playlist's own page), or null for any other url. </summary>
	public static string PlaylistOfBrowsePath( Uri url )
	{
		if ( !YouTubeExtractor.IsMusicUrl( url ) ) return null;

		var m = PlaylistBrowsePath.Match( url.AbsolutePath );
		return m.Success ? m.Groups[1].Value : null;
	}

	/// <summary>
	/// The playlist to play for an album or artist page, from its "browse" answer: an album names its playlist (OLAK5uy...) as
	/// its canonical link; an artist's "Top songs" shelf links one; else the first playlist the page links (an album or
	/// playlist shelf), else the artist's radio behind its play button. Null if the page has none.
	/// </summary>
	public static string PlaylistOf( JsonNode browse )
	{
		var canonical = browse?["microformat"]?["microformatDataRenderer"]?["urlCanonical"].Str();
		if ( canonical is not null && Uri.TryCreate( canonical, UriKind.Absolute, out var page ) )
		{
			var list = System.Web.HttpUtility.ParseQueryString( page.Query )["list"];
			if ( !string.IsNullOrEmpty( list ) ) return list;
		}

		foreach ( var shelf in browse.FindAll( "musicShelfRenderer" ) )
		{
			var target = shelf["title"]?["runs"]?[0]?["navigationEndpoint"]?["browseEndpoint"]?["browseId"].Str()
				?? shelf["bottomEndpoint"]?["browseEndpoint"]?["browseId"].Str();
			if ( target is not null && target.StartsWith( "VL", StringComparison.Ordinal ) ) return target.Substring( 2 );
		}

		var linked = browse.Find( "watchPlaylistEndpoint" )?["playlistId"].Str();
		if ( !string.IsNullOrEmpty( linked ) ) return linked;

		var radio = browse.Find( "playButton" )?.Find( "playlistId" ).Str();
		return string.IsNullOrEmpty( radio ) ? null : radio;
	}
}
