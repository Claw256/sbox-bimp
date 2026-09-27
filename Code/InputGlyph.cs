using System;
using Sandbox.UI;

namespace Bimp;

/// <summary>
/// The button for an input action, drawn the way the player's device shows it: a key cap on keyboard, the pad's own
/// button (Xbox, PlayStation, Nintendo...) on a controller. It follows them live as they switch device. Sized to the
/// text around it (1.25em tall, as wide as the glyph).
/// </summary>
[StyleSheet.Inline( "bimp-inputglyph", Styles )]
public sealed class InputGlyph : Panel
{
	const string Styles = """
		.inputglyph { height: 1.25em; aspect-ratio: 1; flex-shrink: 0; background-size: contain; background-repeat: no-repeat; background-position: center; }
		""";

	/// <summary> The input action to show the button of, e.g. "use". </summary>
	public string Action { get; set; }

	/// <summary> An analog input to show instead of an action, by <see cref="InputAnalog"/> name, e.g. "LeftStickY". </summary>
	public string Analog { get; set; }

	/// <summary> A keyboard key to show instead of an action, e.g. "escape" (keyboard glyph whatever the device). </summary>
	public string Key { get; set; }

	/// <summary> Glyph resolution. Medium (128px) stays sharp on the big world screens. </summary>
	public InputGlyphSize Size { get; set; } = InputGlyphSize.Medium;

	Texture shown;

	public InputGlyph()
	{
		AddClass( "inputglyph" );
	}

	public override void Tick()
	{
		base.Tick();

		// cheap, and the device can change any frame (the engine's own advice for glyphs)
		var texture = Glyph();
		if ( texture == shown ) return;

		shown = texture;
		Style.SetBackgroundImage( texture );
		Style.AspectRatio = texture is { Height: > 0 } ? texture.Width / (float)texture.Height : 1;
	}

	Texture Glyph()
	{
		if ( !string.IsNullOrWhiteSpace( Key ) ) return Input.Keyboard.GetGlyph( Key, Size );
		if ( Enum.TryParse<InputAnalog>( Analog, true, out var analog ) ) return Input.GetGlyph( analog, Size );
		if ( !string.IsNullOrWhiteSpace( Action ) ) return Input.GetGlyph( Action, Size, false );
		return null;
	}
}
