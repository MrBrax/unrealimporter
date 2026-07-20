using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sandbox;

namespace Editor.UnrealImporter;

/// <summary>
/// Editor tool: pick an Unreal project folder, tick the static meshes and materials to bring
/// over, and export them to sbox (FBX + vmat + vmdl) via a headless Unreal pass + kv3 generation.
///
/// Materials can be picked on their own - they import as a standalone vmat, which is the whole
/// point of surface packs (Megascans Surfaces are a material plus its textures, no mesh).
///
/// The browser is a folder tree mirroring /Game. Folder checkboxes (tri-state) tick whole
/// subtrees, maps sit inline in their folders (double-click to import), and each mesh row
/// shows the triangle count read straight from the .uasset's embedded asset-registry tags.
/// Searching flattens the tree to matches.
///
/// TODO: max texture resolution selection
/// TODO: make async with progress bar
/// </summary>
[EditorApp( "Unreal Importer", "move_to_inbox", "Import Unreal / Fab meshes and materials into s&box" )]
public class UnrealImportWindow : Widget
{
	/// <summary>What an entry turns into once imported.</summary>
	enum AssetKind
	{
		/// <summary>StaticMesh -> fbx + vmdl (+ the vmats of its slots).</summary>
		Mesh,

		/// <summary>Material / Material Instance -> a standalone vmat.</summary>
		Material,
	}

	class AssetEntry
	{
		public AssetKind Kind;
		public string GamePath;     // /Game/.../SM_X
		public string AbsPath;      // ...\Content\...\SM_X.uasset
		public string Display;      // GamePath without the /Game/ prefix
		public long SizeBytes;      // .uasset on disk (uncooked, so this is the whole asset)
		public long Triangles = -1; // meshes only: from the uasset's asset-registry tags; -1 until read
		public bool Selected;       // opt-in: nothing ticked until the user picks

		public bool IsMesh => Kind == AssetKind.Mesh;
	}

	class MapEntry
	{
		public string GamePath;     // /Game/.../Maps/Demonstration
		public string AbsPath;      // ...\Content\...\Demonstration.umap
		public string Display;
	}

	class FolderBucket
	{
		public readonly SortedSet<string> Subfolders = new( StringComparer.OrdinalIgnoreCase );
		public readonly List<AssetEntry> Assets = new();
		public readonly List<MapEntry> Maps = new();

		/// <summary>Every asset anywhere below this folder - drives the tri-state checkbox.</summary>
		public readonly List<AssetEntry> Subtree = new();
	}

	const float CheckWidth = 26;
	const float ThumbSize = 34;

	interface ICheckRow
	{
		void OnCheckClicked();
	}

	/// <summary>A row that can supply a large hover preview.</summary>
	interface IPreviewRow
	{
		Pixmap PreviewPixmap { get; }
		string PreviewCaption { get; }
	}

	/// <summary>
	/// Frameless tooltip window showing a row's embedded thumbnail at full size
	/// (Unreal stores them at 256x256; the list shrinks them to 34px).
	/// </summary>
	class ThumbPreview : Widget
	{
		const float ImageSize = 256;
		const float CaptionHeight = 20;
		const float Pad = 8;

		readonly Pixmap pixmap;
		readonly string caption;

		public object Key;

		public ThumbPreview( Pixmap pixmap, string caption, Vector2 screenPos ) : base( null )
		{
			this.pixmap = pixmap;
			this.caption = caption;

			WindowFlags = WindowFlags.ToolTip | WindowFlags.FramelessWindowHint | WindowFlags.WindowDoesNotAcceptFocus;
			FocusMode = FocusMode.None;
			TransparentForMouseEvents = true;
			ShowWithoutActivating = true;
			NoSystemBackground = true;

			Size = new Vector2( ImageSize + Pad * 2, ImageSize + CaptionHeight + Pad * 2 );
			Position = screenPos;
			Show();
		}

		protected override void OnPaint()
		{
			Paint.ClearPen();
			Paint.SetBrushAndPen( Theme.ControlBackground, Theme.Border );
			Paint.DrawRect( LocalRect );

			var img = LocalRect.Shrink( Pad );
			img.Height = ImageSize;
			Paint.Draw( img, pixmap );

			var text = LocalRect.Shrink( Pad );
			text.Top += ImageSize;
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.8f ) );
			Paint.SetDefaultFont( 7 );
			Paint.DrawText( text, caption, TextFlag.Center );
		}
	}

	/// <summary>
	/// TreeView that routes clicks on the leading checkbox column to the row, and pops a
	/// large thumbnail preview after hovering a mesh/map row briefly.
	/// </summary>
	class ImportTreeView : TreeView
	{
		ThumbPreview preview;
		object hoverNode;
		RealTimeSince hoverSince;

		public ImportTreeView( Widget parent ) : base( parent )
		{
			MouseTracking = true;
		}

		protected override bool OnItemPressed( VirtualWidget pressedItem, MouseEvent e )
		{
			if ( e.LeftMouseButton && pressedItem.Object is ICheckRow row )
			{
				var box = pressedItem.Rect;
				box.Left += IndentWidth * pressedItem.Column + ExpandWidth;
				box.Width = CheckWidth;

				if ( box.IsInside( e.LocalPosition ) )
				{
					row.OnCheckClicked();
					Update();
					return false;
				}
			}

			return base.OnItemPressed( pressedItem, e );
		}

		protected override void OnMouseMove( MouseEvent e )
		{
			base.OnMouseMove( e );

			var node = GetItemAt( e.LocalPosition )?.Object;
			if ( node == hoverNode )
				return;

			hoverNode = node;
			hoverSince = 0;

			if ( preview.IsValid() && preview.Key != node )
			{
				preview.Destroy();
				preview = null;
			}
		}

		protected override void OnMouseLeave()
		{
			base.OnMouseLeave();
			ClearPreview();
		}

		public override void OnDestroyed()
		{
			base.OnDestroyed();
			ClearPreview();
		}

		void ClearPreview()
		{
			hoverNode = null;
			preview?.Destroy();
			preview = null;
		}

		[EditorEvent.Frame]
		public void ShowPreviewWhenSettled()
		{
			if ( preview.IsValid() || hoverNode is not IPreviewRow row || hoverSince < 0.35f )
				return;

			if ( row.PreviewPixmap is null )
				return;

			// To the right of the cursor, nudged up so the image is centred on the row.
			var pos = Application.CursorPosition + new Vector2( 28, -140 );
			preview = new ThumbPreview( row.PreviewPixmap, row.PreviewCaption, pos ) { Key = hoverNode };
		}
	}

	class FolderNode : TreeNode, ICheckRow
	{
		readonly UnrealImportWindow win;
		readonly string path;   // folder path relative to /Game ("" only for the virtual root)

		public FolderNode( UnrealImportWindow win, string path )
		{
			this.win = win;
			this.path = path;
			Value = "folder:" + path;
			Height = 26;
		}

		public override bool HasChildren => win.FolderHasChildren( path );

		protected override void BuildChildren()
		{
			Clear();
			AddItems( win.BuildFolderChildNodes( path ) );
		}

		public override void OnPaint( VirtualWidget item )
		{
			ImportStyle.PaintRow( item, TreeView );
			var r = item.Rect;

			var (sel, total) = win.SubtreeSelection( path );

			var check = r;
			check.Width = CheckWidth;
			Paint.SetPen( sel > 0 ? Theme.Primary : Theme.TextControl.WithAlpha( 0.5f ) );
			Paint.DrawIcon( check, sel == 0 ? "check_box_outline_blank" : sel == total ? "check_box" : "indeterminate_check_box", 16, TextFlag.Center );

			var icon = r;
			icon.Left += CheckWidth;
			icon.Width = 22;
			Paint.SetPen( Theme.Yellow.WithAlpha( 0.8f ) );
			Paint.DrawIcon( icon, item.IsOpen ? "folder_open" : "folder", 16, TextFlag.Center );

			var meta = r;
			meta.Right -= 6;
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.4f ) );
			Paint.SetDefaultFont( 7 );
			Paint.DrawText( meta, win.SubtreeSummary( path ), TextFlag.RightCenter );

			var text = r;
			text.Left += CheckWidth + 26;
			text.Right -= 80;
			Paint.SetPen( Theme.Text );
			Paint.SetDefaultFont();
			var name = path[(path.LastIndexOf( '/' ) + 1)..];
			Paint.DrawText( text, name, TextFlag.LeftCenter );
		}

		public void OnCheckClicked()
		{
			var (sel, total) = win.SubtreeSelection( path );
			win.SetFolderSelected( path, sel < total );
		}
	}

	class AssetNode : TreeNode, ICheckRow, IPreviewRow
	{
		readonly UnrealImportWindow win;
		readonly AssetEntry entry;
		readonly bool fullPath;

		Pixmap pixmap;
		bool thumbResolved;

		/// <summary>Placeholder + material-row icon: a mesh reads as a solid, a material as a swatch.</summary>
		string Icon => entry.IsMesh ? "view_in_ar" : "palette";

		public Pixmap PreviewPixmap => pixmap;
		public string PreviewCaption => entry.Triangles >= 0
			? $"{entry.Display}  ·  {FormatCount( entry.Triangles )} tris"
			: entry.IsMesh ? entry.Display : $"{entry.Display}  ·  material";

		public AssetNode( UnrealImportWindow win, AssetEntry entry, bool fullPath )
		{
			this.win = win;
			this.entry = entry;
			this.fullPath = fullPath;
			Value = entry;
			Height = 40;

			if ( UassetThumbnail.TryGetCached( entry.AbsPath, out pixmap ) )
				thumbResolved = true;
			else
				_ = ResolveThumb();

			// Triangle counts are a mesh-only asset-registry tag.
			if ( entry.IsMesh && entry.Triangles < 0 )
				_ = ResolveStats();
		}

		async Task ResolveThumb()
		{
			pixmap = await UassetThumbnail.LoadAsync( entry.AbsPath );
			thumbResolved = true;
			TreeView?.Update();
		}

		async Task ResolveStats()
		{
			var stats = await UassetMeshStats.LoadAsync( entry.AbsPath );
			if ( stats is not null )
			{
				entry.Triangles = stats.Triangles;
				win.UpdateStatus();
			}
			TreeView?.Update();
		}

		public override void OnPaint( VirtualWidget item )
		{
			ImportStyle.PaintRow( item, TreeView );
			var r = item.Rect;

			var check = r;
			check.Width = CheckWidth;
			Paint.SetPen( entry.Selected ? Theme.Primary : Theme.TextControl.WithAlpha( 0.5f ) );
			Paint.DrawIcon( check, entry.Selected ? "check_box" : "check_box_outline_blank", 16, TextFlag.Center );

			var thumb = r;
			thumb.Left += CheckWidth;
			thumb.Width = ThumbSize;
			thumb.Top += (r.Height - ThumbSize) / 2;
			thumb.Height = ThumbSize;
			Paint.ClearPen();
			Paint.SetBrush( Theme.ControlBackground );
			Paint.DrawRect( thumb, 3 );
			if ( pixmap is not null )
			{
				Paint.Draw( thumb, pixmap, 1, 3 );
			}
			else
			{
				Paint.SetPen( Theme.TextControl.WithAlpha( thumbResolved ? 0.25f : 0.1f ) );
				Paint.DrawIcon( thumb, Icon, 18 );
			}

			var meta = r;
			meta.Right -= 6;
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.5f ) );
			Paint.SetDefaultFont( 7 );
			var label = entry.Triangles >= 0
				? $"{FormatCount( entry.Triangles )} tris · {FormatSize( entry.SizeBytes )}"
				: entry.IsMesh
					? FormatSize( entry.SizeBytes )
					: $"material · {FormatSize( entry.SizeBytes )}";
			Paint.DrawText( meta, label, TextFlag.RightCenter );

			var text = r;
			text.Left += CheckWidth + ThumbSize + 8;
			text.Right -= 120;
			Paint.SetPen( Theme.Text );
			Paint.SetDefaultFont();
			var name = fullPath ? entry.Display : entry.Display[(entry.Display.LastIndexOf( '/' ) + 1)..];
			Paint.DrawText( text, name, TextFlag.LeftCenter );
		}

		public void OnCheckClicked()
		{
			entry.Selected = !entry.Selected;
			win.UpdateStatus();
		}

		public override void OnActivated()
		{
			OnCheckClicked();
			TreeView?.Update();
		}
	}

	class MapNode : TreeNode, IPreviewRow
	{
		readonly UnrealImportWindow win;
		readonly MapEntry entry;
		readonly bool fullPath;

		Pixmap pixmap;
		bool thumbResolved;

		public Pixmap PreviewPixmap => pixmap;
		public string PreviewCaption => $"{entry.Display}  ·  map";

		public MapNode( UnrealImportWindow win, MapEntry entry, bool fullPath )
		{
			this.win = win;
			this.entry = entry;
			this.fullPath = fullPath;
			Value = entry;
			Height = 40;

			if ( UassetThumbnail.TryGetCached( entry.AbsPath, out pixmap ) )
				thumbResolved = true;
			else
				_ = ResolveThumb();
		}

		async Task ResolveThumb()
		{
			pixmap = await UassetThumbnail.LoadAsync( entry.AbsPath );
			thumbResolved = true;
			TreeView?.Update();
		}

		public override void OnPaint( VirtualWidget item )
		{
			ImportStyle.PaintRow( item, TreeView );
			var r = item.Rect;

			var icon = r;
			icon.Width = CheckWidth;
			Paint.SetPen( Theme.Green.WithAlpha( 0.8f ) );
			Paint.DrawIcon( icon, "public", 16, TextFlag.Center );

			var thumb = r;
			thumb.Left += CheckWidth;
			thumb.Width = ThumbSize;
			thumb.Top += (r.Height - ThumbSize) / 2;
			thumb.Height = ThumbSize;
			Paint.ClearPen();
			Paint.SetBrush( Theme.ControlBackground );
			Paint.DrawRect( thumb, 3 );
			if ( pixmap is not null )
			{
				Paint.Draw( thumb, pixmap, 1, 3 );
			}
			else
			{
				Paint.SetPen( Theme.TextControl.WithAlpha( thumbResolved ? 0.25f : 0.1f ) );
				Paint.DrawIcon( thumb, "public", 18 );
			}

			var meta = r;
			meta.Right -= 6;
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.4f ) );
			Paint.SetDefaultFont( 7 );
			Paint.DrawText( meta, "map · double-click to import", TextFlag.RightCenter );

			var text = r;
			text.Left += CheckWidth + ThumbSize + 8;
			text.Right -= 160;
			Paint.SetPen( Theme.Text );
			Paint.SetDefaultFont();
			var name = fullPath ? entry.Display : entry.Display[(entry.Display.LastIndexOf( '/' ) + 1)..];
			Paint.DrawText( text, name, TextFlag.LeftCenter );
		}

		public override void OnActivated()
		{
			win.DoImportMap( entry );
		}
	}

	string uprojectPath;
	string uprojectFolder;
	string outputFolder;
	string searchFilter = "";
	bool flatView;

	readonly List<AssetEntry> entries = new();
	readonly List<MapEntry> mapEntries = new();
	readonly Dictionary<string, FolderBucket> folders = new( StringComparer.OrdinalIgnoreCase );
	List<TreeNode> rootNodes = new();

	/// <summary>Width of the left-hand label column, so the settings rows line up.</summary>
	const float LabelWidth = 110;

	LineEdit projectLabel;
	LineEdit outputLabel;
	Label statusLabel;
	LineEdit searchEdit;
	ImportTreeView tree;
	Button exportButton;
	ComboBox layoutCombo;
	LineEdit subfolderEdit;
	Label subfolderLabel;
	Checkbox lodCheckbox;
	LineEdit lightScaleEdit;
	ComboBox materialOutputCombo;
	ComboBox perAssetFolderCombo;
	Label perAssetFolderLabel;

	/// <summary>Combo item order - the layout row adds items in exactly this order.</summary>
	static readonly ImportLayout[] LayoutOrder = { ImportLayout.Grouped, ImportLayout.Flat, ImportLayout.ClassicSource, ImportLayout.PerAsset };

	ImportLayout SelectedLayout => layoutCombo is null ? ImportLayout.Grouped : LayoutOrder[Math.Clamp( layoutCombo.CurrentIndex, 0, LayoutOrder.Length - 1 )];

	/// <summary>Combo item order - the material output row adds items in exactly this order.</summary>
	static readonly MaterialOutput[] MaterialOutputOrder = { MaterialOutput.Material, MaterialOutput.Terrain, MaterialOutput.Decal };

	MaterialOutput SelectedMaterialOutput => materialOutputCombo is null
		? MaterialOutput.Material
		: MaterialOutputOrder[Math.Clamp( materialOutputCombo.CurrentIndex, 0, MaterialOutputOrder.Length - 1 )];

	/// <summary>Per-asset folder-name depth: the combo index IS the depth (0 = asset's own name).</summary>
	int PerAssetFolderDepth => perAssetFolderCombo?.CurrentIndex ?? 0;

	string Subfolder() => subfolderEdit?.Text ?? "";

	/// <summary>The light-brightness multiplier from the UI, defensively parsed.</summary>
	float LightScale()
	{
		if ( lightScaleEdit is null )
			return 1f;

		return float.TryParse( lightScaleEdit.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v ) && v > 0
			? Math.Clamp( v, 0.01f, 20f )
			: 1f;
	}

	public UnrealImportWindow() : this( null ) { }

	public UnrealImportWindow( Widget parent ) : base( parent )
	{
		WindowFlags = WindowFlags.Dialog | WindowFlags.Customized | WindowFlags.WindowTitle | WindowFlags.CloseButton | WindowFlags.WindowSystemMenuHint;
		DeleteOnClose = true;
		WindowTitle = "Unreal Importer";
		SetWindowIcon( "move_to_inbox" );

		outputFolder = Sandbox.Project.Current is not null
			? Path.Combine( Sandbox.Project.Current.GetAssetsPath(), "unrealimport" )
			: null;

		Layout = Layout.Column();
		Layout.Spacing = 8;
		Layout.Margin = 16;

		Layout.Add( new WarningBox(
			"Select an Unreal project folder, tick the meshes and materials you want, and export.\n" +
			"This runs a headless Unreal pass to extract FBX + textures, then generates vmdl/vmat.\n" +
			"The editor may be unresponsive while Unreal runs.", this ) );

		// Project row
		{
			var row = Layout.Row();
			row.Spacing = 8;
			row.Add( new Label( "Unreal Project", this ) { FixedWidth = LabelWidth } );

			projectLabel = new LineEdit( this )
			{
				ReadOnly = true,
				PlaceholderText = "No Unreal project selected",
				ToolTip = "The .uproject the assets are read from",
			}.StyleInput();
			row.Add( projectLabel, 1 );
			row.Add( new Button( "Browse Project...", "folder_open", this ) { Clicked = PickProject } );
			Layout.Add( row );
		}

		// ---- Asset Selection ----
		{
			var section = new Fieldset( "Asset Selection", this );

			var toolRow = Layout.Row();
			toolRow.Spacing = 8;

			searchEdit = new LineEdit( this ) { PlaceholderText = "⌕  Search meshes and materials", ToolTip = "Filter the list by name or path" };
			searchEdit.StyleInput();
			searchEdit.TextEdited += t =>
			{
				searchFilter = t ?? "";
				RefreshTree();
				UpdateStatus();
			};
			toolRow.Add( searchEdit, 1 );

			toolRow.Add( new Button( "Select All", "done_all", this ) { Clicked = () => SetAll( true ) } );
			toolRow.Add( new Button( "Select None", "remove_done", this ) { Clicked = () => SetAll( false ) } );

			flatView = EditorCookie.Get( "unreal_import_flat_view", false );
			var flatToggle = new Checkbox( "Flat list", this )
			{
				Value = flatView,
				ToolTip = "Show every asset as one flat list instead of the folder tree",
			};
			flatToggle.Toggled = () =>
			{
				flatView = flatToggle.Value;
				EditorCookie.Set( "unreal_import_flat_view", flatView );
				RefreshTree();
			};
			toolRow.Add( flatToggle );

			section.Layout.Add( toolRow );

			tree = new ImportTreeView( this );
			tree.MultiSelect = false;
			// Sunk into the section: darker than the panel so the row stripes read against it.
			tree.SetStyles(
				$"background-color: {Theme.WindowBackground.Hex};" +
				$"border: 1px solid {Theme.Border.WithAlpha( 0.5f ).Hex};" +
				$"border-radius: {Theme.ControlRadius}px;" );
			section.Layout.Add( tree, 1 );

			// The section (and the tree inside it) takes all the leftover height.
			Layout.Add( section, 1 );
		}

		// ---- Export Settings ----
		{
			var section = new Fieldset( "Export Settings", this );

			var grid = Layout.Grid();
			grid.Spacing = 8;
			section.Layout.Add( grid );

			// Row 0: output directory, spanning the full width.
			grid.AddCell( 0, 0, new Label( "Output Directory", this ) { FixedWidth = LabelWidth } );
			outputLabel = new LineEdit( this )
			{
				ReadOnly = true,
				PlaceholderText = "No output folder selected",
				ToolTip = "Where generated assets are written",
			}.StyleInput();
			grid.AddCell( 1, 0, outputLabel, xSpan: 3 );
			grid.AddCell( 4, 0, new Button( "Output...", "drive_file_move", this ) { Clicked = PickOutput } );

			// Row 1: layout | map light brightness.
			grid.AddCell( 0, 1, new Label( "Layout", this ) { FixedWidth = LabelWidth } );

			layoutCombo = new ComboBox( this ) { MinimumWidth = 180 };
			layoutCombo.AddItem( "Grouped", icon: "folder",
				description: "<output>/models, /materials, /textures" );
			layoutCombo.AddItem( "Flat", icon: "folder_open",
				description: "Everything directly in the output folder" );
			layoutCombo.AddItem( "Classic Source", icon: "account_tree",
				description: "Assets/models/<subdir> for fbx+vmdl, Assets/materials/<subdir> for vmat+textures" );
			layoutCombo.AddItem( "Per Asset", icon: "inventory_2",
				description: "<output>/<asset>/ - each asset's model, materials and textures together" );

			var savedLayout = EditorCookie.Get( "unreal_import_layout", 0 );
			layoutCombo.CurrentIndex = Math.Clamp( savedLayout, 0, LayoutOrder.Length - 1 );
			layoutCombo.ItemChanged += () =>
			{
				EditorCookie.Set( "unreal_import_layout", layoutCombo.CurrentIndex );
				UpdateLayoutRow();
			};
			grid.AddCell( 1, 1, layoutCombo.StyleInput() );

			// Scene-light brightness: the conversion is calibrated, but UE maps lean on
			// auto-exposure that s&box doesn't have - taste (and pack) varies, so expose a knob.
			grid.AddCell( 2, 1, new Label( "Map light brightness", this ), alignment: TextFlag.RightCenter );
			lightScaleEdit = new LineEdit( this )
			{
				Text = EditorCookie.Get( "unreal_import_light_scale", 1f ).ToString( System.Globalization.CultureInfo.InvariantCulture ),
				ToolTip = "Multiplier on converted map light intensity. 1 = calibrated default; lower for moodier interiors, higher if too dark. Applies on (re)import.",
			};
			lightScaleEdit.TextEdited += _ => EditorCookie.Set( "unreal_import_light_scale", LightScale() );
			grid.AddCell( 3, 1, lightScaleEdit.StyleInput(), xSpan: 2 );

			// Row 2: subfolder | generate LODs.
			subfolderLabel = new Label( "Subfolder", this ) { FixedWidth = LabelWidth };
			grid.AddCell( 0, 2, subfolderLabel );

			subfolderEdit = new LineEdit( this )
			{
				Text = EditorCookie.Get( "unreal_import_subfolder", "unrealimport" ),
				PlaceholderText = "(none)",
				ToolTip = "Subfolder under Assets/models and Assets/materials. Leave empty to write straight into them.",
			};
			subfolderEdit.TextEdited += t =>
			{
				EditorCookie.Set( "unreal_import_subfolder", t ?? "" );
				if ( outputLabel is not null )
					outputLabel.Text = OutputDisplay();
			};
			grid.AddCell( 1, 2, subfolderEdit.StyleInput() );

			lodCheckbox = new Checkbox( "Generate LODs", this )
			{
				Value = EditorCookie.Get( "unreal_import_lods", true ),
				ToolTip = "5-level auto chain; untick for full detail at every distance",
			};
			grid.AddCell( 2, 2, lodCheckbox, xSpan: 3 );
			lodCheckbox.Toggled = () => EditorCookie.Set( "unreal_import_lods", lodCheckbox.Value );

			// Row 3: what a material picked on its own becomes.
			grid.AddCell( 0, 3, new Label( "Material Output", this ) { FixedWidth = LabelWidth } );

			materialOutputCombo = new ComboBox( this ) { MinimumWidth = 180 };
			materialOutputCombo.AddItem( "Material (.vmat)", icon: "palette",
				description: "Standard complex.shader material" );
			materialOutputCombo.AddItem( "Terrain (.tmat)", icon: "landscape",
				description: "Terrain Material - tiling ground surface with height blending" );
			materialOutputCombo.AddItem( "Decal (.decal)", icon: "approval",
				description: "Decal Definition - projected decal masked by the colour alpha" );

			materialOutputCombo.CurrentIndex = Math.Clamp(
				EditorCookie.Get( "unreal_import_material_output", 0 ), 0, MaterialOutputOrder.Length - 1 );
			materialOutputCombo.ItemChanged += () =>
			{
				EditorCookie.Set( "unreal_import_material_output", materialOutputCombo.CurrentIndex );
				UpdateStatus();
			};
			grid.AddCell( 1, 3, materialOutputCombo.StyleInput() );

			grid.AddCell( 2, 3, new Label( "Meshes always use .vmat", this )
			{
				Color = Theme.TextControl.WithAlpha( 0.5f ),
				ToolTip = "A model's material slots can't reference a terrain or decal resource, so this only applies to materials imported on their own.",
			}, xSpan: 3 );

			// Row 4: Per Asset only - which folder to name each asset's subfolder after.
			// Fab/Megascans MIs are named like "mi_sjfnbeaa"; the readable name is a couple
			// folders up (.../Fine_American_Road_sjfnbeaa/Medium/MI_sjfnbeaa).
			perAssetFolderLabel = new Label( "Folder name", this ) { FixedWidth = LabelWidth };
			grid.AddCell( 0, 4, perAssetFolderLabel );

			perAssetFolderCombo = new ComboBox( this ) { MinimumWidth = 180 };
			perAssetFolderCombo.AddItem( "Asset name", icon: "description",
				description: "Name each folder after the asset itself (e.g. mi_sjfnbeaa)" );
			perAssetFolderCombo.AddItem( "1 folder up", icon: "north",
				description: "Name it after the asset's parent folder" );
			perAssetFolderCombo.AddItem( "2 folders up", icon: "north",
				description: "Grandparent folder - the readable pack name for Fab/Megascans" );
			perAssetFolderCombo.AddItem( "3 folders up", icon: "north",
				description: "Great-grandparent folder" );

			perAssetFolderCombo.CurrentIndex = Math.Clamp( EditorCookie.Get( "unreal_import_perasset_depth", 0 ), 0, 3 );
			perAssetFolderCombo.ItemChanged += () => EditorCookie.Set( "unreal_import_perasset_depth", perAssetFolderCombo.CurrentIndex );
			grid.AddCell( 1, 4, perAssetFolderCombo.StyleInput() );

			grid.AddCell( 2, 4, new Label( "Per Asset layout only", this )
			{
				Color = Theme.TextControl.WithAlpha( 0.5f ),
				ToolTip = "Which folder each asset's subfolder is named after, when using the Per Asset layout.",
			}, xSpan: 3 );

			// Only the field columns absorb extra width; the label columns stay tight.
			grid.SetColumnStretch( 0, 3, 0, 2, 0 );

			Layout.Add( section );
			UpdateLayoutRow();
		}

		statusLabel = new Label( "", this );
		statusLabel.Color = Theme.TextControl.WithAlpha( 0.6f );
		Layout.Add( statusLabel );

		// Bottom bar
		{
			var row = Layout.Row();
			row.Margin = new Sandbox.UI.Margin( 0, 8, 0, 0 );
			row.AddStretchCell();
			exportButton = new Button.Primary( "Export to s&box", "move_to_inbox", this ) { Clicked = () => _ = DoExport() };
			exportButton.Enabled = false;
			row.Add( exportButton );
			Layout.Add( row );
		}

		Width = 640;
		MinimumWidth = 480;
		Height = 680;

		Show();
		Focus();

		var outputPath = EditorCookie.Get( "unreal_import_project_path", "" );
		if ( !string.IsNullOrEmpty( outputPath ) )
		{
			Log.Info( $"UnrealImportWindow: restoring last project path: {outputPath}" );
			uprojectPath = outputPath;
			uprojectFolder = Path.GetDirectoryName( outputPath );
			projectLabel.Text = $"{Path.GetFileName( outputPath )}  ({Path.GetFileName( uprojectFolder )})";
			ScanAssets();
		}
	}

	string OutputDisplay()
	{
		// Classic Source ignores the picked folder entirely - it writes off the Assets root.
		if ( SelectedLayout == ImportLayout.ClassicSource )
		{
			var assets = Sandbox.Project.Current?.GetAssetsPath();
			if ( string.IsNullOrEmpty( assets ) )
				return "Assets/models + Assets/materials";

			var paths = AssetImporter.ResolvePaths( outputFolder, assets, ImportLayout.ClassicSource, Subfolder() );
			return $"{paths.ModelsDir}  +  {paths.MaterialsDir}";
		}

		if ( string.IsNullOrEmpty( outputFolder ) )
			return "";

		// Per Asset fans out into a folder per asset - show that rather than implying one folder.
		return SelectedLayout == ImportLayout.PerAsset
			? Path.Combine( outputFolder, "<asset>" )
			: outputFolder;
	}

	/// <summary>
	/// The subfolder field only means anything in Classic Source; grey it out elsewhere.
	/// (Per Asset names its folders after the assets themselves, so there's nothing to type.)
	/// </summary>
	void UpdateLayoutRow()
	{
		var classic = SelectedLayout == ImportLayout.ClassicSource;
		var perAsset = SelectedLayout == ImportLayout.PerAsset;

		if ( subfolderEdit is not null )
			subfolderEdit.Enabled = classic;
		if ( subfolderLabel is not null )
			subfolderLabel.Enabled = classic;

		// The folder-name depth only matters when each asset gets its own folder.
		if ( perAssetFolderCombo is not null )
			perAssetFolderCombo.Enabled = perAsset;
		if ( perAssetFolderLabel is not null )
			perAssetFolderLabel.Enabled = perAsset;

		if ( outputLabel is not null )
			outputLabel.Text = OutputDisplay();

		UpdateExportEnabled();
	}

	void PickProject()
	{
		var fd = new FileDialog( null ) { Title = "Select Unreal Project Folder" };
		fd.SetFindDirectory();
		fd.SetModeOpen();
		if ( !fd.Execute() )
			return;

		var folder = fd.SelectedFile;
		var uproject = UnrealLocator.FindUprojectInFolder( folder );
		if ( uproject is null )
		{
			EditorUtility.DisplayDialog( "Not an Unreal project", $"No .uproject found in:\n{folder}" );
			return;
		}

		uprojectFolder = folder;
		uprojectPath = uproject;
		projectLabel.Text = $"{Path.GetFileName( uproject )}  ({Path.GetFileName( folder )})";

		EditorCookie.Set( "unreal_import_project_path", uprojectPath );
		Log.Info( $"UnrealImportWindow: storing last project path: {uprojectPath}" );

		ScanAssets();
	}

	void PickOutput()
	{
		var fd = new FileDialog( null ) { Title = "Select Output Folder (inside Assets/)", Directory = outputFolder };
		fd.SetFindDirectory();
		fd.SetModeOpen();
		if ( !string.IsNullOrEmpty( outputFolder ) )
			fd.Directory = outputFolder;
		if ( !fd.Execute() )
			return;

		outputFolder = fd.SelectedFile;
		outputLabel.Text = OutputDisplay();
		UpdateExportEnabled();
	}

	void ScanAssets()
	{
		entries.Clear();
		mapEntries.Clear();

		var content = Path.Combine( uprojectFolder, "Content" );
		if ( Directory.Exists( content ) )
		{
			foreach ( var file in new DirectoryInfo( content ).EnumerateFiles( "*.umap", SearchOption.AllDirectories ) )
			{
				var gamePath = HeadlessExporter.ToGamePath( uprojectFolder, file.FullName );
				if ( gamePath.EndsWith( ".umap", StringComparison.OrdinalIgnoreCase ) )
					gamePath = gamePath[..^".umap".Length];

				mapEntries.Add( new MapEntry
				{
					AbsPath = file.FullName,
					GamePath = gamePath,
					Display = gamePath.StartsWith( "/Game/" ) ? gamePath["/Game/".Length..] : gamePath,
				} );
			}
			mapEntries.Sort( ( a, b ) => string.CompareOrdinal( a.GamePath, b.GamePath ) );

			// FileInfo rather than plain paths so we get the size without a second stat per file.
			foreach ( var file in new DirectoryInfo( content ).EnumerateFiles( "*.uasset", SearchOption.AllDirectories ) )
			{
				var kind = ClassifyUasset( file );
				if ( kind is null )
					continue;

				var gamePath = HeadlessExporter.ToGamePath( uprojectFolder, file.FullName );

				entries.Add( new AssetEntry
				{
					Kind = kind.Value,
					AbsPath = file.FullName,
					GamePath = gamePath,
					// Show the path relative to /Game for readability.
					Display = gamePath.StartsWith( "/Game/" ) ? gamePath["/Game/".Length..] : gamePath,
					SizeBytes = file.Length,
				} );
			}
		}

		if ( entries.Count == 0 )
		{
			Log.Warning( $"No static meshes or materials found in {uprojectFolder}/Content." );
		}

		entries.Sort( ( a, b ) => string.CompareOrdinal( a.GamePath, b.GamePath ) );
		BuildFolderIndex();
		RefreshTree();
		UpdateExportEnabled();
		UpdateStatus();

		_ = WarmStats( entries.ToList() );
	}

	/// <summary>
	/// What a .uasset is, from its name and folder - null for anything we can't import.
	///
	/// Reading the real class out of the package would need version-dependent header parsing;
	/// Unreal/Fab naming is conventional enough that prefixes plus the type folder do the job.
	/// The exporter re-checks the actual type when it loads the asset, so a wrong guess here
	/// costs a warning, not a broken import.
	/// </summary>
	static AssetKind? ClassifyUasset( FileInfo file )
	{
		var dir = (file.DirectoryName ?? "").Replace( '\\', '/' );
		var name = Path.GetFileNameWithoutExtension( file.Name );

		// Name prefixes are stronger evidence than the folder - a material parked in a
		// Meshes/ folder is still a material.
		if ( name.StartsWith( "SM_", StringComparison.OrdinalIgnoreCase ) )
			return AssetKind.Mesh;

		// MI_ = Material Instance, M_/MM_ = Material (master). Textures are T_/TX_, so the
		// single-letter M_ prefix doesn't collide with anything else we'd want to list.
		if ( name.StartsWith( "MI_", StringComparison.OrdinalIgnoreCase )
			|| name.StartsWith( "M_", StringComparison.OrdinalIgnoreCase )
			|| name.StartsWith( "MM_", StringComparison.OrdinalIgnoreCase ) )
			return AssetKind.Material;

		if ( dir.Contains( "/Meshes", StringComparison.OrdinalIgnoreCase ) )
			return AssetKind.Mesh;

		if ( dir.Contains( "/Materials", StringComparison.OrdinalIgnoreCase ) )
			return AssetKind.Material;

		return null;
	}

	/// <summary>
	/// Background pass reading tri counts for everything, so folder rows and the status
	/// total become accurate without expanding every folder. Throttled inside
	/// UassetMeshStats; cached on disk so later opens are instant.
	/// </summary>
	async Task WarmStats( List<AssetEntry> list )
	{
		int done = 0;
		foreach ( var e in list )
		{
			if ( !IsValid || !entries.Contains( e ) )
				return;

			if ( e.Triangles < 0 )
			{
				var stats = await UassetMeshStats.LoadAsync( e.AbsPath );
				if ( stats is not null )
					e.Triangles = stats.Triangles;
			}

			if ( ++done % 64 == 0 )
			{
				UpdateStatus();
				tree?.Update();
			}
		}

		if ( IsValid )
		{
			UpdateStatus();
			tree?.Update();
		}
	}

	// ---- folder index ----

	static string ParentOf( string path ) => path.Contains( '/' ) ? path[..path.LastIndexOf( '/' )] : "";
	static string DirOf( string display ) => display.Contains( '/' ) ? display[..display.LastIndexOf( '/' )] : "";

	FolderBucket Bucket( string path )
	{
		if ( !folders.TryGetValue( path, out var b ) )
			folders[path] = b = new FolderBucket();
		return b;
	}

	void BuildFolderIndex()
	{
		folders.Clear();
		Bucket( "" );

		void RegisterChain( string dir )
		{
			while ( dir.Length > 0 )
			{
				var parent = ParentOf( dir );
				Bucket( parent ).Subfolders.Add( dir );
				Bucket( dir );
				dir = parent;
			}
		}

		foreach ( var e in entries )
		{
			var dir = DirOf( e.Display );
			RegisterChain( dir );
			Bucket( dir ).Assets.Add( e );

			for ( var p = dir; ; p = ParentOf( p ) )
			{
				Bucket( p ).Subtree.Add( e );
				if ( p.Length == 0 )
					break;
			}
		}

		foreach ( var m in mapEntries )
		{
			var dir = DirOf( m.Display );
			RegisterChain( dir );
			Bucket( dir ).Maps.Add( m );
		}

		rootNodes = BuildFolderChildNodes( "" ).ToList();
	}

	bool FolderHasChildren( string path )
		=> folders.TryGetValue( path, out var b ) && (b.Subfolders.Count > 0 || b.Assets.Count > 0 || b.Maps.Count > 0);

	IEnumerable<TreeNode> BuildFolderChildNodes( string path )
	{
		if ( !folders.TryGetValue( path, out var b ) )
			yield break;

		foreach ( var sub in b.Subfolders )
			yield return new FolderNode( this, sub );

		foreach ( var m in b.Maps )
			yield return new MapNode( this, m, fullPath: false );

		foreach ( var e in b.Assets )
			yield return new AssetNode( this, e, fullPath: false );
	}

	(int selected, int total) SubtreeSelection( string path )
	{
		if ( !folders.TryGetValue( path, out var b ) )
			return (0, 0);

		int sel = 0;
		foreach ( var e in b.Subtree )
			if ( e.Selected )
				sel++;

		return (sel, b.Subtree.Count);
	}

	/// <summary>Right-hand folder label: "12 meshes · 3 materials", omitting whichever is zero.</summary>
	string SubtreeSummary( string path )
	{
		if ( !folders.TryGetValue( path, out var b ) )
			return "";

		int meshes = b.Subtree.Count( e => e.IsMesh );
		int mats = b.Subtree.Count - meshes;

		var parts = new List<string>();
		if ( meshes > 0 )
			parts.Add( meshes == 1 ? "1 mesh" : $"{meshes} meshes" );
		if ( mats > 0 )
			parts.Add( mats == 1 ? "1 material" : $"{mats} materials" );

		return string.Join( " · ", parts );
	}

	void SetFolderSelected( string path, bool on )
	{
		if ( !folders.TryGetValue( path, out var b ) )
			return;

		foreach ( var e in b.Subtree )
			e.Selected = on;

		UpdateStatus();
		tree?.Update();
	}

	// ---- filtering / tree ----

	/// <summary>Entries matching the current search box, in list order.</summary>
	IEnumerable<AssetEntry> Filtered()
	{
		if ( string.IsNullOrWhiteSpace( searchFilter ) )
			return entries;

		var term = searchFilter.Trim();
		return entries.Where( e => e.Display.Contains( term, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>Maps matching the current search box.</summary>
	IEnumerable<MapEntry> FilteredMaps()
	{
		if ( string.IsNullOrWhiteSpace( searchFilter ) )
			return mapEntries;

		var term = searchFilter.Trim();
		return mapEntries.Where( e => e.Display.Contains( term, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>Tree of folders normally; a flat list while searching or when toggled flat.</summary>
	void RefreshTree()
	{
		if ( tree is null )
			return;

		bool searching = !string.IsNullOrWhiteSpace( searchFilter );

		if ( !searching && !flatView )
		{
			// Persistent nodes, so folder expansion survives search/flat round-trips.
			tree.SetItems( rootNodes );

			if ( rootNodes.Count == 1 )
				tree.Open( rootNodes[0] );
		}
		else
		{
			// Filtered()/FilteredMaps() return everything when the search box is empty,
			// so this doubles as the plain flat view.
			var flat = new List<TreeNode>();
			flat.AddRange( FilteredMaps().Select( m => (TreeNode)new MapNode( this, m, fullPath: true ) ) );
			flat.AddRange( Filtered().Select( e => (TreeNode)new AssetNode( this, e, fullPath: true ) ) );

			if ( flat.Count == 0 && searching )
				flat.Add( new TreeNode( $"No matches for \"{searchFilter.Trim()}\"" ) );

			tree.SetItems( flat );
		}
	}

	static string FormatSize( long bytes )
	{
		if ( bytes >= 1024L * 1024 * 1024 ) return $"{bytes / (1024f * 1024 * 1024):0.##} GB";
		if ( bytes >= 1024 * 1024 ) return $"{bytes / (1024f * 1024):0.#} MB";
		if ( bytes >= 1024 ) return $"{bytes / 1024f:0} KB";
		return $"{bytes} B";
	}

	static string FormatCount( long n )
	{
		if ( n >= 1_000_000 ) return $"{n / 1_000_000f:0.##}M";
		if ( n >= 1_000 ) return $"{n / 1_000f:0.#}k";
		return $"{n}";
	}

	/// <summary>Ticks or unticks everything currently shown - the search filter narrows this.</summary>
	void SetAll( bool on )
	{
		foreach ( var e in Filtered() )
			e.Selected = on;

		tree?.Update();
		UpdateStatus();
	}

	void UpdateStatus()
	{
		if ( statusLabel is null )
			return;

		if ( entries.Count == 0 )
		{
			statusLabel.Text = "";
			return;
		}

		var selected = entries.Where( e => e.Selected ).ToList();
		var shown = Filtered().Count();

		int meshCount = entries.Count( e => e.IsMesh );
		var found = $"{meshCount} static mesh(es), {entries.Count - meshCount} material(s)";
		var text = shown == entries.Count ? $"{found} found." : $"{shown} of {found} shown.";

		if ( selected.Count > 0 )
		{
			text += $"  {selected.Count} selected ({FormatSize( selected.Sum( e => e.SizeBytes ) )}";

			// Tri counts only exist for meshes - "+" means some are still being read.
			long tris = selected.Sum( e => Math.Max( 0, e.Triangles ) );
			bool partial = selected.Any( e => e.IsMesh && e.Triangles < 0 );
			if ( tris > 0 )
				text += $", {FormatCount( tris )}{(partial ? "+" : "")} tris";

			text += ").";
		}
		else
		{
			text += "  Nothing selected.";
		}

		statusLabel.Text = text;
	}

	void UpdateExportEnabled()
	{
		if ( exportButton is null )
			return;

		// Classic Source writes off the Assets root, so it doesn't need a picked output folder.
		var haveOutput = SelectedLayout == ImportLayout.ClassicSource || !string.IsNullOrEmpty( outputFolder );
		exportButton.Enabled = entries.Count > 0 && haveOutput && !string.IsNullOrEmpty( uprojectPath );
	}

	/// <summary>Push a live export/import event into the progress toast + status line.</summary>
	void ApplyProgress( IProgressSection progress, ExportEvent ev )
	{
		if ( ev.Total is > 0 )
			progress.TotalCount = ev.Total.Value;
		if ( ev.Done is > 0 )
			progress.Current = ev.Done.Value;
		if ( !string.IsNullOrEmpty( ev.Message ) )
		{
			progress.Subtitle = ev.Message;
			statusLabel.Text = ev.Done is > 0 && ev.Total is > 0 ? $"[{ev.Done}/{ev.Total}] {ev.Message}" : ev.Message;
		}
	}

	/// <summary>Locate ue_export.py + the right UnrealEditor-Cmd, dialoging on failure.</summary>
	bool TryResolveTools( out string script, out string editorCmd )
	{
		editorCmd = null;
		script = HeadlessExporter.FindExportScript();
		if ( script is null )
		{
			EditorUtility.DisplayDialog( "Export script missing", "Could not find Tools/ue_export.py in this library." );
			return false;
		}

		var engineVersion = UnrealLocator.ReadEngineAssociation( uprojectPath );
		editorCmd = UnrealLocator.FindEditorCmd( engineVersion );
		if ( editorCmd is null )
		{
			EditorUtility.DisplayDialog( "Unreal not found",
				$"Couldn't locate UnrealEditor-Cmd.exe for engine '{engineVersion}'.\nIs Unreal installed under Epic Games?" );
			return false;
		}

		return true;
	}

	/// <summary>Double-clicking a map row lands here - confirm before kicking a long export.</summary>
	void DoImportMap( MapEntry map )
	{
		EditorUtility.DisplayDialog( "Import map?",
			$"Import {map.Display}?\n\nThis exports every mesh the level uses and builds a prefab of its layout. It can take a while.",
			"Cancel", "Import", () => _ = RunImportMap( map ), "🌍" );
	}

	async Task RunImportMap( MapEntry map )
	{
		if ( string.IsNullOrEmpty( outputFolder ) && SelectedLayout != ImportLayout.ClassicSource )
		{
			EditorUtility.DisplayDialog( "No output folder", "Pick an output folder (inside Assets/) first." );
			return;
		}

		if ( !TryResolveTools( out var script, out var editorCmd ) )
			return;

		await Task.Delay( 100 );

		statusLabel.Text = $"Importing map {map.Display}... this exports every mesh the level uses and can take a while.";

		using var progress = Application.Editor.ProgressSection();
		progress.Title = $"Exporting map {map.Display}";
		var progressToken = progress.GetCancel();

		try
		{
			var export = await HeadlessExporter.Run( editorCmd, uprojectPath, Enumerable.Empty<string>(), script, progressToken, mapGamePath: map.GamePath,
				onProgress: ev => ApplyProgress( progress, ev ) );
			if ( !export.Success )
			{
				EditorUtility.DisplayDialog( "Map export failed", export.Error ?? "Unknown error.", icon: "⚠️" );
				statusLabel.Text = "Map export failed.";
				return;
			}

			progress.Title = $"Importing map {map.Display}";
			var manifest = ImportManifest.Load( export.ManifestPath );
			var summary = await AssetImporter.Import( manifest, export.StagingDir, outputFolder, progressToken, SelectedLayout, Subfolder(),
				generateLods: lodCheckbox is null || lodCheckbox.Value,
				lightScale: LightScale(),
				materialOutput: SelectedMaterialOutput,
				perAssetFolderDepth: PerAssetFolderDepth,
				onProgress: ( done, total, name ) => ApplyProgress( progress, new ExportEvent( done, total, $"Importing {name}" ) ) );

			var msg = $"Imported {summary.Models} model(s), {summary.Materials} material(s), {summary.Textures} texture(s).\n" +
				$"{summary.Placements} placement(s) written to:\n{summary.PrefabPath}";
			if ( summary.Warnings.Count > 0 )
				msg += "\n\nWarnings:\n - " + string.Join( "\n - ", summary.Warnings.Take( 10 ) );

			EditorUtility.DisplayDialog( "Map import complete", msg, icon: "✅" );
			statusLabel.Text = $"Done: {summary.Placements} placements, {summary.Models} models.";
		}
		catch ( Exception e )
		{
			EditorUtility.DisplayDialog( "Map import error", e.ToString(), icon: "⚠️" );
			statusLabel.Text = "Map import error.";
		}
	}

	async Task DoExport()
	{
		// Deliberately ignores the search filter - ticks persist across filtering, so everything
		// the user has selected gets exported whether or not it's on screen right now.
		var selectedEntries = entries.Where( e => e.Selected ).ToList();
		var selected = selectedEntries.Select( e => e.GamePath ).ToList();
		if ( selected.Count == 0 )
		{
			EditorUtility.DisplayDialog( "Nothing selected", "Tick at least one mesh or material to export." );
			return;
		}

		if ( !TryResolveTools( out var script, out var editorCmd ) )
			return;

		await Task.Delay( 100 );

		// Meshes and materials go over in one selection - the export script routes by asset type.
		int meshCount = selectedEntries.Count( e => e.IsMesh );
		var what = meshCount == selected.Count ? $"{meshCount} mesh(es)"
			: meshCount == 0 ? $"{selected.Count} material(s)"
			: $"{meshCount} mesh(es) + {selected.Count - meshCount} material(s)";
		statusLabel.Text = $"Exporting {what} via headless Unreal... this can take a minute.";

		using var progress = Application.Editor.ProgressSection();

		progress.Title = "Exporting from Unreal";
		progress.TotalCount = selected.Count;
		var progressToken = progress.GetCancel();

		try
		{
			var export = await HeadlessExporter.Run( editorCmd, uprojectPath, selected, script, progressToken,
				onProgress: ev => ApplyProgress( progress, ev ) );
			if ( !export.Success )
			{
				EditorUtility.DisplayDialog( "Export failed", export.Error ?? "Unknown error.", icon: "⚠️" );
				statusLabel.Text = "Export failed.";
				return;
			}

			progress.Title = "Importing into s&box";
			var manifest = ImportManifest.Load( export.ManifestPath );
			var summary = await AssetImporter.Import( manifest, export.StagingDir, outputFolder, progressToken, SelectedLayout, Subfolder(),
				generateLods: lodCheckbox is null || lodCheckbox.Value,
				lightScale: LightScale(),
				materialOutput: SelectedMaterialOutput,
				perAssetFolderDepth: PerAssetFolderDepth,
				onProgress: ( done, total, name ) => ApplyProgress( progress, new ExportEvent( done, total, $"Importing {name}" ) ) );

			var msg = $"Imported {summary.Models} model(s), {summary.Materials} material(s), {summary.Textures} texture(s).\n\n" +
				$"Output:\n{summary.OutputDir}";
			if ( summary.Warnings.Count > 0 )
				msg += "\n\nWarnings:\n - " + string.Join( "\n - ", summary.Warnings.Take( 10 ) );

			EditorUtility.DisplayDialog( "Import complete", msg, icon: "✅" );
			statusLabel.Text = $"Done: {summary.Models} models, {summary.Materials} materials, {summary.Textures} textures.";
		}
		catch ( Exception e )
		{
			EditorUtility.DisplayDialog( "Import error", e.ToString(), icon: "⚠️" );
			statusLabel.Text = "Import error.";
		}
	}
}
