using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Editor.UnrealImporter;

/// <summary>
/// Mirrors the manifest.json produced by Tools/ue_export.py (the headless Unreal export).
/// </summary>
public class ImportManifest
{
	[JsonPropertyName( "version" )] public int Version { get; set; }
	[JsonPropertyName( "assets" )] public List<ManifestAsset> Assets { get; set; } = new();

	/// <summary>Present only for scene-mode exports (UE_EXPORT_MAP): the level's placements + lights.</summary>
	[JsonPropertyName( "scene" )] public ManifestScene Scene { get; set; }

	public static ImportManifest Load( string path )
	{
		var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
		return JsonSerializer.Deserialize<ImportManifest>( File.ReadAllText( path ), opts );
	}
}

public class ManifestAsset
{
	[JsonPropertyName( "asset" )] public string Asset { get; set; }

	/// <summary>/Game package path - scene placements reference meshes by this.</summary>
	[JsonPropertyName( "game_path" )] public string GamePath { get; set; }
	[JsonPropertyName( "fbx" )] public string Fbx { get; set; }
	[JsonPropertyName( "import_scale" )] public float ImportScale { get; set; } = 0.3937f;
	[JsonPropertyName( "materials" )] public List<ManifestMaterial> Materials { get; set; } = new();
}

public class ManifestMaterial
{
	/// <summary>FBX material slot name (e.g. "lambert2") - this is the vmdl remap key.</summary>
	[JsonPropertyName( "slot" )] public string Slot { get; set; }

	/// <summary>Source Material Instance name (e.g. "MI_CardboardBoxes_01a") - used for vmat/texture naming + dedup.</summary>
	[JsonPropertyName( "material" )] public string Material { get; set; }

	/// <summary>Unreal blend mode name (BLEND_OPAQUE / BLEND_MASKED / BLEND_TRANSLUCENT...). Null on old manifests.</summary>
	[JsonPropertyName( "blend_mode" )] public string BlendMode { get; set; }

	// Texture role -> staging-relative png path. Null when the material doesn't use that role.
	[JsonPropertyName( "alb" )] public string Alb { get; set; }
	[JsonPropertyName( "nrm" )] public string Nrm { get; set; }
	[JsonPropertyName( "rma" )] public string Rma { get; set; }
	[JsonPropertyName( "rough" )] public string Rough { get; set; }
	[JsonPropertyName( "metal" )] public string Metal { get; set; }
	[JsonPropertyName( "ao" )] public string Ao { get; set; }
	[JsonPropertyName( "emissive" )] public string Emissive { get; set; }
	[JsonPropertyName( "opacity" )] public string Opacity { get; set; }

	/// <summary>Grayscale tint mask (white = full tint). Packed into the normal's alpha by the complex shader.</summary>
	[JsonPropertyName( "tintmask" )] public string TintMask { get; set; }

	/// <summary>Best-guess single tint color [r,g,b,a] in Unreal LINEAR space (sRGB-encode for g_vColorTint).</summary>
	[JsonPropertyName( "tint_color" )] public float[] TintColor { get; set; }

	/// <summary>Multi-zone tint: mask channel ("r"/"g"/"b"/"a") -> LINEAR tint [r,g,b,a]. Baked into the albedo.</summary>
	[JsonPropertyName( "tint_zones" )] public Dictionary<string, float[]> TintZones { get; set; }

	/// <summary>Best-guess tint amount/strength (0..1) -> g_flModelTintAmount.</summary>
	[JsonPropertyName( "tint_amount" )] public float? TintAmount { get; set; }

	/// <summary>All scalar parameter overrides on the Material Instance (kept for fidelity).</summary>
	[JsonPropertyName( "scalar_params" )] public Dictionary<string, float> ScalarParams { get; set; }

	/// <summary>All vector (color) parameter overrides [r,g,b,a] on the Material Instance.</summary>
	[JsonPropertyName( "vector_params" )] public Dictionary<string, float[]> VectorParams { get; set; }
}

public class ManifestScene
{
	[JsonPropertyName( "name" )] public string Name { get; set; }
	[JsonPropertyName( "map" )] public string Map { get; set; }
	[JsonPropertyName( "placements" )] public List<ManifestPlacement> Placements { get; set; } = new();
	[JsonPropertyName( "lights" )] public List<ManifestLight> Lights { get; set; } = new();

	/// <summary>Things the exporter skipped (capped scatter ISMs, landscapes...) - surfaced in the import summary.</summary>
	[JsonPropertyName( "warnings" )] public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// One static-mesh placement in the level. Transform is raw Unreal: centimetres,
/// left-handed X-fwd/Y-right/Z-up, quaternion xyzw. Conversion happens in ScenePrefabBuilder.
/// </summary>
public class ManifestPlacement
{
	[JsonPropertyName( "mesh" )] public string Mesh { get; set; }
	[JsonPropertyName( "name" )] public string Name { get; set; }
	[JsonPropertyName( "pos" )] public float[] Pos { get; set; }
	[JsonPropertyName( "rot" )] public float[] Rot { get; set; }
	[JsonPropertyName( "scale" )] public float[] Scale { get; set; }
}

public class ManifestLight
{
	/// <summary>"point", "spot" or "directional".</summary>
	[JsonPropertyName( "type" )] public string Type { get; set; }
	[JsonPropertyName( "name" )] public string Name { get; set; }
	[JsonPropertyName( "pos" )] public float[] Pos { get; set; }
	[JsonPropertyName( "rot" )] public float[] Rot { get; set; }
	[JsonPropertyName( "scale" )] public float[] Scale { get; set; }
	[JsonPropertyName( "color" )] public float[] Color { get; set; }

	/// <summary>Raw Unreal intensity - unit depends on <see cref="Units"/> (lux for directional).</summary>
	[JsonPropertyName( "intensity" )] public float? Intensity { get; set; }

	/// <summary>Unreal ELightUnits name: CANDELAS / LUMENS / UNITLESS / EV. Debug info - use Candela.</summary>
	[JsonPropertyName( "units" )] public string Units { get; set; }

	/// <summary>Luminous intensity in candela, converted from Intensity+Units by the exporter. Point/spot only.</summary>
	[JsonPropertyName( "candela" )] public float? Candela { get; set; }

	/// <summary>Attenuation radius in centimetres.</summary>
	[JsonPropertyName( "radius" )] public float? Radius { get; set; }
	[JsonPropertyName( "inner_cone" )] public float? InnerCone { get; set; }
	[JsonPropertyName( "outer_cone" )] public float? OuterCone { get; set; }
}
