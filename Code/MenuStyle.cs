using Sandbox.UI;

namespace Bimp;

/// <summary>
/// A drop down's menu is a popup in the root panel, where a component's stylesheet doesn't reach - it came up in the
/// engine's default look (blue-grey, pale text, square corners) next to our dark rounded remote. Menus opened from our
/// panel get their own sheet, matching the remote: the same dark window and accent, hover and the pad's selection
/// ("pad-selected", see <see cref="PadNavigation"/>) drawn like the rest of our controls, and the current choice marked.
/// </summary>
public static class MenuStyle
{
	const string Styles = """
		.popup-panel.bimp-menu { background-color: rgba( 14, 17, 23, 0.98 ); border: 1px solid rgba( 255, 255, 255, 0.12 ); border-radius: 8px; box-shadow: 0 10px 40px rgba( 0, 0, 0, 0.6 ); color: white; font-family: Poppins; font-size: 14px; padding: 4px; margin-top: 4px; min-width: 190px; }
		.popup-panel.bimp-menu button { padding: 6px 10px; border-radius: 6px; font-weight: 400; color: rgba( 255, 255, 255, 0.85 ); white-space: nowrap; }
		.popup-panel.bimp-menu button.current { background-color: rgba( 63, 169, 255, 0.18 ); color: #3fa9ff; font-weight: 600; }
		.popup-panel.bimp-menu button:hover { background-color: rgba( 63, 169, 255, 0.3 ); color: white; }
		.popup-panel.bimp-menu button.pad-selected { background-color: rgba( 63, 169, 255, 0.3 ); box-shadow: 0 0 0 2px #3fa9ff; }
		""";

	/// <summary> Call every frame: styles any menu opened from <paramref name="owner"/> that isn't yet. </summary>
	public static void Apply( Panel owner )
	{
		if ( owner is null ) return;
		var top = owner;
		while ( top.Parent is not null ) top = top.Parent;

		foreach ( var menu in top.Descendants.OfType<Popup>() )
		{
			if ( menu.HasClass( "bimp-menu" ) || !IsInside( menu.PopupSource, owner ) ) continue;
			menu.AddClass( "bimp-menu" );
			menu.StyleSheet.AddInline( Styles, "bimp-menu" );

			// the engine's own "active" mark for the current choice compares the values by reference and misses; ours
			// goes by the text the drop down shows
			var current = (menu.PopupSource as Button)?.Text;
			foreach ( var option in menu.Children )
				option.SetClass( "current", option is Button b && b.Text == current );
		}
	}

	static bool IsInside( Panel panel, Panel owner )
	{
		for ( var p = panel; p is not null; p = p.Parent )
			if ( p == owner ) return true;
		return false;
	}
}
