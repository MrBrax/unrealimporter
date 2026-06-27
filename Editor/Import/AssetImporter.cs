using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sandbox;

namespace Editor.UnrealImporter;

public class ImportSummary
{
	public int Models;
	public int Materials;
	public int Textures;
	public string OutputDir;
	public List<string> Warnings = new();
}

/// <summary>
/// Consumes a staging folder (FBX + PNG + manifest.json from the headless export) and writes
/// sbox assets (.fbx + .vmat + .vmdl) into the project, ready for the engine to compile.
/// </summary>
public static class AssetImporter
{
	public static ImportSummary Import( ImportManifest manifest, string stagingDir, string outputRoot )
	{
		var summary = new ImportSummary { OutputDir = outputRoot };

		var assetsDir = FindAssetsDir( outputRoot ) ?? Sandbox.Project.Current?.GetAssetsPath();
		if ( string.IsNullOrEmpty( assetsDir ) )
			throw new Exception( "Could not resolve the project's Assets folder. Pick an output folder inside Assets/." );

		var modelsDir = Path.Combine( outputRoot, "models" );
		var materialsDir = Path.Combine( outputRoot, "materials" );
		var texturesDir = Path.Combine( outputRoot, "textures" );
		Directory.CreateDirectory( modelsDir );
		Directory.CreateDirectory( materialsDir );
		Directory.CreateDirectory( texturesDir );

		// Track shared materials/textures so we only process them once.
		var writtenVmats = new Dictionary<string, string>();   // base -> vmat content path

		foreach ( var asset in manifest.Assets )
		{
			if ( string.IsNullOrEmpty( asset.Fbx ) )
			{
				summary.Warnings.Add( $"{asset.Asset}: no fbx in manifest, skipped." );
				continue;
			}

			// Copy the mesh.
			var fbxSrc = Path.Combine( stagingDir, asset.Fbx.Replace( '/', Path.DirectorySeparatorChar ) );
			if ( !File.Exists( fbxSrc ) )
			{
				summary.Warnings.Add( $"{asset.Asset}: fbx missing at {fbxSrc}, skipped." );
				continue;
			}

			var modelName = Sanitize( asset.Asset );
			var fbxDst = Path.Combine( modelsDir, modelName + ".fbx" );
			File.Copy( fbxSrc, fbxDst, overwrite: true );

			// Build per-slot remaps, writing vmats + textures as needed.
			var remaps = new List<(string slot, string vmat)>();

			foreach ( var mat in asset.Materials )
			{
				var baseName = MaterialBaseName( mat );
				var slot = string.IsNullOrEmpty( mat.Slot ) ? baseName : mat.Slot;

				if ( !writtenVmats.TryGetValue( baseName, out var vmatContent ) )
				{
					var tex = TextureProcessor.Process( mat, stagingDir, texturesDir, baseName );
					summary.Textures += CountTextures( tex );

					var vmatText = Kv3Writer.VmatText(
						color: TexContent( assetsDir, texturesDir, tex.Color ),
						normal: TexContent( assetsDir, texturesDir, tex.Normal ),
						roughness: TexContent( assetsDir, texturesDir, tex.Roughness ),
						metallic: TexContent( assetsDir, texturesDir, tex.Metallic ),
						ao: TexContent( assetsDir, texturesDir, tex.Ao ),
						alpha: TexContent( assetsDir, texturesDir, tex.Alpha ) );

					var vmatPath = Path.Combine( materialsDir, baseName + ".vmat" );
					File.WriteAllText( vmatPath, vmatText );
					summary.Materials++;

					vmatContent = ToContentPath( assetsDir, vmatPath );
					writtenVmats[baseName] = vmatContent;
				}

				remaps.Add( (slot, vmatContent) );
			}

			// Write the model.
			var fbxContent = ToContentPath( assetsDir, fbxDst );
			var vmdlText = Kv3Writer.VmdlText( fbxContent, asset.ImportScale <= 0 ? 0.3937f : asset.ImportScale, remaps );
			File.WriteAllText( Path.Combine( modelsDir, modelName + ".vmdl" ), vmdlText );
			summary.Models++;
		}

		return summary;
	}

	static int CountTextures( ProcessedTextures t )
	{
		int n = 0;
		if ( t.Color != null ) n++;
		if ( t.Alpha != null ) n++;
		if ( t.Normal != null ) n++;
		if ( t.Roughness != null ) n++;
		if ( t.Metallic != null ) n++;
		if ( t.Ao != null ) n++;
		if ( t.Emissive != null ) n++;
		return n;
	}

	static string TexContent( string assetsDir, string texturesDir, string fileName )
	{
		if ( string.IsNullOrEmpty( fileName ) )
			return null;

		return ToContentPath( assetsDir, Path.Combine( texturesDir, fileName ) );
	}

	/// <summary>Path relative to the Assets folder, forward slashes, lowercase.</summary>
	static string ToContentPath( string assetsDir, string absPath )
		=> Path.GetRelativePath( assetsDir, absPath ).Replace( '\\', '/' ).ToLowerInvariant();

	static string FindAssetsDir( string path )
	{
		var d = new DirectoryInfo( path );
		while ( d != null )
		{
			if ( string.Equals( d.Name, "Assets", StringComparison.OrdinalIgnoreCase ) )
				return d.FullName;

			d = d.Parent;
		}

		return null;
	}

	/// <summary>Base name (lowercase, dot-free) for a material's textures + vmat, from the MI name when available.</summary>
	static string MaterialBaseName( ManifestMaterial mat )
	{
		if ( !string.IsNullOrEmpty( mat.Material ) )
			return Sanitize( mat.Material );

		// Fall back to a texture filename minus its role suffix.
		var any = mat.Alb ?? mat.Nrm ?? mat.Rma ?? mat.Rough ?? mat.Metal ?? mat.Ao;
		if ( !string.IsNullOrEmpty( any ) )
		{
			var name = Path.GetFileNameWithoutExtension( any );
			foreach ( var suffix in new[] { "_ALB", "_ALBEDO", "_BASECOLOR", "_COLOR", "_NRM", "_NORMAL", "_RMA", "_ORM" } )
			{
				if ( name.EndsWith( suffix, StringComparison.OrdinalIgnoreCase ) )
				{
					name = name[..^suffix.Length];
					break;
				}
			}
			return Sanitize( name );
		}

		return Sanitize( mat.Slot ?? "material" );
	}

	/// <summary>Lowercase; non [a-z0-9_] -> '_'. Guarantees no dots in generated filenames.</summary>
	static string Sanitize( string s )
	{
		if ( string.IsNullOrEmpty( s ) )
			return "unnamed";

		var sb = new StringBuilder( s.Length );
		foreach ( var ch in s.ToLowerInvariant() )
			sb.Append( (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' ? ch : '_' );

		return sb.ToString();
	}
}
