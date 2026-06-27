using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Editor.UnrealImporter;

public class ExportResult
{
	public bool Success;
	public string StagingDir;
	public string ManifestPath;
	public string Error;
}

/// <summary>
/// Drives Tools/ue_export.py inside headless Unreal (UnrealEditor-Cmd) to turn selected
/// .uasset StaticMeshes into FBX + PNG + manifest.json in a staging folder.
/// </summary>
public static class HeadlessExporter
{
	/// <summary>Find ue_export.py shipped in this library's Tools folder.</summary>
	public static string FindExportScript()
	{
		var root = Sandbox.Project.Current?.GetRootPath();
		if ( !string.IsNullOrEmpty( root ) )
		{
			var direct = Path.Combine( root, "Libraries", "unrealimporter", "Tools", "ue_export.py" );
			if ( File.Exists( direct ) )
				return direct;

			var hit = Directory.EnumerateFiles( root, "ue_export.py", SearchOption.AllDirectories ).FirstOrDefault();
			if ( hit != null )
				return hit;
		}

		return null;
	}

	/// <summary>Convert a Content-relative .uasset file path to a /Game object path.</summary>
	/// <example>.../Content/Construction_VOL1/Meshes/SM_Boxes_01a.uasset -> /Game/Construction_VOL1/Meshes/SM_Boxes_01a</example>
	public static string ToGamePath( string uprojectFolder, string uassetAbsPath )
	{
		var content = Path.Combine( uprojectFolder, "Content" );
		var rel = Path.GetRelativePath( content, uassetAbsPath ).Replace( '\\', '/' );
		if ( rel.EndsWith( ".uasset", StringComparison.OrdinalIgnoreCase ) )
			rel = rel[..^".uasset".Length];

		return "/Game/" + rel;
	}

	public static ExportResult Run( string editorCmd, string uprojectPath, IEnumerable<string> gameAssetPaths, string scriptPath )
	{
		var result = new ExportResult();

		var stagingDir = Path.Combine( Path.GetTempPath(), "unrealimporter", Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( stagingDir );
		result.StagingDir = stagingDir;

		// Pass the selection via a file (env-var/command-line length is limited).
		var assetsFile = Path.Combine( stagingDir, "_assets.txt" );
		File.WriteAllLines( assetsFile, gameAssetPaths );

		var logPath = Path.Combine( stagingDir, "ue_export.log" );

		// NOTE: -script must use forward slashes; a backslash before u/r/etc. is eaten as a python escape.
		var script = scriptPath.Replace( '\\', '/' );
		var args =
			$"\"{uprojectPath}\" -run=pythonscript -script=\"{script}\" " +
			$"-EnablePlugins=PythonScriptPlugin -unattended -nosplash -nullrhi -abslog=\"{logPath}\"";

		var psi = new ProcessStartInfo
		{
			FileName = editorCmd,
			Arguments = args,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.EnvironmentVariables["UE_EXPORT_OUT"] = stagingDir;
		psi.EnvironmentVariables["UE_EXPORT_ASSETS_FILE"] = assetsFile;

		try
		{
			using var proc = Process.Start( psi );
			proc.WaitForExit();

			result.ManifestPath = Path.Combine( stagingDir, "manifest.json" );
			if ( proc.ExitCode != 0 )
			{
				result.Error = $"UnrealEditor-Cmd exited with code {proc.ExitCode}. See log:\n{logPath}";
				return result;
			}

			if ( !File.Exists( result.ManifestPath ) )
			{
				result.Error = $"Export finished but no manifest.json was produced. See log:\n{logPath}";
				return result;
			}

			result.Success = true;
			return result;
		}
		catch ( Exception e )
		{
			result.Error = e.Message;
			return result;
		}
	}
}
