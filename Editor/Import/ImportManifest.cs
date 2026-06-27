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

	public static ImportManifest Load( string path )
	{
		var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
		return JsonSerializer.Deserialize<ImportManifest>( File.ReadAllText( path ), opts );
	}
}

public class ManifestAsset
{
	[JsonPropertyName( "asset" )] public string Asset { get; set; }
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

	// Texture role -> staging-relative png path. Null when the material doesn't use that role.
	[JsonPropertyName( "alb" )] public string Alb { get; set; }
	[JsonPropertyName( "nrm" )] public string Nrm { get; set; }
	[JsonPropertyName( "rma" )] public string Rma { get; set; }
	[JsonPropertyName( "rough" )] public string Rough { get; set; }
	[JsonPropertyName( "metal" )] public string Metal { get; set; }
	[JsonPropertyName( "ao" )] public string Ao { get; set; }
	[JsonPropertyName( "emissive" )] public string Emissive { get; set; }
	[JsonPropertyName( "opacity" )] public string Opacity { get; set; }
}
