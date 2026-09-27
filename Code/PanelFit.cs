namespace Bimp;

/// <summary>
/// Keeps a world panel's UI laid out at a fixed reference resolution, scaled to whatever size the panel is.
/// <para>
/// Our screen UI is styled in pixels for a 1920x1080 panel. On a smaller <see cref="WorldPanel.PanelSize"/>
/// there are fewer pixels, so text and controls would get cut off. <see cref="WorldPanel.RenderScale"/>
/// changes how many layout pixels a panel has without changing its size in the world
/// (layout bounds = PanelSize / RenderScale, drawn scaled by RenderScale), so setting it to
/// PanelSize / reference / (the world panel's UI scale, 2) makes the UI lay out exactly as designed,
/// just smaller or bigger.
/// </para>
/// </summary>
public static class PanelFit
{
	/// <summary>
	/// Scale for a panel of <paramref name="panelSize"/> to fit a UI designed for <paramref name="reference"/>.
	/// Uses the tighter axis, so the whole design always fits - an unusual aspect ratio just gets more room on the other axis.
	/// </summary>
	public static float ScaleFor( Vector2 panelSize, Vector2 reference )
	{
		if ( panelSize.x <= 0 || panelSize.y <= 0 || reference.x <= 0 || reference.y <= 0 ) return 1.0f;
		return MathF.Min( panelSize.x / reference.x, panelSize.y / reference.y );
	}

	/// <summary>
	/// The bounds of <paramref name="model"/> in <paramref name="space"/>'s local space (unscaled by its world scale).
	/// </summary>
	public static BBox LocalBounds( GameObject space, ModelRenderer model )
	{
		var corners = model.Model.Bounds.Corners
			.Select( c => space.WorldTransform.PointToLocal( model.WorldTransform.PointToWorld( c ) ) );
		return BBox.FromPoints( corners );
	}

	/// <summary>
	/// Size the WorldPanel on <paramref name="component"/>'s GameObject to the front face of <paramref name="model"/>
	/// (its extent along the panel's local Y/Z plane), minus <paramref name="bezel"/> on each side.
	/// Optionally fits a BoxCollider on the same object to the model too, so USE hits the whole screen.
	/// Returns the model's local bounds, or null if nothing could be fitted.
	/// </summary>
	public static BBox? FitToModel( Component component, ModelRenderer model, float bezel, bool fitCollider )
	{
		var panel = component.Components.Get<Sandbox.WorldPanel>();
		if ( panel is null || !model.IsValid() || model.Model is null ) return null;
		if ( model.Model.Bounds.Size.Length <= 0.01f ) return null;

		var bounds = LocalBounds( component.GameObject, model );

		// The panel is drawn in our local Y/Z plane, PanelSize pixels * ScreenToWorldScale units, and scaled
		// by our world scale - the same as these local bounds - so this matches the model at any object scale.
		var w = bounds.Size.y - bezel * 2;
		var h = bounds.Size.z - bezel * 2;
		if ( w < 1 || h < 1 ) return null;

		var size = new Vector2( w, h ) / Sandbox.UI.WorldPanel.ScreenToWorldScale;
		if ( (panel.PanelSize - size).Length > 0.5f )
			panel.PanelSize = size;

		if ( fitCollider && component.Components.Get<BoxCollider>() is BoxCollider box )
		{
			// Cover the model's face, and in depth from the model to just in front of the panel
			var minX = MathF.Min( bounds.Mins.x, -1 );
			var maxX = MathF.Max( bounds.Maxs.x, 1 );
			var center = new Vector3( (minX + maxX) * 0.5f, bounds.Center.y, bounds.Center.z );
			var scale = new Vector3( maxX - minX, bounds.Size.y, bounds.Size.z );

			if ( (box.Center - center).Length > 0.05f ) box.Center = center;
			if ( (box.Scale - scale).Length > 0.05f ) box.Scale = scale;
		}

		return bounds;
	}

	/// <summary>
	/// Set the WorldPanel on <paramref name="component"/>'s GameObject to fit <paramref name="reference"/>.
	/// </summary>
	public static void Apply( Component component, Vector2 reference )
	{
		var panel = component.Components.Get<Sandbox.WorldPanel>();
		if ( panel is null ) return;

		// World panels boost their UI scale (Sandbox.UI.WorldPanel sets RootPanel.Scale = 2), so every CSS
		// pixel is 2 layout pixels. Divide it out, so DesignSize is in real CSS pixels and the UI is drawn
		// at the size it's designed for instead of twice as big.
		var uiScale = (panel.GetPanel() as Sandbox.UI.RootPanel)?.Scale ?? 2.0f;
		if ( uiScale <= 0 ) uiScale = 1;

		var scale = ScaleFor( panel.PanelSize, reference ) / uiScale;
		if ( scale > 0 && MathF.Abs( panel.RenderScale - scale ) > 0.0001f )
			panel.RenderScale = scale;
	}
}
