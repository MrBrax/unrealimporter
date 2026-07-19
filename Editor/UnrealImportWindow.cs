using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sandbox;

namespace Editor.UnrealImporter;

/// <summary>
/// Editor tool: pick an Unreal project folder, tick the static meshes to bring over, and export
/// them to sbox (FBX + vmat + vmdl) via a headless Unreal pass + kv3 generation.
///
/// The browser is a folder tree mirroring /Game. Folder checkboxes (tri-state) tick whole
/// subtrees, maps sit inline in their folders (double-click to import), and each mesh row
/// shows the triangle count read straight from the .uasset's embedded asset-registry tags.
/// Searching flattens the tree to matches.
///
/// TODO: max texture resolution selection
/// TODO: make async with progress bar
/// </summary>
[EditorApp( "Unreal Importer", "move_to_inbox", "Import Unreal / Fab static meshes into s&box" )]
public class UnrealImportWindow : Widget
{
	class MeshEntry
	{
		public string GamePath;     // /Game/.../SM_X
		public string AbsPath;      // ...\Content\...\SM_X.uasset
		public string Display;      // GamePath without the /Game/ prefix
		public long SizeBytes;      // .uasset on disk (uncooked, so this is the whole asset)
		public long Triangles = -1; // from the uasset's asset-registry tags; -1 until read
		public bool Selected = true;
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
		public readonly List<MeshEntry> Meshes = new();
		public readonly List<MapEntry> Maps = new();

		/// <summary>Every mesh anywhere below this folder - drives the tri-state checkbox.</summary>
		public readonly List<MeshEntry> Subtree = new();
	}

	const float CheckWidth = 26;
	const float ThumbSize = 34;

	interface ICheckRow
	{
		void OnCheckClicked();
	}

	/// <summary>TreeView that routes clicks on the leading checkbox column to the row.</summary>
	class ImportTreeView : TreeView
	{
		public ImportTreeView( Widget parent ) : base( parent ) { }

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
			PaintSelection( item );
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
			Paint.DrawText( meta, total == 1 ? "1 mesh" : $"{total} meshes", TextFlag.RightCenter );

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

	class MeshNode : TreeNode, ICheckRow
	{
		readonly UnrealImportWindow win;
		readonly MeshEntry entry;
		readonly bool fullPath;

		Pixmap pixmap;
		bool thumbResolved;

		public MeshNode( UnrealImportWindow win, MeshEntry entry, bool fullPath )
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

			if ( entry.Triangles < 0 )
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
			PaintSelection( item );
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
				Paint.DrawIcon( thumb, "view_in_ar", 18 );
			}

			var meta = r;
			meta.Right -= 6;
			Paint.SetPen( Theme.TextControl.WithAlpha( 0.5f ) );
			Paint.SetDefaultFont( 7 );
			var label = entry.Triangles >= 0
				? $"{FormatCount( entry.Triangles )} tris · {FormatSize( entry.SizeBytes )}"
				: FormatSize( entry.SizeBytes );
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

	class MapNode : TreeNode
	{
		readonly UnrealImportWindow win;
		readonly MapEntry entry;
		readonly bool fullPath;

		Pixmap pixmap;
		bool thumbResolved;

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
			PaintSelection( item );
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

	readonly List<MeshEntry> entries = new();
	readonly List<MapEntry> mapEntries = new();
	readonly Dictionary<string, FolderBucket> folders = new( StringComparer.OrdinalIgnoreCase );
	List<TreeNode> rootNodes = new();

	Label projectLabel;
	Label outputLabel;
	Label statusLabel;
	LineEdit searchEdit;
	ImportTreeView tree;
	Button exportButton;
	Checkbox flatCheckbox;

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
			"Select an Unreal project folder, tick the static meshes you want, and export.\n" +
			"This runs a headless Unreal pass to extract FBX + textures, then generates vmdl/vmat.\n" +
			"The editor may be unresponsive while Unreal runs.", this ) );

		// Project row
		{
			var row = Layout.Row();
			row.Spacing = 8;
			projectLabel = new Label( "No Unreal project selected", this ) { WordWrap = false };
			row.Add( projectLabel, 1 );
			var browse = new Button( "Browse Project...", "folder_open", this ) { Clicked = PickProject };
			row.Add( browse );
			Layout.Add( row );
		}

		// Search row
		{
			searchEdit = new LineEdit( this ) { PlaceholderText = "⌕  Search meshes", ToolTip = "Filter the list by name or path" };
			searchEdit.TextEdited += t =>
			{
				searchFilter = t ?? "";
				RefreshTree();
				UpdateStatus();
			};
			Layout.Add( searchEdit );
		}

		// Select all/none row
		{
			var row = Layout.Row();
			row.Spacing = 8;
			row.Add( new Button( "Select All", "done_all", this ) { Clicked = () => SetAll( true ) } );
			row.Add( new Button( "Select None", "remove_done", this ) { Clicked = () => SetAll( false ) } );
			row.AddStretchCell();
			Layout.Add( row );
		}

		// Mesh tree
		tree = new ImportTreeView( this );
		tree.MultiSelect = false;
		Layout.Add( tree, 1 );

		// Output row
		{
			var row = Layout.Row();
			row.Spacing = 8;
			outputLabel = new Label( OutputDisplay(), this ) { WordWrap = false };
			row.Add( outputLabel, 1 );
			var browse = new Button( "Output...", "drive_file_move", this ) { Clicked = PickOutput };
			row.Add( browse );
			Layout.Add( row );
		}

		flatCheckbox = new Checkbox( "Flat output (no models/materials/textures subfolders)", this ) { Value = false };
		Layout.Add( flatCheckbox );

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
			ScanMeshes();
		}
	}

	string OutputDisplay() => string.IsNullOrEmpty( outputFolder ) ? "No output folder" : $"Output: {outputFolder}";

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

		ScanMeshes();
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

	void ScanMeshes()
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
				// Heuristic: meshes live in a "Meshes" folder and/or are named SM_*.
				var dir = file.DirectoryName ?? "";
				var name = Path.GetFileNameWithoutExtension( file.Name );
				bool looksLikeMesh = dir.Replace( '\\', '/' ).Contains( "/Meshes", StringComparison.OrdinalIgnoreCase )
					|| name.StartsWith( "SM_", StringComparison.OrdinalIgnoreCase );
				if ( !looksLikeMesh )
					continue;

				var gamePath = HeadlessExporter.ToGamePath( uprojectFolder, file.FullName );

				entries.Add( new MeshEntry
				{
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
			Log.Warning( $"No static meshes found in {uprojectFolder}/Content." );
		}

		entries.Sort( ( a, b ) => string.CompareOrdinal( a.GamePath, b.GamePath ) );
		BuildFolderIndex();
		RefreshTree();
		UpdateExportEnabled();
		UpdateStatus();

		_ = WarmStats( entries.ToList() );
	}

	/// <summary>
	/// Background pass reading tri counts for everything, so folder rows and the status
	/// total become accurate without expanding every folder. Throttled inside
	/// UassetMeshStats; cached on disk so later opens are instant.
	/// </summary>
	async Task WarmStats( List<MeshEntry> list )
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
			Bucket( dir ).Meshes.Add( e );

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
		=> folders.TryGetValue( path, out var b ) && (b.Subfolders.Count > 0 || b.Meshes.Count > 0 || b.Maps.Count > 0);

	IEnumerable<TreeNode> BuildFolderChildNodes( string path )
	{
		if ( !folders.TryGetValue( path, out var b ) )
			yield break;

		foreach ( var sub in b.Subfolders )
			yield return new FolderNode( this, sub );

		foreach ( var m in b.Maps )
			yield return new MapNode( this, m, fullPath: false );

		foreach ( var e in b.Meshes )
			yield return new MeshNode( this, e, fullPath: false );
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
	IEnumerable<MeshEntry> Filtered()
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

	/// <summary>Tree of folders normally; a flat list of matches while searching.</summary>
	void RefreshTree()
	{
		if ( tree is null )
			return;

		if ( string.IsNullOrWhiteSpace( searchFilter ) )
		{
			// Persistent nodes, so folder expansion survives search round-trips.
			tree.SetItems( rootNodes );

			if ( rootNodes.Count == 1 )
				tree.Open( rootNodes[0] );
		}
		else
		{
			var flat = new List<TreeNode>();
			flat.AddRange( FilteredMaps().Select( m => (TreeNode)new MapNode( this, m, fullPath: true ) ) );
			flat.AddRange( Filtered().Select( e => (TreeNode)new MeshNode( this, e, fullPath: true ) ) );

			if ( flat.Count == 0 )
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

		var text = shown == entries.Count
			? $"{entries.Count} static mesh(es) found."
			: $"{shown} of {entries.Count} static mesh(es) shown.";

		if ( selected.Count > 0 )
		{
			text += $"  {selected.Count} selected ({FormatSize( selected.Sum( e => e.SizeBytes ) )}";

			long tris = selected.Sum( e => Math.Max( 0, e.Triangles ) );
			bool partial = selected.Any( e => e.Triangles < 0 );
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
		if ( exportButton is not null )
			exportButton.Enabled = entries.Count > 0 && !string.IsNullOrEmpty( outputFolder ) && !string.IsNullOrEmpty( uprojectPath );
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
		if ( string.IsNullOrEmpty( outputFolder ) )
		{
			EditorUtility.DisplayDialog( "No output folder", "Pick an output folder (inside Assets/) first." );
			return;
		}

		if ( !TryResolveTools( out var script, out var editorCmd ) )
			return;

		await Task.Delay( 100 );

		statusLabel.Text = $"Importing map {map.Display}... this exports every mesh the level uses and can take a while.";

		using var progress = Application.Editor.ProgressSection();
		progress.Title = $"Importing map {map.Display}";
		var progressToken = progress.GetCancel();

		try
		{
			var export = await HeadlessExporter.Run( editorCmd, uprojectPath, Enumerable.Empty<string>(), script, progressToken, mapGamePath: map.GamePath );
			if ( !export.Success )
			{
				EditorUtility.DisplayDialog( "Map export failed", export.Error ?? "Unknown error.", icon: "⚠️" );
				statusLabel.Text = "Map export failed.";
				return;
			}

			var manifest = ImportManifest.Load( export.ManifestPath );
			var summary = await AssetImporter.Import( manifest, export.StagingDir, outputFolder, progressToken, flatCheckbox is not null && flatCheckbox.Value );

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
		var selected = entries.Where( e => e.Selected ).Select( e => e.GamePath ).ToList();
		if ( selected.Count == 0 )
		{
			EditorUtility.DisplayDialog( "Nothing selected", "Tick at least one mesh to export." );
			return;
		}

		if ( !TryResolveTools( out var script, out var editorCmd ) )
			return;

		await Task.Delay( 100 );

		statusLabel.Text = $"Exporting {selected.Count} mesh(es) via headless Unreal... this can take a minute.";

		using var progress = Application.Editor.ProgressSection();

		progress.Title = "Exporting meshes via headless Unreal";
		progress.TotalCount = selected.Count;
		var progressToken = progress.GetCancel();

		try
		{
			var export = await HeadlessExporter.Run( editorCmd, uprojectPath, selected, script, progressToken );
			if ( !export.Success )
			{
				EditorUtility.DisplayDialog( "Export failed", export.Error ?? "Unknown error.", icon: "⚠️" );
				statusLabel.Text = "Export failed.";
				return;
			}

			var manifest = ImportManifest.Load( export.ManifestPath );
			var summary = await AssetImporter.Import( manifest, export.StagingDir, outputFolder, progressToken, flatCheckbox is not null && flatCheckbox.Value );

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
