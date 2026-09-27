namespace Bimp;

/// <summary>
/// Console variables for the media player (bimp).
/// </summary>
public static class MediaSettings
{
	/// <summary>
	/// YouTube InnerTube clients to try, in order (see <see cref="Resolver.InnerTubeClients"/>). YouTube changes
	/// which clients work now and then - this lets a server adjust without a code change.
	/// </summary>
	[ConVar( "bimp_yt_clients", ConVarFlags.Replicated | ConVarFlags.Saved, Help = "YouTube clients to try in order (visionos, android_vr, ios, android)" )]
	public static string YouTubeClients { get; set; } = Resolver.InnerTubeClients.DefaultOrder;

	/// <summary>
	/// Minimum length of a live stream segment, in seconds (segments are cut at keyframes, so they're never shorter
	/// than the source's keyframe interval). Every segment boundary costs a small hitch - a frame skip and a short
	/// audio crossfade - so longer segments hitch less often, at the cost of about twice that much extra latency.
	/// </summary>
	[ConVar( "bimp_live_segment", ConVarFlags.Saved, Min = 1, Max = 20, Help = "Minimum live segment length in seconds: longer = fewer hitches, more latency (default 4)" )]
	public static float LiveSegmentSeconds { get; set; } = 4.0f;

	/// <summary>
	/// Live latency mode for RTSP and MPEG-TS streams. "low": a segment per keyframe interval and no overlap, so the
	/// delay is about one keyframe interval plus ~0.2 s (a camera with a 1 s keyframe interval plays about 1.2 s
	/// behind). "normal": longer segments (bimp_live_segment) with fewer decoder restarts, ~2 segments behind.
	/// HLS always plays normal. Takes effect on the next stream.
	/// </summary>
	[ConVar( "bimp_live_latency", ConVarFlags.Saved, Help = "Live stream latency: low (about one keyframe interval) or normal (bimp_live_segment segments)" )]
	public static string LiveLatency { get; set; } = "low";

	internal static bool LowLatencyLive => !string.Equals( LiveLatency?.Trim(), "normal", StringComparison.OrdinalIgnoreCase );

	/// <summary>
	/// Play YouTube videos at 4K above 30 fps from their AV1 copy. The engine's VP9 decoder can't keep up with 4K60
	/// (19-45 frames a second measured), its AV1 decoder plays the same video at a steady 60. Only the Android clients get
	/// AV1, and YouTube refuses their urls after ~88 MB (about a minute of 4K), so playback switches to VP9 1440p60 at a
	/// keyframe before that (see WebmSegmenter).
	/// </summary>
	[ConVar( "bimp_av1", ConVarFlags.Saved, Help = "Play YouTube 4K60 as AV1 while YouTube serves it (~88 MB, about a minute), then VP9 1440p60 - 0 = VP9 4K60 throughout (19-45 fps)" )]
	public static bool PreferAv1 { get; set; } = true;

	/// <summary>
	/// Local volume multiplier for every media player.
	/// </summary>
	[ConVar( "bimp_volume", ConVarFlags.Saved, Min = 0, Max = 1, Help = "Local volume of all media players (0-1)" )]
	public static float Volume { get; set; } = 1.0f;

	/// <summary>
	/// 3D audio: media players sound from where they are and fade with distance. Off = every media player plays flat
	/// (2D, no direction) for this player - still quieter the further away they are.
	/// </summary>
	[ConVar( "bimp_spatial", ConVarFlags.Saved, Help = "3D audio from media players (1, default), or flat 2D audio for you (0) - still fading with distance" )]
	public static bool SpatialAudio { get; set; } = true;

	/// <summary>
	/// Locally mute every media player.
	/// </summary>
	[ConVar( "bimp_mute", ConVarFlags.Saved, Help = "Mute all media players locally" )]
	public static bool Mute { get; set; } = false;

	/// <summary>
	/// Size of the full remote. It's laid out for 1080p and scaled with the screen height; this multiplies that (it
	/// never gets wider than 90% of the screen).
	/// </summary>
	[ConVar( "bimp_ui_scale", ConVarFlags.Saved, Min = 0.5f, Max = 3, Help = "Size of the media player remote: 1 = its 1080p design size, scaled with the screen height (default 1.4)" )]
	public static float RemoteScale { get; set; } = 1.4f;

	/// <summary>
	/// Max video height clients stream. Lower is easier on bandwidth.
	/// </summary>
	[ConVar( "bimp_max_height", ConVarFlags.Replicated | ConVarFlags.Saved, Help = "Max video height streamed (e.g. 480, 720, 1080)" )]
	public static int MaxVideoHeight { get; set; } = 720;

	/// <summary>
	/// Preferred video height for YouTube videos, e.g. 1080. 0 = automatic (bimp_max_height).
	/// If a video doesn't have it, the nearest height below is used.
	/// </summary>
	[ConVar( "bimp_quality", ConVarFlags.Saved, Help = "Preferred YouTube video height (e.g. 1080, 720). 0 = auto" )]
	public static int Quality { get; set; } = 0;

	/// <summary>
	/// Preferred audio language for videos with several audio tracks (YouTube dubs), e.g. "es" or "ja".
	/// Empty = the original track.
	/// </summary>
	[ConVar( "bimp_audio_lang", ConVarFlags.Saved, Help = "Preferred audio track language for dubbed videos (e.g. es, ja). Empty = original" )]
	public static string AudioLanguage { get; set; } = "";

	/// <summary>
	/// Log screen aiming / button hit-testing, for diagnosing the on-screen controls.
	/// </summary>
	[ConVar( "bimp_debug", Help = "Log what the media screens think you're aiming at" )]
	public static bool Debug { get; set; } = false;

	internal static float EffectiveVolume => Mute ? 0.0f : Volume.Clamp( 0.0f, 1.0f );
}
