using Sandbox;

namespace Editor.UnrealImporter;

/// <summary>
/// Shared colours + painting helpers for the importer window.
///
/// The editor's dark theme sets ControlBackground and WindowBackground to the SAME value
/// (#181818), so a stock LineEdit or ComboBox is painted exactly the colour of the window
/// behind it and reads as loose text rather than a field. These helpers derive contrasting
/// tones by lerping towards the theme's surface colours, so they still track a custom theme
/// instead of hardcoding greys.
/// </summary>
public static class ImportStyle
{
	/// <summary>Section/panel fill - a step up from the window background.</summary>
	public static Color Panel => Color.Lerp( Theme.WindowBackground, Theme.SurfaceBackground, 0.22f );

	/// <summary>Input field fill - a further step up, so fields read as sunken boxes.</summary>
	public static Color Input => Color.Lerp( Theme.WindowBackground, Theme.SurfaceBackground, 0.45f );

	/// <summary>Alternating tree row tint (matches the editor's own scene tree).</summary>
	public static Color RowStripe => Theme.SurfaceLightBackground.WithAlpha( 0.06f );

	/// <summary>Give a text field / combo a visible box, since the theme's default is invisible.</summary>
	public static T StyleInput<T>( this T widget ) where T : Widget
	{
		widget.SetStyles(
			$"background-color: {Input.Hex};" +
			$"border: 1px solid {Theme.Border.WithAlpha( 0.5f ).Hex};" +
			$"border-radius: {Theme.ControlRadius}px;" );

		return widget;
	}

	/// <summary>
	/// Row background for a tree item: selection, then hover, then a zebra stripe. Spans the
	/// full width of the view rather than the (indented) item rect, so nested rows still
	/// stripe in line with their parents.
	/// </summary>
	public static void PaintRow( VirtualWidget item, TreeView tree )
	{
		var full = item.Rect;
		full.Left = 0;
		if ( tree.IsValid() )
			full.Right = tree.Width;

		Paint.ClearPen();

		if ( item.Selected || item.Pressed )
			Paint.SetBrush( Theme.SelectedBackground.WithAlpha( 0.9f ) );
		else if ( item.Hovered )
			Paint.SetBrush( Theme.SelectedBackground.WithAlpha( 0.25f ) );
		else if ( item.Row % 2 == 0 )
			Paint.SetBrush( RowStripe );
		else
			return;

		Paint.DrawRect( full );
	}
}
