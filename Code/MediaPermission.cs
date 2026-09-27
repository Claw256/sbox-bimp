namespace Bimp;

/// <summary>
/// Who is allowed to control a <see cref="MediaPlayer"/> (play, pause, seek, skip, remove from queue).
/// The host can always control every player.
/// </summary>
public enum MediaPermission
{
	/// <summary>
	/// Any connected player can control it.
	/// </summary>
	[Icon( "public" )]
	Anyone,

	/// <summary>
	/// Only the host of the server.
	/// </summary>
	[Icon( "dns" )]
	HostOnly,

	/// <summary>
	/// The host, plus the SteamIds listed in <see cref="MediaPlayer.AllowedSteamIds"/>.
	/// </summary>
	[Icon( "badge" )]
	Whitelist,
}
