using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bimp;

/// <summary>
/// One caption track of a video (a language, or YouTube's auto-generated one).
/// </summary>
public sealed class MediaCaptionTrack
{
	/// <summary> Extractor specific id (YouTube's vssId: ".en", "a.en"), passed back to the extractor to fetch the cues. </summary>
	public string Id { get; set; }

	/// <summary> Language code ("en", "pt-BR"). </summary>
	public string Lang { get; set; }

	/// <summary> Display name ("English", "English (auto-generated)"). </summary>
	public string Name { get; set; }

	/// <summary> Speech recognition, not written by a person. </summary>
	public bool IsAuto { get; set; }
}

/// <summary> One line of text, shown from <see cref="Start"/> until <see cref="End"/> (seconds). </summary>
public readonly record struct CaptionCue( float Start, float End, string Text );

/// <summary>
/// Caption tracks and cues: the choices of the current media (synced as JSON like the audio tracks), picking one, and
/// reading the cue files extractors hand out.
/// </summary>
public static class Captions
{
	public static string EncodeTracks( IEnumerable<MediaCaptionTrack> tracks )
	{
		var list = tracks?.Where( t => !string.IsNullOrWhiteSpace( t?.Id ) ).ToList();
		return list is { Count: > 0 } ? Json.Serialize( list ) : null;
	}

	static string cachedJson;
	static IReadOnlyList<MediaCaptionTrack> cachedTracks = Array.Empty<MediaCaptionTrack>();

	public static IReadOnlyList<MediaCaptionTrack> ParseTracks( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) return Array.Empty<MediaCaptionTrack>();
		if ( json == cachedJson ) return cachedTracks;

		try
		{
			cachedTracks = Json.Deserialize<List<MediaCaptionTrack>>( json ) ?? new List<MediaCaptionTrack>();
		}
		catch ( Exception )
		{
			cachedTracks = Array.Empty<MediaCaptionTrack>();
		}

		cachedJson = json;
		return cachedTracks;
	}

	/// <summary>
	/// The track for a language preference ("es" matches "es-419"), written by a person before speech recognition.
	/// With no preference, the first written track. Null when there's nothing to show.
	/// </summary>
	public static MediaCaptionTrack Choose( IReadOnlyList<MediaCaptionTrack> available, string preferred )
	{
		if ( available.Count == 0 ) return null;

		if ( !string.IsNullOrWhiteSpace( preferred ) )
		{
			var primary = Primary( preferred );
			var match = available.Where( t => string.Equals( t.Lang, preferred, StringComparison.OrdinalIgnoreCase ) )
				.Concat( available.Where( t => string.Equals( Primary( t.Lang ), primary, StringComparison.OrdinalIgnoreCase ) ) )
				.OrderBy( t => t.IsAuto )
				.FirstOrDefault();
			if ( match is not null ) return match;
		}

		return available.FirstOrDefault( t => !t.IsAuto ) ?? available[0];
	}

	static string Primary( string lang ) => (lang ?? "").Split( '-' )[0];

	/// <summary> The text to show at this time: of the cues covering it, the one that started last. Null for none. </summary>
	public static string TextAt( IReadOnlyList<CaptionCue> cues, float time )
	{
		if ( cues is null || cues.Count == 0 ) return null;

		// cues are sorted by start: find the last one that has started, then walk back to one that's still showing
		int lo = 0, hi = cues.Count - 1, last = -1;
		while ( lo <= hi )
		{
			var mid = (lo + hi) / 2;
			if ( cues[mid].Start <= time ) { last = mid; lo = mid + 1; }
			else hi = mid - 1;
		}

		// overlapping cues (auto-generated captions roll) end at different times; look a few back
		for ( var i = last; i >= 0 && i > last - 8; i-- )
			if ( cues[i].End > time ) return cues[i].Text;

		return null;
	}

	/// <summary> Cues from a caption file: YouTube's json3 or timedtext XML. Empty if it isn't either. </summary>
	public static List<CaptionCue> Parse( string body )
	{
		var cues = new List<CaptionCue>();
		if ( string.IsNullOrWhiteSpace( body ) ) return cues;

		try
		{
			if ( body.TrimStart().StartsWith( '{' ) ) ParseJson3( body, cues );
			else ParseTimedText( body, cues );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[bimp] Couldn't read the captions: {e.Message}" );
			cues.Clear();
		}

		cues.Sort( ( a, b ) => a.Start.CompareTo( b.Start ) );
		return cues;
	}

	static void ParseJson3( string body, List<CaptionCue> cues )
	{
		var events = JsonNode.Parse( body )?["events"] as JsonArray;
		if ( events is null ) return;

		foreach ( var e in events )
		{
			if ( e?["segs"] is not JsonArray segs ) continue;

			var text = new StringBuilder();
			foreach ( var seg in segs ) text.Append( (string)seg?["utf8"] );

			var clean = Clean( text.ToString() );
			if ( clean.Length == 0 ) continue;

			var start = ReadMs( e["tStartMs"] ) / 1000f;
			var duration = ReadMs( e["dDurationMs"] ) / 1000f;
			cues.Add( new CaptionCue( start, start + Math.Max( duration, 0.5f ), clean ) );
		}
	}

	// <p t="1360" d="1680">text</p>, with <s> word spans inside for some auto-generated tracks
	static readonly Regex Paragraph = new( "<p\\s[^>]*?t=\"(\\d+)\"[^>]*?d=\"(\\d+)\"[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.Compiled );
	static readonly Regex Tags = new( "<[^>]+>", RegexOptions.Compiled );

	static void ParseTimedText( string body, List<CaptionCue> cues )
	{
		foreach ( Match m in Paragraph.Matches( body ) )
		{
			var clean = Clean( System.Web.HttpUtility.HtmlDecode( Tags.Replace( m.Groups[3].Value, "" ) ) );
			if ( clean.Length == 0 ) continue;

			var start = int.Parse( m.Groups[1].Value, CultureInfo.InvariantCulture ) / 1000f;
			var duration = int.Parse( m.Groups[2].Value, CultureInfo.InvariantCulture ) / 1000f;
			cues.Add( new CaptionCue( start, start + Math.Max( duration, 0.5f ), clean ) );
		}
	}

	static long ReadMs( JsonNode node ) => node is JsonValue v && v.TryGetValue<long>( out var n ) ? n : 0;

	/// <summary> Trim, and turn line breaks into "\n" without blank lines. </summary>
	static string Clean( string text )
	{
		var lines = text.Replace( "\r", "" ).Split( '\n' ).Select( l => l.Trim() ).Where( l => l.Length > 0 );
		return string.Join( "\n", lines );
	}
}
