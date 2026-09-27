using System;
using Sandbox.UI;
using Sandbox.UI.Construct;

namespace Bimp;

/// <summary>
/// One line of text that scrolls sideways when it doesn't fit, for titles. It waits on the start, scrolls until its end
/// is in view, waits there, and scrolls back - over and over. Text that fits just sits still. Style it like a label
/// (font, colour) and give it a width to fit in - in a row, `flex-grow: 1; flex-basis: 0` (sized from its content, a
/// long title would squash everything beside it).
/// </summary>
[StyleSheet.Inline( "bimp-scrollingtext", Styles )]
public sealed class ScrollingText : Panel
{
	const string Styles = """
		.scrollingtext { flex-direction: row; overflow: hidden; white-space: nowrap; min-width: 0; }
		.scrollingtext > .line { white-space: nowrap; flex-shrink: 0; }
		""";

	/// <summary> Scroll speed, in the panel's own pixels (so it looks the same at any scale). </summary>
	const float PixelsPerSecond = 80;

	/// <summary> How long it rests at each end. </summary>
	const float HoldSeconds = 2.5f;

	readonly Label line;

	float offset;
	int direction = 1; // 1 = towards the end, -1 = back to the start
	RealTimeSince sinceHold;
	bool holding = true;

	public ScrollingText()
	{
		AddClass( "scrollingtext" );
		line = Add.Label( "", "line" );
	}

	public string Text
	{
		get => line.Text;
		set
		{
			value ??= "";
			if ( value == line.Text ) return;
			line.Text = value;
			Restart();
		}
	}

	/// <summary> For probes: widths in our own pixels, whether it's scrolling, and how far. </summary>
	internal string Describe() => $"box {Box.Rect.Width * ScaleFromScreen:0}px, text {line.Box.Rect.Width * ScaleFromScreen:0}px, overflow {ComputedStyle?.OverflowX}, classes [{Classes}], offset {offset:0}, direction {direction}, holding {holding}";

	/// <summary> Back to the start of the text, and wait there. </summary>
	void Restart()
	{
		offset = 0;
		direction = 1;
		Hold();
		line.Style.MarginLeft = 0;
	}

	void Hold()
	{
		holding = true;
		sinceHold = 0;
	}

	public override void Tick()
	{
		base.Tick();

		// Box rects are in screen pixels; offsets are in our own
		var width = Box.Rect.Width * ScaleFromScreen;
		var textWidth = line.Box.Rect.Width * ScaleFromScreen;
		var travel = textWidth - width;
		var overflowing = width > 0 && travel > 1;
		SetClass( "overflowing", overflowing );

		if ( !overflowing )
		{
			if ( offset != 0 || direction != 1 ) Restart();
			return;
		}

		if ( holding )
		{
			if ( sinceHold < HoldSeconds ) return;
			holding = false;
		}

		offset += direction * PixelsPerSecond * RealTime.Delta;

		// the end is in view (or the start again): wait, then go the other way
		if ( offset >= travel || offset <= 0 )
		{
			offset = Math.Clamp( offset, 0, travel );
			direction = -direction;
			Hold();
		}

		line.Style.MarginLeft = Length.Pixels( -offset );
	}
}
