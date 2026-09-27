namespace Bimp;

/// <summary>
/// Lets players use the media player with the USE key (works with the engine's <see cref="PlayerController"/>,
/// or anything else that calls <see cref="Component.IPressable"/>). Needs a collider on this GameObject or a child.
/// <para>
/// Aiming at one of the screen's on-screen buttons while the controls are showing presses that button.
/// Anywhere else (or while the controls are hidden) opens the full remote.
/// </para>
/// </summary>
[Title( "Media Player Interact" ), Category( "Media" ), Icon( "touch_app" )]
public sealed class MediaInteract : Component, Component.IPressable
{
	/// <summary>
	/// The player to control. Found automatically in this object or its parents if unset.
	/// </summary>
	[Property] public MediaPlayer Player { get; set; }

	/// <summary>
	/// The screen whose on-screen buttons USE can press. Found automatically in this object or its children if unset.
	/// </summary>
	[Property] public MediaScreen Screen { get; set; }

	MediaPlayer Target => Player.IsValid() ? Player : (Player = GetComponentInParent<MediaPlayer>());
	MediaScreen TargetScreen => Screen.IsValid() ? Screen : (Screen = GetComponentInChildren<MediaScreen>());

	public bool CanPress( IPressable.Event e ) => Target.IsValid();

	public bool Press( IPressable.Event e )
	{
		if ( !Target.IsValid() ) return false;

		// The screen may already have handled this USE press itself this frame
		if ( !UseHandled.TryConsume() ) return true;

		// Press runs on the client that pressed, so this only affects them (button presses go through the
		// player's permission-checked host requests)
		var screen = TargetScreen;
		if ( screen.IsValid() && screen.TryPressAimedControl() )
			return true;

		MediaRemote.Toggle( Target );
		return true;
	}

	public IPressable.Tooltip? GetTooltip( IPressable.Event e )
	{
		if ( !Target.IsValid() ) return null;

		var title = Target.HasMedia ? Target.Title : "Media Player";
		var icon = Target.AudioOnlyPlayer ? "speaker" : "smart_display";

		var action = TargetScreen.IsValid() ? TargetScreen.AimedActionName : null;
		if ( action is not null )
			return new IPressable.Tooltip( action, "touch_app", title );

		return new IPressable.Tooltip( title, icon, "Open controls" );
	}
}
