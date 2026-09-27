using System;
using Sandbox.UI;

namespace Bimp;

/// <summary>
/// Moves a highlight around a panel's controls with a gamepad. The D-pad or left stick moves it to the next control
/// that way, A presses it, and left/right adjusts a control that can be adjusted (a slider) instead of moving. While a
/// popup menu is open (a drop down's), up/down moves through its options, A picks one and B closes it. Controls opt in
/// with the <c>nav</c> class (<c>nav-default</c> is highlighted first); the highlighted one gets <c>pad-focus</c>.
/// <para>
/// Pads only reach games through input actions, so each button is read as whatever action the game binds to it (A is
/// "Jump" in the default set) - it works with any game's bindings, and a button nothing is bound to just does nothing.
/// Call <see cref="Update"/> first thing in the frame (<see cref="Component.ISceneStage"/>), then suppress the frame's
/// input, so the player doesn't also jump or walk.
/// </para>
/// </summary>
public sealed class PadNavigation
{
	/// <summary> Stick past this counts as a direction. </summary>
	const float StickThreshold = 0.5f;

	/// <summary> Holding a direction repeats it: first after this long, then this often. </summary>
	const float RepeatDelay = 0.4f;
	const float RepeatInterval = 0.12f;

	/// <summary> Press the control, e.g. a button. </summary>
	public Action<Panel> Press { get; set; }

	/// <summary> Adjust the control by a step (-1 left, +1 right). Returns false if it can't be adjusted - left/right then moves. </summary>
	public Func<Panel, int, bool> Adjust { get; set; }

	/// <summary> The highlighted control, if any. </summary>
	public Panel Focused { get; private set; }

	/// <summary> The actions bound to A (press) and B (back), for glyphs. Null if the game binds nothing to them. </summary>
	public string SelectAction { get; }
	public string BackAction { get; }

	readonly string up, down, left, right;

	Vector2 heldDirection;
	RealTimeSince sinceMoved;
	bool repeating;
	Vector2 lastFocusCenter;

	public PadNavigation()
	{
		SelectAction = ActionFor( GamepadCode.A );
		BackAction = ActionFor( GamepadCode.B );
		up = ActionFor( GamepadCode.DpadNorth );
		down = ActionFor( GamepadCode.DpadSouth );
		left = ActionFor( GamepadCode.DpadWest );
		right = ActionFor( GamepadCode.DpadEast );
	}

	static string ActionFor( GamepadCode code ) => Input.GetActions().FirstOrDefault( a => a.GamepadCode == code )?.Name;

	static bool Down( string action ) => action is not null && Input.Down( action, false );
	static bool Pressed( string action ) => action is not null && Input.Pressed( action );

	/// <summary> B pressed this frame (and no popup menu took it). </summary>
	public bool BackPressed { get; private set; }

	/// <summary> A popup menu that's open (a drop down's options), if any - it takes the pad until it closes. </summary>
	static Popup OpenPopup( Panel root )
	{
		var top = root;
		while ( top.Parent is not null ) top = top.Parent;
		return top.Descendants.OfType<Popup>().FirstOrDefault( p => p.IsValid() && !p.IsDeleting && p.IsVisible );
	}

	/// <summary> Up/down through a popup's options, starting on the current one; A picks, B closes it. </summary>
	void UpdatePopup( Popup popup )
	{
		if ( popup.SelectedChild is null || !popup.SelectedChild.IsValid() )
		{
			// start on the current choice: the option showing the drop down's text (the engine's own "active" mark
			// compares the values by reference, and misses)
			var current = (popup.PopupSource as Button)?.Text;
			popup.SelectedChild = popup.Children.FirstOrDefault( c => c is Button b && b.Text == current )
				?? popup.Children.FirstOrDefault();
		}

		var direction = Direction();
		var step = direction.y != 0 && Repeat( direction );
		if ( direction != heldDirection ) Hold( direction );
		if ( step ) popup.MoveSelection( (int)direction.y );

		// our own mark for the option it's on, every frame: a Button resets its "active" class from its Active property
		// each tick, so the one MoveSelection sets never shows
		foreach ( var option in popup.Children ) option.SetClass( "pad-selected", option == popup.SelectedChild );

		if ( Pressed( SelectAction ) && popup.SelectedChild is { } selected )
			selected.CreateEvent( new MousePanelEvent( "onclick", selected, "mouseleft" ) );
		else if ( Pressed( BackAction ) )
			popup.Delete();
	}

	/// <summary>
	/// Read the pad and move, press or adjust. Keeps the highlight on a control that's still there (the panel may have
	/// rebuilt), or on the nearest one to where it was.
	/// </summary>
	public void Update( Panel root )
	{
		BackPressed = false;
		if ( !root.IsValid() ) return;

		if ( OpenPopup( root ) is { } popup )
		{
			UpdatePopup( popup );
			return;
		}
		BackPressed = Pressed( BackAction );

		var controls = root.Descendants.Where( p => p.HasClass( "nav" ) && p.IsVisible ).ToList();
		if ( Focused is null || !Focused.IsValid() || !controls.Contains( Focused ) )
			Focus( controls.FirstOrDefault( p => p.HasClass( "nav-default" ) && Focused is null ) ?? Nearest( controls, lastFocusCenter ) ?? controls.FirstOrDefault() );

		// the highlight is a class, re-applied every frame - a rebuild of the panel can reset classes
		foreach ( var p in controls ) p.SetClass( "pad-focus", p == Focused );
		if ( Focused is null ) return;
		lastFocusCenter = Focused.Box.Rect.Center;

		var direction = Direction();
		var step = Repeat( direction );
		if ( direction != heldDirection ) Hold( direction );
		if ( step ) Step( controls, direction );

		if ( Pressed( SelectAction ) ) Press?.Invoke( Focused );
	}

	/// <summary> Should a direction act this frame: when it's first pushed, then repeatedly while it's held. </summary>
	bool Repeat( Vector2 direction )
	{
		if ( direction == Vector2.Zero ) return false;
		if ( direction != heldDirection ) return true;
		if ( sinceMoved <= (repeating ? RepeatInterval : RepeatDelay) ) return false;
		repeating = true;
		sinceMoved = 0;
		return true;
	}

	void Hold( Vector2 direction )
	{
		heldDirection = direction;
		repeating = false;
		sinceMoved = 0;
	}

	/// <summary> The D-pad, else the left stick, as one of the four directions (y down, like the screen). </summary>
	Vector2 Direction()
	{
		if ( Down( up ) ) return new Vector2( 0, -1 );
		if ( Down( down ) ) return new Vector2( 0, 1 );
		if ( Down( left ) ) return new Vector2( -1, 0 );
		if ( Down( right ) ) return new Vector2( 1, 0 );

		var x = Input.GetAnalog( InputAnalog.LeftStickX );
		var y = Input.GetAnalog( InputAnalog.LeftStickY );
		if ( MathF.Max( MathF.Abs( x ), MathF.Abs( y ) ) < StickThreshold ) return Vector2.Zero;
		return MathF.Abs( x ) > MathF.Abs( y ) ? new Vector2( MathF.Sign( x ), 0 ) : new Vector2( 0, MathF.Sign( y ) );
	}

	/// <summary> For probes: one move as if the D-pad was pushed that way (left/right adjusts first, as on the pad). </summary>
	internal void ProbeStep( Panel root, Vector2 direction )
	{
		var controls = root.Descendants.Where( p => p.HasClass( "nav" ) && p.IsVisible ).ToList();
		if ( Focused is null || !controls.Contains( Focused ) ) Focus( controls.FirstOrDefault() );
		if ( Focused is not null ) Step( controls, direction );
	}

	void Step( List<Panel> controls, Vector2 direction )
	{
		// left/right on an adjustable control changes it rather than leaving it
		if ( direction.y == 0 && Adjust is not null && Adjust( Focused, (int)direction.x ) ) return;

		// Up/down: the nearest row that way, then the control in it closest to straight ahead - so down from the play
		// button lands on the row below it, not on a control further down that happens to be under it.
		// Left/right: the next control in the same row (so right from the mute button is the volume slider, not a tab
		// above that's only a little to the right); nothing further that way in the row, it stays.
		// A control counts as "that way" only if it's wholly past our edge: centres alone put a smaller button beside the
		// big play button "above" it.
		var rect = Focused.Box.Rect;
		var from = rect.Center;
		const float slack = 4;
		bool Beyond( Rect r ) =>
			direction.y < 0 ? r.Bottom <= rect.Top + slack :
			direction.y > 0 ? r.Top >= rect.Bottom - slack :
			direction.x < 0 ? r.Right <= rect.Left + slack :
			r.Left >= rect.Right - slack;

		var ahead = controls.Where( p => p != Focused && Beyond( p.Box.Rect ) )
			.Select( p => (panel: p, d: p.Box.Rect.Center - from) )
			.Select( x => (x.panel, along: Vector2.Dot( x.d, direction ), across: MathF.Abs( x.d.x * direction.y - x.d.y * direction.x )) )
			.ToList();

		if ( direction.y == 0 )
		{
			var sameRow = ahead.Where( x => x.panel.Box.Rect.Top < rect.Bottom && x.panel.Box.Rect.Bottom > rect.Top ).ToList();
			if ( sameRow.Count > 0 ) Focus( sameRow.OrderBy( x => x.along ).First().panel );
			return;
		}

		if ( ahead.Count == 0 ) return;

		var nearest = ahead.Min( x => x.along );
		var rowSlack = Math.Max( 8, (direction.y != 0 ? Focused.Box.Rect.Height : Focused.Box.Rect.Width) * 0.5f );
		Focus( ahead.Where( x => x.along <= nearest + rowSlack ).OrderBy( x => x.across ).First().panel );
	}

	void Focus( Panel panel )
	{
		Focused = panel;
		if ( panel is null ) return;

		// bring it into view in a scrolling list
		for ( var parent = panel.Parent; parent is not null; parent = parent.Parent )
			if ( parent.ScrollIntoView( panel.Box.Rect ) ) break;
	}

	static Panel Nearest( List<Panel> controls, Vector2 point )
		=> point == default ? null : controls.OrderBy( p => (p.Box.Rect.Center - point).LengthSquared ).FirstOrDefault();
}
