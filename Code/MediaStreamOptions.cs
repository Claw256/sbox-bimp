using System.Globalization;

namespace Bimp;

/// <summary>
/// One audio track of a video, e.g. a YouTube dub.
/// </summary>
public sealed class MediaAudioTrack
{
	/// <summary> Language code, as the resolver's &amp;lang= expects ("en-US", "es", "ja"). </summary>
	public string Id { get; set; }

	/// <summary> Display name ("Spanish", "English (US) original"). </summary>
	public string Name { get; set; }

	/// <summary> The video's original track. </summary>
	public bool IsDefault { get; set; }
}

/// <summary>
/// The resolution / audio track choices of the current media, and which ones this client picks.
/// Every client chooses for itself - they all watch the same timeline, just different files.
/// </summary>
public static class MediaStreamOptions
{
	public static string EncodeQualities( IEnumerable<int> heights )
	{
		if ( heights is null ) return null;
		var list = heights.Where( h => h > 0 ).Distinct().OrderByDescending( h => h ).ToList();
		return list.Count > 0 ? string.Join( ",", list ) : null;
	}

	public static IReadOnlyList<int> ParseQualities( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) ) return Array.Empty<int>();

		return value.Split( ',', StringSplitOptions.RemoveEmptyEntries )
			.Select( s => int.TryParse( s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h ) ? h : 0 )
			.Where( h => h > 0 )
			.ToList();
	}

	public static string EncodeAudioTracks( IEnumerable<MediaAudioTrack> tracks )
	{
		var list = tracks?.Where( t => !string.IsNullOrWhiteSpace( t?.Id ) ).ToList();
		return list is { Count: > 1 } ? Json.Serialize( list ) : null;
	}

	static string cachedTracksJson;
	static IReadOnlyList<MediaAudioTrack> cachedTracks = Array.Empty<MediaAudioTrack>();

	public static IReadOnlyList<MediaAudioTrack> ParseAudioTracks( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) return Array.Empty<MediaAudioTrack>();
		if ( json == cachedTracksJson ) return cachedTracks;

		try
		{
			cachedTracks = Json.Deserialize<List<MediaAudioTrack>>( json ) ?? new List<MediaAudioTrack>();
		}
		catch ( Exception )
		{
			cachedTracks = Array.Empty<MediaAudioTrack>();
		}

		cachedTracksJson = json;
		return cachedTracks;
	}

	/// <summary>
	/// The height to ask for, given what's available and what the player prefers. 0 = let the resolver pick (auto).
	/// </summary>
	public static int ChooseQuality( IReadOnlyList<int> available, int preferred )
	{
		if ( preferred <= 0 || available.Count == 0 ) return 0;

		// the best we have at or below the preference, else the lowest there is
		var below = available.Where( h => h <= preferred ).ToList();
		return below.Count > 0 ? below.Max() : available.Min();
	}

	/// <summary>
	/// The audio track to ask for, or null for the original. "es" matches "es-419".
	/// </summary>
	public static MediaAudioTrack ChooseAudioTrack( IReadOnlyList<MediaAudioTrack> available, string preferred )
	{
		if ( string.IsNullOrWhiteSpace( preferred ) || available.Count == 0 ) return null;

		var exact = available.FirstOrDefault( t => string.Equals( t.Id, preferred, StringComparison.OrdinalIgnoreCase ) );
		if ( exact is not null ) return exact.IsDefault ? null : exact;

		var primary = preferred.Split( '-' )[0];
		var loose = available.FirstOrDefault( t => string.Equals( t.Id.Split( '-' )[0], primary, StringComparison.OrdinalIgnoreCase ) );
		return loose is null || loose.IsDefault ? null : loose;
	}

	/// <summary>
	/// Add the client's choices to a play token (native resolver media).
	/// </summary>
	public static string ApplyTo( string playUrl, int height, MediaAudioTrack track )
	{
		if ( string.IsNullOrEmpty( playUrl ) ) return playUrl;

		var url = playUrl;
		if ( height > 0 ) url += $"&h={height}";
		if ( track is not null ) url += $"&lang={Uri.EscapeDataString( track.Id )}";
		return url;
	}

	public static string QualityLabel( int height ) => height switch
	{
		>= 4320 => "4320p (8K)",
		>= 2160 => $"{height}p (4K)",
		>= 1440 => $"{height}p (QHD)",
		>= 720 => $"{height}p (HD)",
		_ => $"{height}p",
	};
}
