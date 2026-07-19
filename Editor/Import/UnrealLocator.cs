using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Editor.UnrealImporter;

/// <summary>
/// Locates a .uproject's engine and the UnrealEditor-Cmd.exe used to run the headless export.
/// </summary>
public static class UnrealLocator
{
	public static string FindUprojectInFolder( string folder )
	{
		if ( string.IsNullOrEmpty( folder ) || !Directory.Exists( folder ) )
			return null;

		return Directory.GetFiles( folder, "*.uproject", SearchOption.TopDirectoryOnly ).FirstOrDefault();
	}

	/// <summary>Reads "EngineAssociation" (e.g. "5.5") from a .uproject. May be null.</summary>
	public static string ReadEngineAssociation( string uprojectPath )
	{
		try
		{
			using var doc = JsonDocument.Parse( File.ReadAllText( uprojectPath ) );
			if ( doc.RootElement.TryGetProperty( "EngineAssociation", out var e ) )
				return e.GetString();
		}
		catch { }

		return null;
	}

	/// <summary>
	/// Find UnrealEditor-Cmd.exe, preferring the version the project targets.
	/// Tries the registry first, then scans the standard Epic Games install root.
	/// When the exact version isn't installed, prefers the CLOSEST NEWER engine
	/// (a newer engine opens older assets; an older one can't read newer assets),
	/// falling back to the highest older install.
	/// </summary>
	public static string FindEditorCmd( string engineVersion )
	{
		var fromReg = FromRegistry( engineVersion );
		if ( fromReg != null )
			return fromReg;

		var roots = new[]
		{
			Environment.GetEnvironmentVariable( "ProgramW6432" ),
			Environment.GetEnvironmentVariable( "ProgramFiles" ),
		}.Where( x => !string.IsNullOrEmpty( x ) ).Distinct();

		Version.TryParse( engineVersion ?? "", out var wanted );

		foreach ( var pf in roots )
		{
			var epic = Path.Combine( pf, "Epic Games" );
			if ( !Directory.Exists( epic ) )
				continue;

			if ( !string.IsNullOrEmpty( engineVersion ) )
			{
				var exact = CmdPath( Path.Combine( epic, $"UE_{engineVersion}" ) );
				if ( File.Exists( exact ) )
					return exact;
			}

			var installed = Directory.GetDirectories( epic, "UE_*" )
				.Where( d => File.Exists( CmdPath( d ) ) )
				.Select( d => (dir: d, ver: Version.TryParse( Path.GetFileName( d )["UE_".Length..], out var v ) ? v : null) )
				.Where( x => x.ver is not null )
				.ToList();

			if ( installed.Count == 0 )
				continue;

			var pick = wanted is not null
				? installed.Where( x => x.ver >= wanted ).OrderBy( x => x.ver ).FirstOrDefault().dir
					?? installed.OrderByDescending( x => x.ver ).First().dir
				: installed.OrderByDescending( x => x.ver ).First().dir;

			return CmdPath( pick );
		}

		return null;
	}

	static string CmdPath( string engineRoot )
		=> Path.Combine( engineRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe" );

	static string FromRegistry( string engineVersion )
	{
		if ( string.IsNullOrEmpty( engineVersion ) )
			return null;

		try
		{
			using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
				$@"SOFTWARE\EpicGames\Unreal Engine\{engineVersion}" );

			if ( key?.GetValue( "InstalledDirectory" ) is string dir && !string.IsNullOrEmpty( dir ) )
			{
				var cmd = CmdPath( dir );
				if ( File.Exists( cmd ) )
					return cmd;
			}
		}
		catch { }

		return null;
	}
}
