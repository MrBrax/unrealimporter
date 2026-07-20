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

/// <summary>How generated assets are laid out on disk.</summary>
public enum ImportLayout
{
	/// <summary>&lt;output&gt;/models, /materials, /textures.</summary>
	Grouped,

	/// <summary>Everything directly in &lt;output&gt;.</summary>
	Flat,

	/// <summary>
	/// One self-contained folder per imported asset: &lt;output&gt;/&lt;asset&gt;/ holds its
	/// model, materials and textures together. Shared materials are duplicated into each
	/// asset's folder - that's the point, each folder can be moved or deleted on its own.
	/// </summary>
	PerAsset,

	/// <summary>
	/// Classic Source style: Assets/models/&lt;sub&gt; for fbx+vmdl, Assets/materials/&lt;sub&gt; for
	/// vmat+textures, Assets/prefabs/&lt;sub&gt; for map prefabs. Ignores the picked output folder.
	/// </summary>
	ClassicSource,
}

/// <summary>
/// What a material picked on its own turns into. Materials on a MESH are always .vmat -
/// a model's material slots can't reference a terrain or decal resource.
/// </summary>
public enum MaterialOutput
{
	/// <summary>A complex.shader .vmat (the default).</summary>
	Material,

	/// <summary>A .tmat Terrain Material - for tiling ground surfaces.</summary>
	Terrain,

	/// <summary>A .decal Decal Definition - projected decals.</summary>
	Decal,
}

/// <summary>Where each kind of generated file goes.</summary>
public class ImportPaths
{
	public string ModelsDir;
	public string MaterialsDir;
	public string TexturesDir;
	public string PrefabDir;

	/// <summary>What to show the user as "where it went".</summary>
	public string Display;
}

/// <summary>
/// Consumes a staging folder (FBX + PNG + manifest.json from the headless export) and writes
/// sbox assets (.fbx + .vmat + .vmdl) into the project, ready for the engine to compile.
/// </summary>
public static class AssetImporter
{
	/// <summary>
	/// Resolve the destination folders for a layout. Classic Source hangs off the Assets root
	/// (type first, then subfolder) rather than off the picked output folder.
	/// </summary>
	public static ImportPaths ResolvePaths( string outputRoot, string assetsDir, ImportLayout layout, string subfolder )
	{
		switch ( layout )
		{
			case ImportLayout.Flat:
				return new ImportPaths
				{
					ModelsDir = outputRoot,
					MaterialsDir = outputRoot,
					TexturesDir = outputRoot,
					PrefabDir = outputRoot,
					Display = outputRoot,
				};

			case ImportLayout.PerAsset:
				// These are the ROOT - Import() appends the per-asset folder as it goes.
				return new ImportPaths
				{
					ModelsDir = outputRoot,
					MaterialsDir = outputRoot,
					TexturesDir = outputRoot,
					PrefabDir = outputRoot,
					Display = Path.Combine( outputRoot, "<asset>" ),
				};

			case ImportLayout.ClassicSource:
			{
				// Empty subfolder is legal - assets land straight in Assets/models, Assets/materials.
				var sub = SanitizeSubfolder( subfolder );
				string Under( string type ) => string.IsNullOrEmpty( sub )
					? Path.Combine( assetsDir, type )
					: Path.Combine( assetsDir, type, sub );

				var models = Under( "models" );
				var materials = Under( "materials" );

				return new ImportPaths
				{
					ModelsDir = models,
					// Textures live beside the vmats that reference them.
					MaterialsDir = materials,
					TexturesDir = materials,
					PrefabDir = Under( "prefabs" ),
					Display = $"{models}\n{materials}",
				};
			}

			default:
				return new ImportPaths
				{
					ModelsDir = Path.Combine( outputRoot, "models" ),
					MaterialsDir = Path.Combine( outputRoot, "materials" ),
					TexturesDir = Path.Combine( outputRoot, "textures" ),
					PrefabDir = outputRoot,
					Display = outputRoot,
				};
		}
	}

	/// <summary>Trim a user-typed subfolder to a safe relative path ("Props/Barrels" stays nested).</summary>
	static string SanitizeSubfolder( string subfolder )
	{
		if ( string.IsNullOrWhiteSpace( subfolder ) )
			return "";

		var parts = subfolder.Split( new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries )
			.Select( p => p.Trim() )
			.Where( p => p.Length > 0 && p != "." && p != ".." )
			.Select( Sanitize );

		return string.Join( Path.DirectorySeparatorChar, parts );
	}

	/// <param name="manifest"></param>
	/// <param name="stagingDir"></param>
	/// <param name="outputRoot"></param>
	/// <param name="progressToken"></param>
	/// <param name="layout">How the generated files are foldered - see <see cref="ImportLayout"/>.</param>
	/// <param name="subfolder">Subfolder under Assets/models + Assets/materials, ClassicSource layout only.</param>
	/// <param name="onProgress">(done, total, current asset name) per imported model.</param>
	/// <param name="generateLods">When false, models get no auto-LOD chain (full detail always).</param>
	/// <param name="lightScale">Extra multiplier on converted scene-light brightness (1 = calibrated default).</param>
	/// <param name="materialOutput">What standalone materials become - vmat, tmat or decal. Mesh slots are always vmat.</param>
	/// <param name="perAssetFolderDepth">
	/// PerAsset layout only: how many folders up the /Game path to name each asset's folder after.
	/// 0 = the asset's own name (e.g. mi_sjfnbeaa). Fab/Megascans bury the real name a couple of
	/// folders up (.../Fine_American_Road_sjfnbeaa/Medium/MI_sjfnbeaa), so 2 gives a readable folder.
	/// </param>
	public static async Task<ImportSummary> Import( ImportManifest manifest, string stagingDir, string outputRoot, CancellationToken progressToken, ImportLayout layout = ImportLayout.Grouped, string subfolder = null, Action<int, int, string> onProgress = null, bool generateLods = true, float lightScale = 1f, MaterialOutput materialOutput = MaterialOutput.Material, int perAssetFolderDepth = 0 )
	{
		var assetsDir = FindAssetsDir( outputRoot ) ?? Sandbox.Project.Current?.GetAssetsPath();
		if ( string.IsNullOrEmpty( assetsDir ) )
			throw new Exception( "Could not resolve the project's Assets folder. Pick an output folder inside Assets/." );

		var paths = ResolvePaths( outputRoot, assetsDir, layout, subfolder );
		var summary = new ImportSummary { OutputDir = paths.Display };

		Directory.CreateDirectory( paths.ModelsDir );
		Directory.CreateDirectory( paths.MaterialsDir );
		Directory.CreateDirectory( paths.TexturesDir );

		// PerAsset puts every asset in its own self-contained folder; every other layout
		// shares one set of directories for the whole import. The folder is named after a
		// parent of the /Game path (perAssetFolderDepth up) when the asset's own name is
		// unhelpful - Fab MIs are named like "mi_sjfnbeaa".
		(string models, string materials, string textures) DirsFor( string ownName, string gamePath )
		{
			if ( layout != ImportLayout.PerAsset )
				return (paths.ModelsDir, paths.MaterialsDir, paths.TexturesDir);

			var dir = Path.Combine( paths.ModelsDir, PerAssetFolder( gamePath, ownName, perAssetFolderDepth ) );
			Directory.CreateDirectory( dir );
			return (dir, dir, dir);
		}

		// Track materials we've already written so shared ones are processed once. Keyed by
		// folder too: under PerAsset the same material is deliberately written into each
		// asset's folder, so the name alone would wrongly dedupe it away.
		var writtenVmats = new Dictionary<string, string>();   // "<dir>|<base>" -> vmat content path
		var modelsByGamePath = new Dictionary<string, string>();   // /Game path -> vmdl content path
		var mirroredByGamePath = new Dictionary<string, string>(); // /Game path -> mirrored vmdl content path

		// Scene placements with an odd number of negative scale axes are true mirrors -
		// s&box doesn't flip winding for negative GameObject scale, so those need a
		// mirrored model variant (negative vmdl import_scale bakes the mirror + winding).
		// Progress spans meshes then standalone materials as one run.
		var totalAssets = manifest.Assets.Count + (manifest.Materials?.Count ?? 0);

		var needsMirror = new HashSet<string>();
		foreach ( var p in manifest.Scene?.Placements ?? new() )
		{
			if ( p.Mesh is not null && p.Scale is { Length: >= 3 } && p.Scale.Count( v => v < 0 ) % 2 == 1 )
				needsMirror.Add( p.Mesh );
		}

		foreach ( var asset in manifest.Assets )
		{
			progressToken.ThrowIfCancellationRequested();
			onProgress?.Invoke( manifest.Assets.IndexOf( asset ) + 1, totalAssets, asset.Asset );

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
			var (modelsDir, materialsDir, texturesDir) = DirsFor( modelName, asset.GamePath );
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

				var vmatContent = await WriteVmat( mat, baseName, stagingDir, assetsDir, materialsDir, texturesDir, writtenVmats, summary, progressToken );
				remaps.Add( (remapKey, vmatContent) );
			}

			// UE's FBX exporter names material nodes after the assigned material - when two
			// slots share one material, the FBX SDK uniquifies the duplicates with numeric
			// suffixes (MI_Escalator_01a + MI_Escalator_01a_3). Those suffixed nodes need
			// remaps too, or the engine hunts for a literal "mi_escalator_01a_3.vmat".
			remaps.AddRange( SuffixedRemaps( fbxDst, remaps ) );

			// Write the model, then verify it compiles. Hull-from-render chokes on some
			// geometry (dense foliage cards -> "Inconsistent hull geometry"), so fall back
			// to a single hull, then to no collision, until the model compiles.
			var fbxContent = ToContentPath( assetsDir, fbxDst );
			var vmdlPath = Path.Combine( modelsDir, modelName + ".vmdl" );
			var scale = asset.ImportScale <= 0 ? 0.3937f : asset.ImportScale;
			string usedHullMode = null;

			foreach ( var hullMode in new[] { "HullPerElement", "SingleHull", null } )
			{
				await File.WriteAllTextAsync( vmdlPath, Kv3Writer.VmdlText( fbxContent, scale, remaps, hullMode, lods: generateLods ), progressToken );

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
				await File.WriteAllTextAsync( mirrorPath, Kv3Writer.VmdlText( fbxContent, scale, remaps, usedHullMode ?? "HullPerElement", mirror: true, lods: generateLods ), progressToken );

				var mirrorAsset = global::Editor.AssetSystem.RegisterFile( mirrorPath );
				if ( mirrorAsset is not null && (!mirrorAsset.Compile( full: false ) || mirrorAsset.IsCompileFailed) )
					summary.Warnings.Add( $"{asset.Asset}: mirrored variant failed to compile - mirrored placements will use the unmirrored model." );
				else
					mirroredByGamePath[asset.GamePath] = ToContentPath( assetsDir, mirrorPath );
			}

			Log.Info( $"[{manifest.Assets.IndexOf( asset ) + 1}/{manifest.Assets.Count}] Imported {asset.Asset} -> {vmdlPath}" +
				(usedHullMode != "HullPerElement" ? $" (collision: {usedHullMode ?? "none"})" : "") );
		}

		// A model's material slots have to be vmats, so a terrain/decal choice only applies to
		// the standalone materials - say so rather than leaving the user to wonder.
		if ( materialOutput != MaterialOutput.Material && manifest.Assets.Count > 0 )
			summary.Warnings.Add( $"Material output '{materialOutput}' applies to materials imported on their own; the {manifest.Assets.Count} mesh(es) still got .vmat materials." );

		// Materials picked on their own: no mesh, just a vmat + its textures. Surface packs
		// (Megascans Surfaces) consist of nothing else.
		foreach ( var mat in manifest.Materials ?? new() )
		{
			progressToken.ThrowIfCancellationRequested();

			var name = mat.Asset ?? mat.Material ?? "material";
			onProgress?.Invoke( manifest.Assets.Count + manifest.Materials.IndexOf( mat ) + 1, totalAssets, name );

			var baseName = MaterialBaseName( mat );
			// A standalone material is its own asset, so PerAsset gives it its own folder.
			var (_, matDir, texDir) = DirsFor( baseName, mat.GamePath );

			string written;
			if ( materialOutput == MaterialOutput.Terrain )
			{
				written = WriteTerrainMaterial( mat, baseName, stagingDir, assetsDir, matDir, texDir, summary );
			}
			else if ( materialOutput == MaterialOutput.Decal )
			{
				written = WriteDecal( mat, baseName, stagingDir, assetsDir, matDir, texDir, summary );
			}
			else
			{
				written = await WriteVmat( mat, baseName, stagingDir, assetsDir, matDir, texDir, writtenVmats, summary, progressToken );

				// A vmat is just a file we wrote - nothing compiles it for us here (a model
				// would have pulled it in), so register it or it won't show in the asset
				// browser until a rescan. The GameResource paths are saved through the asset
				// system already, which registers and compiles them.
				global::Editor.AssetSystem.RegisterFile( Path.Combine( matDir, baseName + ".vmat" ) );
			}

			Log.Info( $"Imported material {name} -> {written}" );
		}

		// Scene mode: turn the level's placements + lights into a prefab next to the models.
		if ( manifest.Scene is not null )
		{
			if ( manifest.Scene.Warnings is { Count: > 0 } )
				summary.Warnings.AddRange( manifest.Scene.Warnings );

			Directory.CreateDirectory( paths.PrefabDir );
			summary.PrefabPath = ScenePrefabBuilder.Build( manifest.Scene, modelsByGamePath, paths.PrefabDir, summary.Warnings, mirroredByGamePath );
			summary.Placements = manifest.Scene.Placements?.Count ?? 0;
		}

		return summary;
	}

	/// <summary>
	/// Write (or reuse) the .vmat for one material, processing its textures on the way.
	/// Returns the vmat's content path. Shared by mesh slots and standalone material imports.
	/// </summary>
	static async Task<string> WriteVmat( ManifestMaterial mat, string baseName, string stagingDir, string assetsDir,
		string materialsDir, string texturesDir, Dictionary<string, string> writtenVmats, ImportSummary summary, CancellationToken token )
	{
		var cacheKey = $"{materialsDir}|{baseName}";
		if ( writtenVmats.TryGetValue( cacheKey, out var existing ) )
			return existing;

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
		await File.WriteAllTextAsync( vmatPath, vmatText, token );
		summary.Materials++;

		// complex.shader has no displacement input - say so rather than silently dropping it.
		if ( !string.IsNullOrEmpty( mat.Height ) )
			summary.Warnings.Add( $"{baseName}: has a displacement/height map, which complex.shader can't use - ignored." );

		var content = ToContentPath( assetsDir, vmatPath );
		writtenVmats[cacheKey] = content;
		return content;
	}

	/// <summary>
	/// Write a .tmat Terrain Material. Terrain wants separate grayscale maps plus the height
	/// map (which the vmat path has no slot for), and carries metalness as a scalar - so a
	/// metallic texture has nowhere to go and is reported rather than silently dropped.
	/// </summary>
	static string WriteTerrainMaterial( ManifestMaterial mat, string baseName, string stagingDir, string assetsDir,
		string materialsDir, string texturesDir, ImportSummary summary )
	{
		var tex = TextureProcessor.Process( mat, stagingDir, texturesDir, baseName, AlphaRole.Ignore, packRmo: false, wantHeight: true );
		summary.Textures += CountTextures( tex );

		var path = Path.Combine( materialsDir, baseName + ".tmat" );
		var asset = GameResourceWriter.CreateTerrainMaterial( path,
			albedo: TexContent( assetsDir, texturesDir, tex.Color ),
			roughness: TexContent( assetsDir, texturesDir, tex.Roughness ),
			normal: TexContent( assetsDir, texturesDir, tex.Normal ),
			height: TexContent( assetsDir, texturesDir, tex.Height ),
			ao: TexContent( assetsDir, texturesDir, tex.Ao ) );

		if ( asset is null )
		{
			summary.Warnings.Add( $"{baseName}: failed to create the terrain material - see console." );
			return ToContentPath( assetsDir, path );
		}

		summary.Materials++;

		if ( tex.Metallic is not null )
			summary.Warnings.Add( $"{baseName}: terrain materials carry metalness as a single value, not a texture - the metallic map was not used." );
		if ( tex.Height is null )
			summary.Warnings.Add( $"{baseName}: no height/displacement map found - terrain height blending will be flat." );

		return asset.Path;
	}

	/// <summary>
	/// Write a .decal Decal Definition. Decals take ONE packed rough/metal/occlusion map
	/// rather than three, and are masked by the colour texture's alpha - so an opaque source
	/// material makes a decal that covers its whole quad.
	/// </summary>
	static string WriteDecal( ManifestMaterial mat, string baseName, string stagingDir, string assetsDir,
		string materialsDir, string texturesDir, ImportSummary summary )
	{
		// Keep the albedo's alpha as the decal mask whatever the Unreal blend mode says.
		var tex = TextureProcessor.Process( mat, stagingDir, texturesDir, baseName, AlphaRole.Ignore, packRmo: true, wantHeight: true );
		summary.Textures += CountTextures( tex );

		var path = Path.Combine( materialsDir, baseName + ".decal" );
		var asset = GameResourceWriter.CreateDecal( path,
			color: TexContent( assetsDir, texturesDir, tex.Color ),
			normal: TexContent( assetsDir, texturesDir, tex.Normal ),
			rmo: TexContent( assetsDir, texturesDir, tex.RoughMetalOcclusion ),
			emissive: TexContent( assetsDir, texturesDir, tex.Emissive ),
			height: TexContent( assetsDir, texturesDir, tex.Height ) );

		if ( asset is null )
		{
			summary.Warnings.Add( $"{baseName}: failed to create the decal - see console." );
			return ToContentPath( assetsDir, path );
		}

		summary.Materials++;

		if ( tex.Color is null )
			summary.Warnings.Add( $"{baseName}: decal has no colour texture - its alpha is what masks a decal, so this one won't show." );

		return asset.Path;
	}

	/// <summary>
	/// Scan the FBX for numeric-suffixed variants of known material node names
	/// (duplicate-material slots uniquified by the FBX SDK) and remap them to the same
	/// vmat as their base name. False positives from unrelated strings just produce
	/// unused remap entries, which are harmless.
	/// </summary>
	static List<(string slot, string vmat)> SuffixedRemaps( string fbxPath, IReadOnlyList<(string slot, string vmat)> remaps )
	{
		var extra = new List<(string, string)>();

		string text;
		try
		{
			text = Encoding.ASCII.GetString( File.ReadAllBytes( fbxPath ) );
		}
		catch
		{
			return extra;
		}

		var known = remaps.Select( r => r.slot ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		foreach ( var (slot, vmat) in remaps.ToList() )
		{
			if ( string.IsNullOrEmpty( slot ) )
				continue;

			foreach ( System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches( text, System.Text.RegularExpressions.Regex.Escape( slot ) + @"_\d+" ) )
			{
				if ( known.Add( m.Value ) )
					extra.Add( (m.Value, vmat) );
			}
		}

		return extra;
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
		if ( t.Height != null ) n++;
		if ( t.RoughMetalOcclusion != null ) n++;
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
		if ( string.IsNullOrEmpty( path ) )
			return null;

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

	/// <summary>
	/// Folder name for an asset under the PerAsset layout: its own sanitized name at depth 0,
	/// or an ancestor of its /Game path further up. Fab MIs carry meaningless names
	/// (mi_sjfnbeaa) while the human-readable pack name sits a couple of folders above
	/// (.../Fine_American_Road_sjfnbeaa/Medium/MI_sjfnbeaa), so depth 2 names the folder for it.
	/// Never climbs into the "/Game" mount root, and falls back to the own name if the path
	/// is too shallow for the requested depth.
	/// </summary>
	static string PerAssetFolder( string gamePath, string ownName, int depth )
	{
		if ( depth <= 0 || string.IsNullOrEmpty( gamePath ) )
			return Sanitize( ownName );

		var parts = gamePath.Split( '/', StringSplitOptions.RemoveEmptyEntries );

		// Last segment is the asset itself; walk `depth` folders up from it.
		int idx = parts.Length - 1 - depth;

		// parts[0] is normally the "Game" mount - don't name a folder after it.
		int floor = parts.Length > 1 && parts[0].Equals( "Game", StringComparison.OrdinalIgnoreCase ) ? 1 : 0;

		if ( idx < floor || idx >= parts.Length - 1 )
			return Sanitize( ownName );

		return Sanitize( parts[idx] );
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
