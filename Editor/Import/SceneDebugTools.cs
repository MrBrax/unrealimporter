using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor.Mcp;
using Sandbox;

namespace Editor.UnrealImporter;

// TEMPORARY verification tool - delete once scene import scale is confirmed.
[McpToolset( "unrealimporter", "Unreal importer debug tools" )]
public static class SceneDebugTools
{
	/// <summary>Run AssetImporter.Import on a staging folder (manifest.json + FBX + PNG).</summary>
	/// <param name="stagingDir">Staging folder containing manifest.json.</param>
	/// <param name="outputFolder">Output folder inside the project's Assets/.</param>
	[McpTool( "unreal_scene_import_test" )]
	public static async Task<string> SceneImportTest( string stagingDir, string outputFolder )
	{
		var manifestPath = Path.Combine( stagingDir, "manifest.json" );
		if ( !File.Exists( manifestPath ) )
			return $"no manifest.json in {stagingDir}";

		var manifest = ImportManifest.Load( manifestPath );
		var summary = await AssetImporter.Import( manifest, stagingDir, outputFolder, CancellationToken.None );

		var result = $"models={summary.Models} materials={summary.Materials} textures={summary.Textures} " +
			$"placements={summary.Placements} prefab={summary.PrefabPath ?? "(none)"}";
		if ( summary.Warnings.Count > 0 )
			result += "\nwarnings:\n - " + string.Join( "\n - ", summary.Warnings.Take( 10 ) );

		return result;
	}

	/// <summary>Load a model and report its bounds in inches.</summary>
	/// <param name="modelPath">Model content path, e.g. "unrealimport/models/x.vmdl".</param>
	[McpTool( "unreal_model_bounds" )]
	public static string ModelBounds( string modelPath )
	{
		var model = Model.Load( modelPath );
		if ( model is null || model.IsError )
			return $"failed to load {modelPath}";

		var b = model.Bounds;
		return $"size=({b.Size.x:0.##}, {b.Size.y:0.##}, {b.Size.z:0.##}) in  mins=({b.Mins.x:0.##},{b.Mins.y:0.##},{b.Mins.z:0.##}) maxs=({b.Maxs.x:0.##},{b.Maxs.y:0.##},{b.Maxs.z:0.##})";
	}

	/// <summary>Bounds of every .vmdl in a folder, one json object per line.</summary>
	/// <param name="folder">Absolute folder containing .vmdl files.</param>
	[McpTool( "unreal_all_model_bounds" )]
	public static string AllModelBounds( string folder )
	{
		var sb = new System.Text.StringBuilder();
		foreach ( var f in Directory.EnumerateFiles( folder, "*.vmdl" ) )
		{
			var rel = Path.GetRelativePath( Sandbox.Project.Current.GetAssetsPath(), f ).Replace( '\\', '/' );
			var model = Model.Load( rel );
			if ( model is null || model.IsError )
			{
				sb.AppendLine( $"{{\"model\":\"{Path.GetFileName( f )}\",\"error\":true}}" );
				continue;
			}

			var b = model.Bounds;
			sb.AppendLine( System.FormattableString.Invariant(
				$"{{\"model\":\"{Path.GetFileName( f )}\",\"min\":[{b.Mins.x:0.###},{b.Mins.y:0.###},{b.Mins.z:0.###}],\"max\":[{b.Maxs.x:0.###},{b.Maxs.y:0.###},{b.Maxs.z:0.###}]}}" ) );
		}
		return sb.ToString();
	}
}
