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

Scene mode (UE_EXPORT_MAP set): loads the given .umap in the headless editor,
records every StaticMeshComponent placement (plain, instanced and blueprint-owned)
plus point/spot/directional lights, then exports the unique meshes it referenced
through the normal pipeline. The manifest gains a "scene" section the s&box tool
turns into a prefab. Loading through the editor means One-File-Per-Actor maps
work transparently. World Partition maps get every external actor force-loaded
via WorldPartitionBlueprintLibrary before the walk; HLOD proxies are skipped and
huge scatter ISMs (PCG grass etc.) are dropped past UE_EXPORT_MAX_INSTANCES.
Landscapes have no mesh to export and are reported as a warning.

Configuration (env vars override the CONFIG defaults below):
  UE_EXPORT_OUT     absolute staging output dir
  UE_EXPORT_PATHS   ';'-separated /Game content paths to scan
  UE_EXPORT_MAP     /Game path of a .umap - scene mode (overrides asset selection)
  UE_EXPORT_MAX_INSTANCES  per-component ISM instance cap in scene mode (default 1000)
  UE_EXPORT_MAX_MESH_PLACEMENTS  per-mesh total placement cap in scene mode (default 2000).
                    A mesh placed more often than this is scatter (PCG grass chunks etc.) -
                    all its placements are dropped, with a warning. 0 disables.

Example command line (PowerShell):
  $env:UE_EXPORT_OUT   = "C:\\Temp\\ue_stage"
  $env:UE_EXPORT_PATHS = "/Game/Construction_VOL1/Meshes"
  & "C:\\Program Files\\Epic Games\\UE_5.5\\Engine\\Binaries\\Win64\\UnrealEditor-Cmd.exe" `
      "C:\\Users\\Profile\\Documents\\Unreal Projects\\ripper_5_4\\ripper_5_4.uproject" `
      -run=pythonscript -script="<this file>" `
      -EnablePlugins=PythonScriptPlugin -unattended -nosplash -nullrhi
"""

import os
import json
import math
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


def classify_param_name(pname):
    """Return a role from a material *parameter* name (more reliable than the filename), or None."""
    p = (pname or "").lower().replace(" ", "").replace("_", "")
    if not p:
        return None
    # tint / color mask first (otherwise "mask" or "color" would mis-route)
    if ("tint" in p and "mask" in p) or "colormask" in p or "tintmask" in p:
        return "tintmask"
    if "normal" in p:
        return "nrm"
    if "rma" in p or "orm" in p or "arm" in p or "mra" in p:
        return "rma"
    if "basecolor" in p or "albedo" in p or "diffuse" in p or p in ("color", "basecolour"):
        return "alb"
    if "roughness" in p:
        return "rough"
    if "metallic" in p or "metalness" in p:
        return "metal"
    if "ambientocclusion" in p or "occlusion" in p or p == "ao":
        return "ao"
    if "emissive" in p or "emission" in p:
        return "emissive"
    if "opacity" in p or "alpha" in p:
        return "opacity"
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
    opt.export_source_mesh = True
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


def _param_name(struct):
    """Pull the parameter name out of a *ParameterValue struct's parameter_info."""
    try:
        info = struct.get_editor_property("parameter_info")
        return str(info.get_editor_property("name"))
    except Exception:
        return ""


def textures_from_instance(mi):
    """(param_name, texture) pairs a MaterialInstance overrides (deduped by texture)."""
    out, seen = [], set()
    try:
        tpvs = mi.get_editor_property("texture_parameter_values")
    except Exception:
        tpvs = []
    for tpv in tpvs or []:
        tex = tpv.get_editor_property("parameter_value")
        if tex and tex.get_path_name() not in seen:
            seen.add(tex.get_path_name())
            out.append((_param_name(tpv), tex))
    return out


def scalars_from_instance(mi):
    """{param_name: float} scalar overrides on a MaterialInstance."""
    out = {}
    try:
        svs = mi.get_editor_property("scalar_parameter_values")
    except Exception:
        svs = []
    for sv in svs or []:
        name = _param_name(sv)
        if not name:
            continue
        try:
            out[name] = float(sv.get_editor_property("parameter_value"))
        except Exception:
            pass
    return out


def vectors_from_instance(mi):
    """{param_name: [r,g,b,a]} vector (LinearColor) overrides on a MaterialInstance."""
    out = {}
    try:
        vvs = mi.get_editor_property("vector_parameter_values")
    except Exception:
        vvs = []
    for vv in vvs or []:
        name = _param_name(vv)
        if not name:
            continue
        try:
            c = vv.get_editor_property("parameter_value")  # unreal.LinearColor
            out[name] = [c.r, c.g, c.b, c.a]
        except Exception:
            pass
    return out


def tint_channel(name):
    """Mask channel ('r'/'g'/'b'/'a') a per-channel tint param drives, e.g. 'Base Color Tint (Red)'."""
    n = name.lower()
    if "(red)" in n or "_red" in n or n.endswith(" red"):
        return "r"
    if "(green)" in n or "_green" in n or n.endswith(" green"):
        return "g"
    if "(blue)" in n or "_blue" in n or n.endswith(" blue"):
        return "b"
    if "(alpha)" in n or n.endswith(" alpha"):
        return "a"
    return None


def pick_tint_zones(vectors):
    """{channel: [r,g,b,a]} for multi-zone tint masks (each tint color keyed to a mask channel)."""
    zones = {}
    for name, val in vectors.items():
        if "tint" not in name.lower():
            continue
        ch = tint_channel(name)
        if ch and ch not in zones:
            zones[ch] = val
    return zones


def pick_tint_color(vectors):
    """Single (non-channel) tint color: a 'tint' vector param that isn't emissive/specular/per-channel."""
    for name, val in vectors.items():
        n = name.lower()
        if "tint" in n and "emiss" not in n and "spec" not in n and tint_channel(name) is None:
            return val
    return None


def pick_tint_amount(scalars):
    """Best-guess tint amount/strength from scalar params, or None."""
    for name, val in scalars.items():
        n = name.lower()
        if "tint" in n and ("amount" in n or "strength" in n or "opacity" in n or "intensity" in n):
            return val
    return None


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

        named_texs = textures_from_instance(mi)
        if not named_texs:
            named_texs = [("", t) for t in textures_from_dependencies(mi)]
        log("  slot='{}' mi='{}' textures={}".format(
            slot, mi.get_name(), [t.get_name() for _, t in named_texs]))

        # Scalar/vector params: keep the full set for fidelity, plus a best-guess tint.
        scalars = scalars_from_instance(mi)
        vectors = vectors_from_instance(mi)
        if scalars:
            entry["scalar_params"] = scalars
        if vectors:
            entry["vector_params"] = vectors
        # Multi-zone tint mask (per-channel tint colors) must be baked into the albedo - s&box's
        # complex shader only has a single tint. A single uniform tint can stay dynamic.
        tint_zones = pick_tint_zones(vectors)
        if tint_zones:
            entry["tint_zones"] = tint_zones
            log("    tint_zones={}".format(tint_zones))
        else:
            tint_color = pick_tint_color(vectors)
            if tint_color is not None:
                entry["tint_color"] = tint_color
                log("    tint_color={}".format(tint_color))
        tint_amount = pick_tint_amount(scalars)
        if tint_amount is not None:
            entry["tint_amount"] = tint_amount

        for pname, tex in named_texs:
            # The parameter name (e.g. "Tint Mask") is more reliable than the filename suffix.
            role = classify_param_name(pname) or classify_texture(tex.get_name())
            if role is None:
                warn("    unclassified texture '{}' (param '{}') (skipped)".format(tex.get_name(), pname))
                continue
            key = tex.get_path_name()
            if key not in exported_textures:
                exported_textures[key] = export_one(tex, out_dir, "png")
            relpath = exported_textures[key]
            if relpath:
                entry[role] = relpath
        materials.append(entry)
    return materials


def mesh_game_path(asset):
    """'/Game/Foo/SM_X' package path for an asset (no .ObjectName suffix)."""
    return asset.get_path_name().split(".")[0]


def transform_to_dict(t):
    """unreal.Transform -> {pos (cm), rot (quat xyzw), scale} plain lists."""
    loc, rot, s = t.translation, t.rotation, t.scale3d
    return {
        "pos": [loc.x, loc.y, loc.z],
        "rot": [rot.x, rot.y, rot.z, rot.w],
        "scale": [s.x, s.y, s.z],
    }


def world_transform_of(comp):
    """World transform of a scene component, tolerating API differences."""
    try:
        return comp.get_world_transform()
    except Exception:
        # Older exposure: compose from location/rotation/scale.
        return unreal.Transform(
            comp.get_world_location(),
            comp.get_world_rotation(),
            comp.get_world_scale())


def instance_world_transforms(comp):
    """World transforms of an ISM/HISM component's instances (out-params vary by version)."""
    out = []
    for i in range(comp.get_instance_count()):
        t = comp.get_instance_transform(i, world_space=True)
        if isinstance(t, tuple):          # some versions return (success, transform)
            t = t[-1]
        if t is not None:
            out.append(t)
    return out


def light_candela(comp, kind, intensity, entry):
    """Convert a local light's intensity to candela, honouring its ELightUnits.

    UE stores intensity in per-light units (candelas / lumens / legacy unitless / EV).
    Comparing raw values across units is meaningless - this is why naive brightness
    mapping made everything blinding. Uses UE's own conversion factor when exposed,
    else mirrors ULocalLightComponent::GetUnitsConversionFactor.
    """
    try:
        units = comp.get_editor_property("intensity_units")
    except Exception:
        return None
    # repr looks like "<LightUnits.UNITLESS: 0>" - extract the bare enum name.
    entry["units"] = str(units).split("LightUnits.")[-1].split(":")[0].strip(" >")

    # Solid-angle term: spot cone half-angle, full sphere for point lights.
    cos_half = -1.0
    if kind == "spot":
        cos_half = math.cos(math.radians(entry.get("outer_cone", 44.0)))

    try:
        factor = unreal.LocalLightComponent.get_units_conversion_factor(
            units, unreal.LightUnits.CANDELAS, cos_half)
        return intensity * float(factor)
    except Exception:
        pass

    sr = 2.0 * math.pi * (1.0 - cos_half)
    u = entry["units"]
    if u == "CANDELAS":
        return intensity
    if u == "LUMENS":
        return intensity / sr if sr > 0 else intensity
    if u == "EV":
        return 2.0 ** intensity          # EV100-style: 1 EV step doubles brightness
    return intensity * 16.0 / 10000.0    # UNITLESS legacy factor from UE source


def load_world_partition_actors():
    """Force-load every external actor of a World Partition map. No-op for classic maps."""
    try:
        descs = unreal.WorldPartitionBlueprintLibrary.get_actor_descs()
    except Exception:
        return
    if not descs:
        return

    guids = []
    for d in descs:
        try:
            guids.append(d.get_editor_property("guid"))
        except Exception:
            pass
    if not guids:
        return

    log("world partition map: force-loading {} external actors...".format(len(guids)))
    try:
        # Returns False when some actors can't load (PCG partitions etc.) - fine, take what we get.
        unreal.WorldPartitionBlueprintLibrary.load_actors(guids)
    except Exception as e:
        warn("world partition load_actors failed: {}".format(e))


def collect_scene(actors):
    """Walk level actors -> (placements, lights, {game_path: mesh}, warnings)."""
    placements, lights, meshes, warnings = [], [], {}, []
    max_instances = int(os.environ.get("UE_EXPORT_MAX_INSTANCES", "1000"))
    landscapes = 0

    for actor in actors:
        label = str(actor.get_actor_label())
        cls = actor.get_class().get_name()

        # HLOD proxies duplicate real geometry as baked merged meshes - never export them.
        if cls == "WorldPartitionHLOD":
            continue
        if cls in ("Landscape", "LandscapeStreamingProxy"):
            landscapes += 1
            continue

        for comp in actor.get_components_by_class(unreal.StaticMeshComponent):
            mesh = comp.get_editor_property("static_mesh")
            if mesh is None:
                continue
            try:
                if not comp.is_visible():
                    continue
            except Exception:
                pass

            if isinstance(comp, unreal.InstancedStaticMeshComponent):
                count = comp.get_instance_count()
                if count > max_instances:
                    warnings.append("{}: skipped {} '{}' with {} instances of {} (over UE_EXPORT_MAX_INSTANCES={})".format(
                        label, comp.get_class().get_name(), comp.get_name(), count, mesh.get_name(), max_instances))
                    warn("  " + warnings[-1])
                    continue
                transforms = instance_world_transforms(comp)
            else:
                transforms = [world_transform_of(comp)]

            gp = mesh_game_path(mesh)
            meshes.setdefault(gp, mesh)

            for t in transforms:
                entry = transform_to_dict(t)
                entry["mesh"] = gp
                entry["name"] = label
                placements.append(entry)

        for comp in actor.get_components_by_class(unreal.LightComponent):
            if isinstance(comp, unreal.SpotLightComponent):
                kind = "spot"
            elif isinstance(comp, unreal.PointLightComponent):
                kind = "point"
            elif isinstance(comp, unreal.DirectionalLightComponent):
                kind = "directional"
            else:
                continue    # sky lights, rect lights: no s&box counterpart wired up

            entry = transform_to_dict(world_transform_of(comp))
            entry["type"] = kind
            entry["name"] = label
            try:
                c = comp.get_editor_property("light_color")   # 0-255 bytes
                entry["color"] = [c.r / 255.0, c.g / 255.0, c.b / 255.0, 1.0]
                intensity = float(comp.get_editor_property("intensity"))
                entry["intensity"] = intensity
                if kind == "spot":
                    entry["inner_cone"] = float(comp.get_editor_property("inner_cone_angle"))
                    entry["outer_cone"] = float(comp.get_editor_property("outer_cone_angle"))
                if kind != "directional":
                    entry["radius"] = float(comp.get_editor_property("attenuation_radius"))
                    candela = light_candela(comp, kind, intensity, entry)
                    if candela is not None:
                        entry["candela"] = candela
                # directional intensity is lux - passed through raw
            except Exception as e:
                warn("  light '{}': {}".format(label, e))
            lights.append(entry)

    if landscapes:
        warnings.append("map has {} Landscape actor(s) - landscapes have no static mesh and were not exported.".format(landscapes))
        warn(warnings[-1])

    return placements, lights, meshes, warnings


def drop_scatter_meshes(placements, meshes, warnings):
    """Drop every placement of meshes placed absurdly often - that's PCG/foliage scatter,
    which would bloat the prefab into six figures of GameObjects."""
    max_per_mesh = int(os.environ.get("UE_EXPORT_MAX_MESH_PLACEMENTS", "2000"))
    if max_per_mesh <= 0:
        return placements

    counts = {}
    for p in placements:
        counts[p["mesh"]] = counts.get(p["mesh"], 0) + 1

    drop = {gp for gp, n in counts.items() if n > max_per_mesh}
    for gp in sorted(drop):
        warnings.append("dropped all {} placements of {} (over UE_EXPORT_MAX_MESH_PLACEMENTS={} - treated as scatter)".format(
            counts[gp], gp, max_per_mesh))
        warn(warnings[-1])
        meshes.pop(gp, None)

    if not drop:
        return placements
    return [p for p in placements if p["mesh"] not in drop]


def export_meshes(meshes, out_dir, manifest):
    """Run the standard per-mesh export (FBX + textures + material bindings)."""
    exported_textures = {}   # texture object path -> exported relpath (dedupe shared textures)

    for i, mesh in enumerate(meshes):
        name = mesh.get_name()
        log("[{}/{}] {}".format(i + 1, len(meshes), name))

        fbx_rel = export_one(mesh, out_dir, "fbx", options=fbx_options())
        if fbx_rel is None:
            continue

        materials = texture_bindings_for_mesh(mesh, out_dir, exported_textures)
        manifest["assets"].append({
            "asset": name,
            "game_path": mesh_game_path(mesh),
            "fbx": fbx_rel,
            "import_scale": 0.3937,           # Unreal cm -> Source inch (1 / 2.54)
            "materials": materials,
        })

    return exported_textures


def scene_mode(map_path, out_dir, limit):
    log("=== ue_export: scene mode, map {} -> {} ===".format(map_path, out_dir))

    if not unreal.EditorLoadingAndSavingUtils.load_map(map_path):
        err("could not load map: {}".format(map_path))
        return None

    load_world_partition_actors()

    try:
        actors = unreal.get_editor_subsystem(unreal.EditorActorSubsystem).get_all_level_actors()
    except Exception:
        actors = unreal.EditorLevelLibrary.get_all_level_actors()

    placements, lights, mesh_map, scene_warnings = collect_scene(actors)
    placements = drop_scatter_meshes(placements, mesh_map, scene_warnings)
    log("{} actors -> {} placements, {} lights, {} unique meshes".format(
        len(actors), len(placements), len(lights), len(mesh_map)))

    meshes = list(mesh_map.values())
    if limit > 0:
        meshes = meshes[:limit]
        kept = {mesh_game_path(m) for m in meshes}
        placements = [p for p in placements if p["mesh"] in kept]
        log("Limiting to first {} meshes ({} placements)".format(limit, len(placements)))

    manifest = {"version": 1, "assets": []}
    exported_textures = export_meshes(meshes, out_dir, manifest)

    manifest["scene"] = {
        "name": map_path.rstrip("/").split("/")[-1],
        "map": map_path,
        "placements": placements,
        "lights": lights,
        "warnings": scene_warnings,
    }
    return manifest, exported_textures


def main():
    out_dir = get_out_dir()
    limit = int(os.environ.get("UE_EXPORT_LIMIT", CONFIG_LIMIT))

    map_path = os.environ.get("UE_EXPORT_MAP")
    if map_path:
        result = scene_mode(map_path, out_dir, limit)
        if result is None:
            return
        manifest, exported_textures = result

        manifest_path = os.path.join(out_dir, "manifest.json")
        with open(manifest_path, "w") as f:
            json.dump(manifest, f, indent=2)
        log("=== wrote {} ({} assets, {} textures, {} placements) ===".format(
            manifest_path, len(manifest["assets"]), len(exported_textures),
            len(manifest["scene"]["placements"])))
        return

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

    if limit > 0:
        meshes = meshes[:limit]
        log("Limiting to first {}".format(limit))

    manifest = {"version": 1, "assets": []}
    exported_textures = export_meshes(meshes, out_dir, manifest)

    manifest_path = os.path.join(out_dir, "manifest.json")
    with open(manifest_path, "w") as f:
        json.dump(manifest, f, indent=2)
    log("=== wrote {} ({} assets, {} textures) ===".format(
        manifest_path, len(manifest["assets"]), len(exported_textures)))


if __name__ == "__main__":
    main()
