using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace Editor.UnrealImporter;

public class ImportSummary
{
	public int Models;
	public int Materials;
	public int Textures;
	public string OutputDir;
	public List<string> Warnings = new();

	/// <summary>Scene mode: placements in the generated prefab, and where it was written.</summary>
	public int Placements;
	public string PrefabPath;
}

/// <summary>
/// Consumes a staging folder (FBX + PNG + manifest.json from the headless export) and writes
/// sbox assets (.fbx + .vmat + .vmdl) into the project, ready for the engine to compile.
/// </summary>
public static class AssetImporter
{
	/// <param name="manifest"></param>
	/// <param name="stagingDir"></param>
	/// <param name="outputRoot"></param>
	/// <param name="flat">When true, everything goes directly in outputRoot instead of models/materials/textures subfolders.</param>
	/// <param name="progressToken"></param>
	/// <param name="onProgress">(done, total, current asset name) per imported model.</param>
	public static async Task<ImportSummary> Import( ImportManifest manifest, string stagingDir, string outputRoot, CancellationToken progressToken, bool flat = false, Action<int, int, string> onProgress = null )
	{
		var summary = new ImportSummary { OutputDir = outputRoot };

		var assetsDir = FindAssetsDir( outputRoot ) ?? Sandbox.Project.Current?.GetAssetsPath();
		if ( string.IsNullOrEmpty( assetsDir ) )
			throw new Exception( "Could not resolve the project's Assets folder. Pick an output folder inside Assets/." );

		var modelsDir = flat ? outputRoot : Path.Combine( outputRoot, "models" );
		var materialsDir = flat ? outputRoot : Path.Combine( outputRoot, "materials" );
		var texturesDir = flat ? outputRoot : Path.Combine( outputRoot, "textures" );
		Directory.CreateDirectory( modelsDir );
		Directory.CreateDirectory( materialsDir );
		Directory.CreateDirectory( texturesDir );

		// Track shared materials/textures so we only process them once.
		var writtenVmats = new Dictionary<string, string>();   // base -> vmat content path
		var modelsByGamePath = new Dictionary<string, string>();   // /Game path -> vmdl content path
		var mirroredByGamePath = new Dictionary<string, string>(); // /Game path -> mirrored vmdl content path

		// Scene placements with an odd number of negative scale axes are true mirrors -
		// s&box doesn't flip winding for negative GameObject scale, so those need a
		// mirrored model variant (negative vmdl import_scale bakes the mirror + winding).
		var needsMirror = new HashSet<string>();
		foreach ( var p in manifest.Scene?.Placements ?? new() )
		{
			if ( p.Mesh is not null && p.Scale is { Length: >= 3 } && p.Scale.Count( v => v < 0 ) % 2 == 1 )
				needsMirror.Add( p.Mesh );
		}

		foreach ( var asset in manifest.Assets )
		{
			progressToken.ThrowIfCancellationRequested();
			onProgress?.Invoke( manifest.Assets.IndexOf( asset ) + 1, manifest.Assets.Count, asset.Asset );

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

				// s&box reads the FBX material *node* name, which Unreal writes as the assigned
				// material (the MI, e.g. "MI_OilBarrel_01a") - NOT the DCC slot label ("lambert2",
				// which ends up unused). So the remap must key off the material name.
				var remapKey = !string.IsNullOrEmpty( mat.Material ) ? mat.Material : mat.Slot;

				if ( !writtenVmats.TryGetValue( baseName, out var vmatContent ) )
				{
					var emissive = EmissiveParams( mat );
					var alphaRole = AlphaRoleFor( mat, emissive is not null );

					var tex = TextureProcessor.Process( mat, stagingDir, texturesDir, baseName, alphaRole );
					summary.Textures += CountTextures( tex );

					// Self-illum source: dedicated emissive texture wins, else the albedo-alpha mask.
					var selfIllumMask = tex.Emissive ?? tex.SelfIllumMask;

					var vmatText = Kv3Writer.VmatText(
						color: TexContent( assetsDir, texturesDir, tex.Color ),
						normal: TexContent( assetsDir, texturesDir, tex.Normal ),
						roughness: TexContent( assetsDir, texturesDir, tex.Roughness ),
						metallic: TexContent( assetsDir, texturesDir, tex.Metallic ),
						ao: TexContent( assetsDir, texturesDir, tex.Ao ),
						alpha: TexContent( assetsDir, texturesDir, tex.Alpha ),
						// Tint stays INERT by default (white) so the albedo's own colours show through.
						// The mask + captured tint colours are emitted for optional manual recolouring.
						tintMask: TexContent( assetsDir, texturesDir, tex.TintMask ),
						tintColor: null,
						tintAmount: null,
						tintComment: TintComment( mat ),
						alphaTest: mat.BlendMode?.Contains( "MASKED" ) == true,
						selfIllumMask: TexContent( assetsDir, texturesDir, selfIllumMask ),
						selfIllumTint: emissive?.tint,
						selfIllumBrightness: emissive?.magnitude ?? 1f,
						selfIllumFromAlbedoAlpha: tex.Emissive is null && tex.SelfIllumMask is not null );

					var vmatPath = Path.Combine( materialsDir, baseName + ".vmat" );
					await File.WriteAllTextAsync( vmatPath, vmatText, progressToken );
					summary.Materials++;

					vmatContent = ToContentPath( assetsDir, vmatPath );
					writtenVmats[baseName] = vmatContent;
				}

				remaps.Add( (remapKey, vmatContent) );
			}

			// Write the model, then verify it compiles. Hull-from-render chokes on some
			// geometry (dense foliage cards -> "Inconsistent hull geometry"), so fall back
			// to a single hull, then to no collision, until the model compiles.
			var fbxContent = ToContentPath( assetsDir, fbxDst );
			var vmdlPath = Path.Combine( modelsDir, modelName + ".vmdl" );
			var scale = asset.ImportScale <= 0 ? 0.3937f : asset.ImportScale;
			string usedHullMode = null;

			foreach ( var hullMode in new[] { "HullPerElement", "SingleHull", null } )
			{
				await File.WriteAllTextAsync( vmdlPath, Kv3Writer.VmdlText( fbxContent, scale, remaps, hullMode ), progressToken );

				var vmdlAsset = global::Editor.AssetSystem.RegisterFile( vmdlPath );
				if ( vmdlAsset is null )
					break;   // can't verify here - leave the default and let the engine compile later

				if ( vmdlAsset.Compile( full: false ) && !vmdlAsset.IsCompileFailed )
				{
					usedHullMode = hullMode;
					break;
				}

				if ( hullMode is null )
					summary.Warnings.Add( $"{asset.Asset}: model failed to compile even without collision - see console." );
				else
					summary.Warnings.Add( $"{asset.Asset}: collision '{hullMode}' failed to compile, falling back to {(hullMode == "HullPerElement" ? "SingleHull" : "no collision")}." );
			}

			summary.Models++;

			if ( !string.IsNullOrEmpty( asset.GamePath ) )
				modelsByGamePath[asset.GamePath] = ToContentPath( assetsDir, vmdlPath );

			// Mirrored variant for placements that flip this mesh. Uses the ScaleAndMirror
			// model modifier (flip across local X, winding corrected) - NOT a negative
			// import_scale, which mirrors the verts but leaves faces wound inside-out.
			// The prefab builder composes a 180° rotation to turn the X-flip into whatever
			// mirror the placement actually wants.
			if ( asset.GamePath is not null && needsMirror.Contains( asset.GamePath ) )
			{
				var mirrorPath = Path.Combine( modelsDir, modelName + "_mirror.vmdl" );
				await File.WriteAllTextAsync( mirrorPath, Kv3Writer.VmdlText( fbxContent, scale, remaps, usedHullMode ?? "HullPerElement", mirror: true ), progressToken );

				var mirrorAsset = global::Editor.AssetSystem.RegisterFile( mirrorPath );
				if ( mirrorAsset is not null && (!mirrorAsset.Compile( full: false ) || mirrorAsset.IsCompileFailed) )
					summary.Warnings.Add( $"{asset.Asset}: mirrored variant failed to compile - mirrored placements will use the unmirrored model." );
				else
					mirroredByGamePath[asset.GamePath] = ToContentPath( assetsDir, mirrorPath );
			}

			Log.Info( $"[{manifest.Assets.IndexOf( asset ) + 1}/{manifest.Assets.Count}] Imported {asset.Asset} -> {vmdlPath}" +
				(usedHullMode != "HullPerElement" ? $" (collision: {usedHullMode ?? "none"})" : "") );
		}

		// Scene mode: turn the level's placements + lights into a prefab next to the models.
		if ( manifest.Scene is not null )
		{
			if ( manifest.Scene.Warnings is { Count: > 0 } )
				summary.Warnings.AddRange( manifest.Scene.Warnings );

			summary.PrefabPath = ScenePrefabBuilder.Build( manifest.Scene, modelsByGamePath, outputRoot, summary.Warnings, mirroredByGamePath );
			summary.Placements = manifest.Scene.Placements?.Count ?? 0;
		}

		return summary;
	}

	/// <summary>
	/// What the albedo's alpha channel means, from the Unreal blend mode. Opaque materials'
	/// alpha is NOT opacity - with emissive params present it's a self-illum mask (lamp
	/// housings etc.), otherwise it packs something we can't interpret and is ignored.
	/// Old manifests without blend_mode keep the legacy translucency behaviour.
	/// </summary>
	static AlphaRole AlphaRoleFor( ManifestMaterial mat, bool hasEmissiveParams )
	{
		var blend = mat.BlendMode ?? "";
		if ( blend.Length == 0 || blend.Contains( "TRANSLUCENT" ) || blend.Contains( "MASKED" )
			|| blend.Contains( "ADDITIVE" ) || blend.Contains( "MODULATE" ) )
			return AlphaRole.Translucency;

		return hasEmissiveParams ? AlphaRole.SelfIllum : AlphaRole.Ignore;
	}

	/// <summary>
	/// Emissive tint (Unreal LINEAR) + linear brightness multiplier from the Material
	/// Instance's parameter overrides ("Emissive Multiply", "Emissive Color Multi", ...).
	/// Null when the material has no emissive-ish parameter.
	/// </summary>
	static (float[] tint, float magnitude)? EmissiveParams( ManifestMaterial mat )
	{
		if ( mat.VectorParams is not null )
		{
			foreach ( var kv in mat.VectorParams )
			{
				if ( !kv.Key.Contains( "emissiv", StringComparison.OrdinalIgnoreCase ) || kv.Value is not { Length: >= 3 } )
					continue;

				float mag = Math.Max( kv.Value[0], Math.Max( kv.Value[1], kv.Value[2] ) );
				if ( mag > 0 )
					return (kv.Value, mag);
			}
		}

		if ( mat.ScalarParams is not null )
		{
			foreach ( var kv in mat.ScalarParams )
			{
				if ( kv.Key.Contains( "emissiv", StringComparison.OrdinalIgnoreCase ) && kv.Value > 0 )
					return (null, kv.Value);
			}
		}

		return null;
	}

	/// <summary>Human-readable note of the tint colours Unreal had, so they can be wired up by hand.</summary>
	static string TintComment( ManifestMaterial mat )
	{
		var parts = new List<string>();
		if ( mat.TintColor is not null )
			parts.Add( $"tint=[{FmtColor( mat.TintColor )}]" );
		if ( mat.TintZones is not null )
			foreach ( var kv in mat.TintZones )
				parts.Add( $"{kv.Key}=[{FmtColor( kv.Value )}]" );

		return parts.Count == 0 ? null : "Captured Unreal tint (NOT auto-applied; set g_vColorTint to use): " + string.Join( ", ", parts );
	}

	static string FmtColor( float[] c )
	{
		if ( c is null )
			return "";

		var sb = new StringBuilder();
		for ( int i = 0; i < c.Length; i++ )
		{
			if ( i > 0 ) sb.Append( ' ' );
			sb.Append( c[i].ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture ) );
		}
		return sb.ToString();
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
		if ( t.TintMask != null ) n++;
		if ( t.SelfIllumMask != null ) n++;
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
