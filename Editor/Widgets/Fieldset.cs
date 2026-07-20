using Sandbox;

namespace Editor.UnrealImporter;

/// <summary>
/// A titled section box: a rounded border with its title notched into the top-left edge,
/// like an HTML fieldset/legend. Add content to <see cref="Widget.Layout"/> as usual - the
/// margins already leave room for the title and border.
/// </summary>
public class Fieldset : Widget
{
	/// <summary>Height reserved for the title row; the border runs through its middle.</summary>
	const float TitleHeight = 16;
	const float TitleInset = 10;
	const float TitlePad = 5;

	public string Title { get; set; }

	public Fieldset( string title, Widget parent ) : base( parent )
	{
		Title = title;

		Layout = Layout.Column();
		Layout.Spacing = 8;
		Layout.Margin = new Sandbox.UI.Margin( 12, TitleHeight + 10, 12, 12 );
	}

	protected override void OnPaint()
	{
		// The border starts halfway down the title so the text can sit on the line.
		var border = LocalRect.Shrink( 0.5f );
		border.Top += TitleHeight * 0.5f;

		// Fill first: the section needs to read as a raised panel, not just an outline.
		Paint.ClearPen();
		Paint.SetBrush( ImportStyle.Panel );
		Paint.DrawRect( border, 4 );

		Paint.ClearBrush();
		Paint.SetPen( Theme.Border, 1 );
		Paint.DrawRect( border, 4 );

		if ( string.IsNullOrEmpty( Title ) )
			return;

		Paint.SetDefaultFont( 8, 400 );
		var text = Paint.MeasureText( Title );

		// Punch a gap in the border so the title reads as part of the frame, not on top of it.
		// The gap straddles the border line, so each half takes the fill it sits against -
		// window background above, panel fill below.
		var gap = new Rect( TitleInset - TitlePad, border.Top - TitleHeight * 0.5f,
			text.x + TitlePad * 2, TitleHeight );

		Paint.ClearPen();

		var above = gap;
		above.Bottom = border.Top;
		Paint.SetBrush( Theme.WindowBackground );
		Paint.DrawRect( above );

		var below = gap;
		below.Top = border.Top;
		Paint.SetBrush( ImportStyle.Panel );
		Paint.DrawRect( below );

		Paint.ClearBrush();
		Paint.SetPen( Theme.Text.WithAlpha( 0.9f ) );
		Paint.DrawText( gap, Title, TextFlag.Center );
	}
}
