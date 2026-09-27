using System;
using System.Globalization;
using System.Linq;

namespace Bimp;

/// <summary>
/// Url helpers. Direct media files are played as-is; YouTube and the other sites the native resolver
/// understands go through <see cref="Resolver.BimpResolver"/>.
/// </summary>
public static class MediaSource
{
	static readonly string[] AudioExtensions = { "mp3", "ogg", "oga", "opus", "flac", "wav", "m4a", "aac" };
	static readonly string[] VideoExtensions = { "mp4", "webm", "mkv", "mov", "m4v", "webp" };

	/// <summary>
	/// Try to parse the user's input into an absolute http(s) url. Adds https:// if missing.
	/// </summary>
	public static bool TryNormalize( string input, out Uri uri )
	{
		uri = null;
		if ( string.IsNullOrWhiteSpace( input ) ) return false;

		input = input.Trim();
		if ( !input.Contains( "://" ) ) input = "https://" + input;

		if ( !Uri.TryCreate( input, UriKind.Absolute, out uri ) ) return false;
		return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || Resolver.Live.LiveStream.IsRtsp( uri );
	}

	static string Extension( Uri uri ) => System.IO.Path.GetExtension( uri.AbsolutePath ).TrimStart( '.' ).ToLowerInvariant();

	public static bool IsDirectMedia( Uri uri )
	{
		var ext = Extension( uri );
		return AudioExtensions.Contains( ext ) || VideoExtensions.Contains( ext );
	}

	public static bool IsAudioExtension( Uri uri ) => AudioExtensions.Contains( Extension( uri ) );

	/// <summary>
	/// A live stream: RTSP (tunnelled over HTTP), MPEG-TS over HTTP, or HLS. Each client connects to it itself.
	/// </summary>
	public static MediaQueueItem LiveItem( Uri uri, bool forceAudio )
	{
		var name = System.IO.Path.GetFileName( uri.AbsolutePath );
		return new MediaQueueItem
		{
			Url = uri.ToString(),
			PlayUrl = uri.ToString(),
			Title = string.IsNullOrWhiteSpace( name ) ? uri.Host : $"{uri.Host} - {Uri.UnescapeDataString( name )}",
			Duration = 0,
			IsLive = true,
			AudioOnly = forceAudio,
			SeekByReload = false,
		};
	}

	/// <summary>
	/// A url the engine plays itself.
	/// </summary>
	public static MediaQueueItem DirectItem( Uri uri, bool forceAudio )
	{
		var name = Uri.UnescapeDataString( System.IO.Path.GetFileName( uri.AbsolutePath ) );
		if ( string.IsNullOrWhiteSpace( name ) ) name = uri.Host;

		return new MediaQueueItem
		{
			Url = uri.ToString(),
			PlayUrl = uri.ToString(),
			Title = name,
			Duration = 0, // learned from the player once it loads
			AudioOnly = forceAudio || IsAudioExtension( uri ),
			SeekByReload = false,
		};
	}

	/// <summary>
	/// Format seconds as m:ss or h:mm:ss.
	/// </summary>
	public static string FormatTime( float seconds )
	{
		if ( float.IsNaN( seconds ) || seconds < 0 ) seconds = 0;
		var t = TimeSpan.FromSeconds( seconds );
		return t.TotalHours >= 1 ? t.ToString( @"h\:mm\:ss" ) : t.ToString( @"m\:ss" );
	}
}
