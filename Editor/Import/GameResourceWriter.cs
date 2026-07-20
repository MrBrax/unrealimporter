using Sandbox;
using Sandbox.Resources;

namespace Editor.UnrealImporter;

/// <summary>
/// Creates s&box GameResources - .tmat (Terrain Material) and .decal (Decal Definition).
///
/// These are built through the editor's own asset API rather than by writing json: create the
/// asset, set properties on the real resource object, save. The resource classes' own defaults
/// then apply to everything we don't set, and SaveToDisk serialises, compiles and registers.
/// Only .vmat is hand-written, because it's kv3 with no GameResource behind it.
///
/// NOTE on creating vs editing: Asset.LoadResource needs an up-to-date COMPILED file, which a
/// just-created asset doesn't have yet - it returns null there. So a new resource is
/// constructed with `new T()` (giving us the class defaults) and only an existing one is
/// loaded, which lets a re-import keep whatever the user hand-tuned on it.
/// </summary>
public static class GameResourceWriter
{
	/// <summary>
	/// A Terrain Material. Terrain takes separate grayscale roughness/AO/height maps and a
	/// scalar metalness (there's no metal texture slot), so a metallic map has nowhere to go.
	/// Null paths are simply left at the resource's default image.
	/// </summary>
	public static Asset CreateTerrainMaterial( string absolutePath, string albedo, string roughness, string normal, string height, string ao, float uvScale = 1f )
	{
		var asset = global::Editor.AssetSystem.CreateResource( "tmat", absolutePath );
		if ( asset is null )
			return null;

		// Existing asset -> update it in place; new one -> start from the class defaults.
		var isNew = !asset.TryLoadResource<TerrainMaterial>( out var mat );
		mat ??= new TerrainMaterial();

		if ( !string.IsNullOrEmpty( albedo ) ) mat.AlbedoImage = albedo;
		if ( !string.IsNullOrEmpty( roughness ) ) mat.RoughnessImage = roughness;
		if ( !string.IsNullOrEmpty( normal ) ) mat.NormalImage = normal;
		if ( !string.IsNullOrEmpty( height ) ) mat.HeightImage = height;
		if ( !string.IsNullOrEmpty( ao ) ) mat.AOImage = ao;

		// Tiling and displacement are the two things a user is most likely to tune by hand
		// (we can't read Unreal's tiling), so only seed them on a fresh resource.
		if ( isNew )
		{
			mat.UVScale = uvScale;

			// Displacement does nothing without a height map, and the resource hides the
			// field while HeightImage is still its "no height" default.
			if ( mat.HasHeightTexture )
				mat.DisplacementScale = 1f;
		}

		return asset.SaveToDisk( mat ) ? asset : null;
	}

	/// <summary>
	/// A Decal Definition. Its rough/metal/occlusion is ONE packed map (RGB in that order),
	/// not three, and the colour texture's alpha is what masks the decal.
	/// </summary>
	public static Asset CreateDecal( string absolutePath, string color, string normal, string rmo, string emissive, string height, float size = 32f )
	{
		var asset = global::Editor.AssetSystem.CreateResource( "decal", absolutePath );
		if ( asset is null )
			return null;

		var isNew = !asset.TryLoadResource<DecalDefinition>( out var decal );
		decal ??= new DecalDefinition();

		decal.ColorTexture = ImageTexture( color );
		decal.NormalTexture = ImageTexture( normal );
		decal.RoughMetalOcclusionTexture = ImageTexture( rmo );
		decal.EmissiveTexture = ImageTexture( emissive );
		decal.HeightTexture = ImageTexture( height );

		// Size is a pure guess on our part - don't stomp it on re-import.
		if ( isNew )
		{
			decal.Width = size;
			decal.Height = size;
			// Parallax needs a height map; leave it inert when there isn't one.
			decal.ParallaxStrength = decal.HeightTexture is null ? 0f : 1f;
		}

		return asset.SaveToDisk( decal ) ? asset : null;
	}

	/// <summary>
	/// A Texture backed by an image file on disk. Going through ImageFileGenerator (rather
	/// than Texture.Load) is what gives the texture its EmbeddedResource, which is how the
	/// image path survives serialisation into the resource's json.
	/// </summary>
	static Texture ImageTexture( string contentPath )
	{
		if ( string.IsNullOrEmpty( contentPath ) )
			return null;

		var generator = new ImageFileGenerator { FilePath = contentPath };
		return generator.FindOrCreate( ResourceGenerator.Options.Default );
	}
}
