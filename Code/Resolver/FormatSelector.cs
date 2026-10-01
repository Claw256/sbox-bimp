namespace Bimp.Resolver;

/// <summary>
/// One format an extractor found.
/// </summary>
public sealed class MediaFormat
{
	public string Id { get; set; }
	public string Url { get; set; }

	/// <summary> "webm", "mp4", "mp3"... </summary>
	public string Container { get; set; }

	/// <summary> Lowercase codec name, null if the format has no video. </summary>
	public string VideoCodec { get; set; }

	/// <summary> Lowercase codec name, null if the format has no audio. </summary>
	public string AudioCodec { get; set; }

	public int Height { get; set; }
	public float Fps { get; set; }
	public long Bitrate { get; set; }
	public long Size { get; set; }
	public bool Hdr { get; set; }

	/// <summary> A dynamic range compressed copy of an audio track. </summary>
	public bool Drc { get; set; }

	/// <summary> Audio language ("en", "es-419"), null if unlabelled. </summary>
	public string Language { get; set; }
	public string LanguageName { get; set; }

	/// <summary> This is the original audio track. </summary>
	public bool LanguageDefault { get; set; }

	public bool HasVideo => VideoCodec is not null;
	public bool HasAudio => AudioCodec is not null;
	public bool IsVp9OrAv1 => VideoCodec is { } v && (v.StartsWith( "vp9" ) || v.StartsWith( "vp09" ) || v.StartsWith( "av01" ) || v.StartsWith( "av1" ));
	public bool IsOpus => AudioCodec?.StartsWith( "opus" ) ?? false;

	public StreamFile ToFile() => new() { Url = Url, Size = Size, FormatId = Id, Container = Container };
}

/// <summary>
/// Picks formats the engine can play. A port of the old resolver's format picking:
/// VP9/AV1 WebM video + Opus WebM audio (merged locally), else a single muxed file.
/// </summary>
public static class FormatSelector
{
	/// <summary> Video-only VP9/AV1 WebM files - the ones we can merge without re-encoding. </summary>
	public static List<MediaFormat> WebmVideo( IEnumerable<MediaFormat> formats )
		=> formats.Where( f => f.Container == "webm" && f.IsVp9OrAv1 && !f.HasAudio ).ToList();

	/// <summary> Audio-only Opus WebM files (every language), without the dynamic-range-compressed copies. </summary>
	public static List<MediaFormat> WebmAudio( IEnumerable<MediaFormat> formats )
		=> formats.Where( f => f.Container == "webm" && f.IsOpus && !f.HasVideo && !f.Drc ).ToList();

	/// <summary>
	/// Tallest at or below the limit first, SDR before HDR (the engine's HDR output is untested and would look
	/// washed out anyway), then fps, then bitrate.
	/// </summary>
	static (bool, bool, int, float, long) ScoreVideo( MediaFormat f, int limit )
		=> (f.Height <= limit, !f.Hdr, f.Height <= limit ? f.Height : -f.Height, f.Fps, f.Bitrate);

	/// <summary>
	/// VP9 at 4K and more than 30 fps: more than the engine's VP9 decoder keeps up with (measured 19-45 frames a second
	/// on a Ryzen 9 9900X3D; 1440p60 and 4K30 are fine).
	/// </summary>
	public static bool TooHeavyForVp9( MediaFormat f ) => f.Height >= 2160 && f.Fps > 30;

	/// <summary> An 8-bit SDR AV1 video-only MP4 with the same height and frame rate, highest bitrate first. </summary>
	public static MediaFormat Av1Like( IEnumerable<MediaFormat> formats, MediaFormat like )
		=> formats.Where( f => f.Container == "mp4" && !f.HasAudio && !f.Hdr && f.Height == like.Height && MathF.Abs( f.Fps - like.Fps ) < 1
				&& f.VideoCodec is { } v && v.StartsWith( "av01" ) && v.Split( '.' ) is { Length: >= 4 } p && p[3] == "08" )
			.OrderByDescending( f => f.Bitrate )
			.FirstOrDefault();

	/// <summary>
	/// What a 4K60 AV1 choice carries on with once YouTube stops serving it: the tallest SDR VP9 below 4K at the same
	/// frame rate (1440p60 - the engine's VP9 decoder keeps up with that).
	/// </summary>
	public static MediaFormat SwitchLike( IEnumerable<MediaFormat> formats, MediaFormat like )
		=> WebmVideo( formats ).Where( f => !f.Hdr && f.Height < 2160 && MathF.Abs( f.Fps - like.Fps ) < 1 && f.VideoCodec is { } v && !v.StartsWith( "av01" ) )
			.OrderByDescending( f => f.Height ).ThenByDescending( f => f.Bitrate )
			.FirstOrDefault();

	public static List<int> Qualities( IEnumerable<MediaFormat> formats )
	{
		var video = WebmVideo( formats );
		var sdr = video.Where( f => !f.Hdr ).ToList();
		if ( sdr.Count == 0 ) sdr = video;
		return sdr.Select( f => f.Height ).Where( h => h > 0 ).Distinct().OrderByDescending( h => h ).ToList();
	}

	/// <summary> The original audio track's language. </summary>
	static string DefaultLanguage( List<MediaFormat> audio )
	{
		var labelled = audio.Where( f => !string.IsNullOrEmpty( f.Language ) ).ToList();
		if ( labelled.Count == 0 ) return null;
		return (labelled.FirstOrDefault( f => f.LanguageDefault ) ?? labelled[0]).Language;
	}

	public static List<MediaAudioTrack> AudioTracks( IEnumerable<MediaFormat> formats )
	{
		var audio = WebmAudio( formats );
		var def = DefaultLanguage( audio );
		var tracks = new Dictionary<string, MediaAudioTrack>();
		foreach ( var f in audio )
		{
			if ( string.IsNullOrEmpty( f.Language ) || tracks.ContainsKey( f.Language ) ) continue;
			tracks[f.Language] = new MediaAudioTrack { Id = f.Language, Name = f.LanguageName ?? f.Language, IsDefault = f.Language == def };
		}

		// a single (or unlabelled) track isn't a choice
		if ( tracks.Count < 2 ) return new List<MediaAudioTrack>();
		return tracks.Values.OrderBy( t => !t.IsDefault ).ThenBy( t => t.Name.ToLowerInvariant() ).ToList();
	}

	/// <summary> Do these formats have an audio track in this language, matched the way <see cref="PickAudio"/> matches it? </summary>
	public static bool HasLanguage( IEnumerable<MediaFormat> formats, string lang )
	{
		if ( string.IsNullOrWhiteSpace( lang ) ) return true;
		var primary = lang.Split( '-' )[0];
		return WebmAudio( formats ).Any( f => string.Equals( f.Language, lang, StringComparison.OrdinalIgnoreCase )
			|| string.Equals( (f.Language ?? "").Split( '-' )[0], primary, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary> Audio formats for the requested language ("es" finds "es-419"), falling back to the original track. </summary>
	static List<MediaFormat> PickAudio( List<MediaFormat> audio, string lang )
	{
		if ( audio.Count == 0 ) return audio;

		if ( !string.IsNullOrWhiteSpace( lang ) )
		{
			var exact = audio.Where( f => string.Equals( f.Language, lang, StringComparison.OrdinalIgnoreCase ) ).ToList();
			if ( exact.Count > 0 ) return exact;

			var primary = lang.Split( '-' )[0];
			var prefix = audio.Where( f => string.Equals( (f.Language ?? "").Split( '-' )[0], primary, StringComparison.OrdinalIgnoreCase ) ).ToList();
			if ( prefix.Count > 0 ) return prefix;
		}

		var def = DefaultLanguage( audio );
		return def is null ? audio : audio.Where( f => f.Language == def ).ToList();
	}

	/// <summary>
	/// Best first. With an exact height, that height wins if it exists, else the nearest below it.
	/// </summary>
	static List<MediaFormat> PickVideo( List<MediaFormat> video, int maxHeight, int height )
	{
		var limit = height > 0 ? height : maxHeight;
		return video.OrderByDescending( f => ScoreVideo( f, limit ) ).ToList();
	}

	public static StreamPlan Plan( IReadOnlyList<MediaFormat> formats, StreamRequest request )
	{
		var maxh = Math.Clamp( request.MaxHeight, 144, 2160 );
		var audio = PickAudio( WebmAudio( formats ), request.Language ).OrderByDescending( f => f.Bitrate ).ToList();

		if ( request.AudioOnly )
		{
			if ( audio.Count > 0 )
				return new StreamPlan { Kind = StreamKind.Audio, Audio = audio[0].ToFile() };

			// anything with audio the engine can decode on its own
			var anyAudio = formats.Where( f => f.HasAudio && !f.HasVideo && f.Container is "mp3" or "ogg" or "m4a" )
				.OrderByDescending( f => f.Bitrate ).FirstOrDefault()
				?? formats.Where( f => f.HasAudio && f.Container is "mp4" or "webm" ).OrderBy( f => f.Height ).FirstOrDefault();
			if ( anyAudio is not null )
				return new StreamPlan { Kind = StreamKind.Direct, DirectUrl = anyAudio.Url };

			throw new ResolveException( "No playable audio found." );
		}

		var video = PickVideo( WebmVideo( formats ), maxh, request.Height );
		if ( video.Count > 0 && audio.Count > 0 )
			return new StreamPlan { Kind = StreamKind.Merge, Video = video[0].ToFile(), Audio = audio[0].ToFile() };

		// a single muxed file the engine can decode (VP9/AV1+Opus WebM, or H.264/AAC MP4)
		var muxed = formats.Where( f => f.HasVideo && f.HasAudio && f.Container is "webm" or "mp4" )
			.OrderByDescending( f => ScoreVideo( f, maxh ) )
			.ThenByDescending( f => f.Container == "webm" )
			.FirstOrDefault();
		if ( muxed is not null )
			return new StreamPlan { Kind = StreamKind.Direct, DirectUrl = muxed.Url };

		throw new ResolveException( "No playable formats found." );
	}
}
