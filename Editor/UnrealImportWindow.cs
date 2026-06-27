using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox;

namespace Editor.UnrealImporter;

/// <summary>
/// Editor tool: pick an Unreal project folder, tick the static meshes to bring over, and export
/// them to sbox (FBX + vmat + vmdl) via a headless Unreal pass + kv3 generation.
///
/// TODO: max texture resolution selection
/// TODO: make async with progress bar
/// TODO: tint masks
/// </summary>
[EditorApp( "Unreal Importer", "move_to_inbox", "Import Unreal / Fab static meshes into s&box" )]
public class UnrealImportWindow : Widget
{
	class MeshEntry
	{
		public string GamePath;     // /Game/.../SM_X
		public string AbsPath;      // ...\Content\...\SM_X.uasset
		public Checkbox Check;
	}

	string uprojectPath;
	string uprojectFolder;
	string outputFolder;

	readonly List<MeshEntry> entries = new();

	Label projectLabel;
	Label outputLabel;
	Label statusLabel;
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
			exportButton = new Button.Primary( "Export to s&box", "move_to_inbox", this ) { Clicked = DoExport };
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
			foreach ( var file in Directory.EnumerateFiles( content, "*.uasset", SearchOption.AllDirectories ) )
			{
				// Heuristic: meshes live in a "Meshes" folder and/or are named SM_*.
				var dir = Path.GetDirectoryName( file ) ?? "";
				var name = Path.GetFileNameWithoutExtension( file );
				bool looksLikeMesh = dir.Replace( '\\', '/' ).Contains( "/Meshes", StringComparison.OrdinalIgnoreCase )
					|| name.StartsWith( "SM_", StringComparison.OrdinalIgnoreCase );
				if ( !looksLikeMesh )
					continue;

				entries.Add( new MeshEntry
				{
					AbsPath = file,
					GamePath = HeadlessExporter.ToGamePath( uprojectFolder, file ),
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
		statusLabel.Text = $"{entries.Count} static mesh(es) found.";
	}

	void RebuildList()
	{
		var canvas = new Widget( listScroll );
		canvas.Layout = Layout.Column();
		canvas.Layout.Margin = 4;
		canvas.Layout.Spacing = 2;

		if ( entries.Count == 0 )
		{
			canvas.Layout.Add( new Label( "Pick a project to list its meshes.", canvas ) );
		}
		else
		{
			foreach ( var e in entries )
			{
				// Show the path relative to /Game for readability.
				var display = e.GamePath.StartsWith( "/Game/" ) ? e.GamePath["/Game/".Length..] : e.GamePath;
				e.Check = new Checkbox( display, canvas ) { Value = true };
				canvas.Layout.Add( e.Check );
			}
		}

		canvas.Layout.AddStretchCell();
		listScroll.Canvas = canvas;
	}

	void SetAll( bool on )
	{
		foreach ( var e in entries )
			if ( e.Check is not null )
				e.Check.Value = on;
	}

	void UpdateExportEnabled()
	{
		if ( exportButton is not null )
			exportButton.Enabled = entries.Count > 0 && !string.IsNullOrEmpty( outputFolder ) && !string.IsNullOrEmpty( uprojectPath );
	}

	void DoExport()
	{
		var selected = entries.Where( e => e.Check is not null && e.Check.Value ).Select( e => e.GamePath ).ToList();
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

		statusLabel.Text = $"Exporting {selected.Count} mesh(es) via headless Unreal... this can take a minute.";

		try
		{
			var export = HeadlessExporter.Run( editorCmd, uprojectPath, selected, script );
			if ( !export.Success )
			{
				EditorUtility.DisplayDialog( "Export failed", export.Error ?? "Unknown error." );
				statusLabel.Text = "Export failed.";
				return;
			}

			var manifest = ImportManifest.Load( export.ManifestPath );
			var summary = AssetImporter.Import( manifest, export.StagingDir, outputFolder, flatCheckbox is not null && flatCheckbox.Value );

			var msg = $"Imported {summary.Models} model(s), {summary.Materials} material(s), {summary.Textures} texture(s).\n\n" +
				$"Output:\n{summary.OutputDir}";
			if ( summary.Warnings.Count > 0 )
				msg += "\n\nWarnings:\n - " + string.Join( "\n - ", summary.Warnings.Take( 10 ) );

			EditorUtility.DisplayDialog( "Import complete", msg );
			statusLabel.Text = $"Done: {summary.Models} models, {summary.Materials} materials, {summary.Textures} textures.";
		}
		catch ( Exception e )
		{
			EditorUtility.DisplayDialog( "Import error", e.ToString() );
			statusLabel.Text = "Import error.";
		}
	}
}
