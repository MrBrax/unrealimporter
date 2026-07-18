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
		public bool Selected = true;
	}

	/// <summary>
	/// The thumbnail Unreal embedded in the .uasset, loaded lazily off the main thread.
	/// Shows a placeholder icon until resolved; assets without a thumbnail keep it.
	/// </summary>
	class ThumbnailWidget : Widget
	{
		const int ThumbSize = 40;

		Pixmap pixmap;
		bool resolved;

		public ThumbnailWidget( string absPath, Widget parent ) : base( parent )
		{
			FixedSize = ThumbSize;

			if ( UassetThumbnail.TryGetCached( absPath, out pixmap ) )
			{
				resolved = true;
				return;
			}

			_ = Resolve( absPath );
		}

		async Task Resolve( string absPath )
		{
			var pm = await UassetThumbnail.LoadAsync( absPath );

			// The list rebuilds on every keystroke, so this row may be gone by now.
			if ( !IsValid )
				return;

			pixmap = pm;
			resolved = true;
			Update();
		}

		protected override void OnPaint()
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.ControlBackground );
			Paint.DrawRect( LocalRect, 3 );

			if ( pixmap is not null )
			{
				Paint.Draw( LocalRect, pixmap, 1, 3 );
				return;
			}

			Paint.SetPen( Theme.TextControl.WithAlpha( resolved ? 0.25f : 0.1f ) );
			Paint.DrawIcon( LocalRect, "view_in_ar", 20 );
		}
	}

	string uprojectPath;
	string uprojectFolder;
	string outputFolder;
	string searchFilter = "";

	readonly List<MeshEntry> entries = new();

	Label projectLabel;
	Label outputLabel;
	Label statusLabel;
	LineEdit searchEdit;
	ScrollArea listScroll;
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
				RebuildList();
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

		// Mesh list
		listScroll = new ScrollArea( this );
		Layout.Add( listScroll, 1 );
		RebuildList();

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

		Width = 560;
		MinimumWidth = 460;
		Height = 640;

		Show();
		Focus();

		var outputPath = EditorCookie.Get( "unreal_import_project_path", "" );
		if (!string.IsNullOrEmpty( outputPath )  )
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

		var content = Path.Combine( uprojectFolder, "Content" );
		if ( Directory.Exists( content ) )
		{
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
		RebuildList();
		UpdateExportEnabled();
		UpdateStatus();
	}

	/// <summary>Entries matching the current search box, in list order.</summary>
	IEnumerable<MeshEntry> Filtered()
	{
		if ( string.IsNullOrWhiteSpace( searchFilter ) )
			return entries;

		var term = searchFilter.Trim();
		return entries.Where( e => e.Display.Contains( term, StringComparison.OrdinalIgnoreCase ) );
	}

	static string FormatSize( long bytes )
	{
		if ( bytes >= 1024L * 1024 * 1024 ) return $"{bytes / (1024f * 1024 * 1024):0.##} GB";
		if ( bytes >= 1024 * 1024 ) return $"{bytes / (1024f * 1024):0.#} MB";
		if ( bytes >= 1024 ) return $"{bytes / 1024f:0} KB";
		return $"{bytes} B";
	}

	void RebuildList()
	{
		var canvas = new Widget( listScroll );
		canvas.Layout = Layout.Column();
		canvas.Layout.Margin = 4;
		canvas.Layout.Spacing = 2;

		var visible = Filtered().ToList();

		if ( entries.Count == 0 )
		{
			canvas.Layout.Add( new Label( "Pick a project to list its meshes.", canvas ) );
		}
		else if ( visible.Count == 0 )
		{
			canvas.Layout.Add( new Label( $"No meshes match \"{searchFilter.Trim()}\".", canvas ) );
		}
		else
		{
			foreach ( var e in visible )
			{
				// Selection lives on the entry, not the checkbox - the list is rebuilt on every
				// keystroke, so ticks would otherwise be lost as soon as an entry is filtered out.
				var entry = e;

				var row = new Widget( canvas );
				row.Layout = Layout.Row();
				row.Layout.Spacing = 8;

				row.Layout.Add( new ThumbnailWidget( entry.AbsPath, row ) );

				var check = new Checkbox( entry.Display, row ) { Value = entry.Selected };
				check.Toggled = () =>
				{
					entry.Selected = check.Value;
					UpdateStatus();
				};
				row.Layout.Add( check, 1 );

				var size = new Label( FormatSize( entry.SizeBytes ), row ) { WordWrap = false };
				size.Color = Theme.TextControl.WithAlpha( 0.5f );
				row.Layout.Add( size );

				canvas.Layout.Add( row );
			}
		}

		canvas.Layout.AddStretchCell();
		listScroll.Canvas = canvas;
	}

	/// <summary>Ticks or unticks everything currently shown - the search filter narrows this.</summary>
	void SetAll( bool on )
	{
		foreach ( var e in Filtered() )
			e.Selected = on;

		RebuildList();
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

		text += selected.Count > 0
			? $"  {selected.Count} selected ({FormatSize( selected.Sum( e => e.SizeBytes ) )})."
			: "  Nothing selected.";

		statusLabel.Text = text;
	}

	void UpdateExportEnabled()
	{
		if ( exportButton is not null )
			exportButton.Enabled = entries.Count > 0 && !string.IsNullOrEmpty( outputFolder ) && !string.IsNullOrEmpty( uprojectPath );
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

		var script = HeadlessExporter.FindExportScript();
		if ( script is null )
		{
			EditorUtility.DisplayDialog( "Export script missing", "Could not find Tools/ue_export.py in this library." );
			return;
		}

		var engineVersion = UnrealLocator.ReadEngineAssociation( uprojectPath );
		var editorCmd = UnrealLocator.FindEditorCmd( engineVersion );
		if ( editorCmd is null )
		{
			EditorUtility.DisplayDialog( "Unreal not found",
				$"Couldn't locate UnrealEditor-Cmd.exe for engine '{engineVersion}'.\nIs Unreal installed under Epic Games?" );
			return;
		}

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
