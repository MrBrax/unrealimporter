"""
ue_export.py  -  Headless Unreal -> s&box asset exporter

Runs inside Unreal (UnrealEditor-Cmd.exe -run=pythonscript) and, for every
StaticMesh under the given content path(s):

  * exports the mesh to FBX  (no LODs, no collision - s&box generates those)
  * exports each referenced texture (ALB / NRM / RMA ...) to PNG
  * records material slot names + texture bindings

It writes everything under an output (staging) folder, mirroring the /Game
package structure, plus a single `manifest.json` the s&box tool consumes.

NOTHING here is s&box specific - it only produces raw FBX + PNG + JSON.
RMA splitting, normal green-flip, renaming and vmdl/vmat generation all happen
later in the s&box editor tool (using Bitmap/Pixmap).

Configuration (env vars override the CONFIG defaults below):
  UE_EXPORT_OUT     absolute staging output dir
  UE_EXPORT_PATHS   ';'-separated /Game content paths to scan

Example command line (PowerShell):
  $env:UE_EXPORT_OUT   = "C:\\Temp\\ue_stage"
  $env:UE_EXPORT_PATHS = "/Game/Construction_VOL1/Meshes"
  & "C:\\Program Files\\Epic Games\\UE_5.5\\Engine\\Binaries\\Win64\\UnrealEditor-Cmd.exe" `
      "C:\\Users\\Braxen\\Documents\\Unreal Projects\\ripper_5_4\\ripper_5_4.uproject" `
      -run=pythonscript -script="<this file>" `
      -EnablePlugins=PythonScriptPlugin -unattended -nosplash -nullrhi
"""

import os
import json
import unreal


# ----------------------------------------------------------------------------
# CONFIG - edit these for a quick manual run, or set the env vars instead.
# ----------------------------------------------------------------------------
CONFIG_OUT = r"C:\Temp\ue_stage"
CONFIG_PATHS = ["/Game/Construction_VOL1/Meshes"]
CONFIG_LIMIT = 0   # max meshes to export (0 = no limit). Override with UE_EXPORT_LIMIT.

# Texture role classification by filename suffix (case-insensitive).
# Add more roles here if a pack uses different conventions.
ROLE_SUFFIXES = {
    "alb": ["_alb", "_albedo", "_basecolor", "_color", "_d", "_diff"],
    "nrm": ["_nrm", "_normal", "_n"],
    "rma": ["_rma", "_orm", "_arm", "_mra"],
    "rough": ["_rough", "_roughness", "_r"],
    "metal": ["_metal", "_metallic", "_m"],
    "ao": ["_ao", "_occlusion"],
    "emissive": ["_emissive", "_emis", "_e"],
    "opacity": ["_opacity", "_mask", "_alpha"],
}

log = unreal.log
warn = unreal.log_warning
err = unreal.log_error


def get_out_dir():
    out = os.environ.get("UE_EXPORT_OUT", CONFIG_OUT)
    os.makedirs(out, exist_ok=True)
    return out


def get_scan_paths():
    raw = os.environ.get("UE_EXPORT_PATHS")
    if raw:
        return [p for p in raw.split(";") if p.strip()]
    return CONFIG_PATHS


def classify_texture(name):
    """Return a role string ('alb'/'nrm'/'rma'/...) from a texture name, or None."""
    lname = name.lower()
    for role, suffixes in ROLE_SUFFIXES.items():
        for s in suffixes:
            if lname.endswith(s):
                return role
    return None


def package_relpath(asset):
    """'/Game/Foo/Bar/SM_X.SM_X' -> 'Foo/Bar/SM_X' (forward slashes, no /Game)."""
    p = asset.get_path_name()          # /Game/Foo/Bar/SM_X.SM_X
    p = p.split(".")[0]                # drop the .ObjectName
    if p.startswith("/Game/"):
        p = p[len("/Game/"):]
    return p


def export_one(asset, out_dir, extension, options=None):
    """Export a single asset to <out_dir>/<package path>.<extension>. Returns abs path or None."""
    rel = package_relpath(asset) + "." + extension
    abs_path = os.path.join(out_dir, rel.replace("/", os.sep))
    os.makedirs(os.path.dirname(abs_path), exist_ok=True)

    task = unreal.AssetExportTask()
    task.object = asset
    task.filename = abs_path
    task.automated = True
    task.prompt = False
    task.replace_identical = True
    if options is not None:
        task.options = options
    # exporter left unset -> engine picks one by file extension (.fbx / .png)

    ok = unreal.Exporter.run_asset_export_task(task)
    if not ok:
        err("  ! export failed: {}".format(abs_path))
        return None
    return rel.replace(os.sep, "/")


def fbx_options():
    opt = unreal.FbxExportOption()
    opt.collision = False
    opt.level_of_detail = False     # export LOD0 only; s&box auto-LOD handles the rest
    opt.vertex_color = True
    return opt


def list_static_meshes(paths):
    eal = unreal.EditorAssetLibrary
    found = []
    seen = set()
    for base in paths:
        for obj_path in eal.list_assets(base, recursive=True, include_folder=False):
            if obj_path in seen:
                continue
            asset = unreal.load_asset(obj_path)
            if isinstance(asset, unreal.StaticMesh):
                seen.add(obj_path)
                found.append(asset)
    return found


def load_static_meshes(asset_paths):
    """Load an explicit list of /Game asset paths, keeping only StaticMeshes."""
    found, seen = [], set()
    for p in asset_paths:
        asset = unreal.load_asset(p)
        if isinstance(asset, unreal.StaticMesh) and asset.get_path_name() not in seen:
            seen.add(asset.get_path_name())
            found.append(asset)
        elif asset is None:
            warn("could not load asset: {}".format(p))
    return found


def textures_from_instance(mi):
    """Textures a MaterialInstance overrides, as a list of unreal.Texture (deduped)."""
    out, seen = [], set()
    try:
        tpvs = mi.get_editor_property("texture_parameter_values")
    except Exception:
        tpvs = []
    for tpv in tpvs or []:
        tex = tpv.get_editor_property("parameter_value")
        if tex and tex.get_path_name() not in seen:
            seen.add(tex.get_path_name())
            out.append(tex)
    return out


def textures_from_dependencies(mi):
    """Fallback: every Texture this material package references (via the asset registry)."""
    ar = unreal.AssetRegistryHelpers.get_asset_registry()
    pkg = mi.get_outermost().get_name()    # /Game/.../MI_X
    opts = unreal.AssetRegistryDependencyOptions()
    opts.include_hard_package_references = True
    opts.include_soft_package_references = True
    out, seen = [], set()
    for dep in ar.get_dependencies(unreal.Name(pkg), opts) or []:
        for ad in ar.get_assets_by_package_name(dep):
            asset = ad.get_asset()
            if isinstance(asset, unreal.Texture) and asset.get_path_name() not in seen:
                seen.add(asset.get_path_name())
                out.append(asset)
    return out


def texture_bindings_for_mesh(mesh, out_dir, exported_textures):
    """Return a list of {slot, <role>: relpath...} per material slot, exporting textures as needed."""
    materials = []
    for sm in mesh.static_materials:
        mi = sm.material_interface
        slot = str(sm.material_slot_name)
        entry = {"slot": slot}
        if mi is not None:
            entry["material"] = mi.get_name()
        if mi is None:
            warn("  slot '{}' has no material".format(slot))
            materials.append(entry)
            continue

        texs = textures_from_instance(mi)
        if not texs:
            texs = textures_from_dependencies(mi)
        log("  slot='{}' mi='{}' textures={}".format(
            slot, mi.get_name(), [t.get_name() for t in texs]))

        for tex in texs:
            role = classify_texture(tex.get_name())
            if role is None:
                warn("    unclassified texture '{}' (skipped)".format(tex.get_name()))
                continue
            key = tex.get_path_name()
            if key not in exported_textures:
                exported_textures[key] = export_one(tex, out_dir, "png")
            relpath = exported_textures[key]
            if relpath:
                entry[role] = relpath
        materials.append(entry)
    return materials


def main():
    out_dir = get_out_dir()

    # Explicit selection (UE_EXPORT_ASSETS, ';'-separated /Game paths) takes priority;
    # otherwise scan whole content paths (UE_EXPORT_PATHS).
    assets_file = os.environ.get("UE_EXPORT_ASSETS_FILE")
    assets_env = os.environ.get("UE_EXPORT_ASSETS")
    if assets_file and os.path.exists(assets_file):
        with open(assets_file) as fh:
            asset_paths = [ln.strip() for ln in fh if ln.strip()]
        log("=== ue_export: {} selected asset(s) from file -> {} ===".format(len(asset_paths), out_dir))
        meshes = load_static_meshes(asset_paths)
    elif assets_env:
        asset_paths = [p for p in assets_env.split(";") if p.strip()]
        log("=== ue_export: {} selected asset(s) -> {} ===".format(len(asset_paths), out_dir))
        meshes = load_static_meshes(asset_paths)
    else:
        paths = get_scan_paths()
        log("=== ue_export: scanning {} -> {} ===".format(paths, out_dir))
        meshes = list_static_meshes(paths)
    log("Found {} static mesh(es)".format(len(meshes)))

    limit = int(os.environ.get("UE_EXPORT_LIMIT", CONFIG_LIMIT))
    if limit > 0:
        meshes = meshes[:limit]
        log("Limiting to first {}".format(limit))

    exported_textures = {}   # texture object path -> exported relpath (dedupe shared textures)
    manifest = {"version": 1, "assets": []}

    for i, mesh in enumerate(meshes):
        name = mesh.get_name()
        log("[{}/{}] {}".format(i + 1, len(meshes), name))

        fbx_rel = export_one(mesh, out_dir, "fbx", options=fbx_options())
        if fbx_rel is None:
            continue

        materials = texture_bindings_for_mesh(mesh, out_dir, exported_textures)
        manifest["assets"].append({
            "asset": name,
            "fbx": fbx_rel,
            "import_scale": 0.3937,           # Unreal cm -> Source inch (1 / 2.54)
            "materials": materials,
        })

    manifest_path = os.path.join(out_dir, "manifest.json")
    with open(manifest_path, "w") as f:
        json.dump(manifest, f, indent=2)
    log("=== wrote {} ({} assets, {} textures) ===".format(
        manifest_path, len(manifest["assets"]), len(exported_textures)))


if __name__ == "__main__":
    main()
