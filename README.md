# Unreal Importer

An s&box editor library that imports Unreal Engine / Fab static meshes into s&box as
`.vmdl` + `.vmat` + textures.

It drives a headless Unreal pass (`UnrealEditor-Cmd.exe -run=pythonscript`) to pull raw
FBX/PNG out of a `.uproject`, then generates the s&box assets from that staging folder
inside the editor.

## Requirements

- s&box (editor)
- A local Unreal Engine install matching the source project's `EngineAssociation`
  (found via the registry, then the standard Epic Games install root)
- The Unreal project you're importing from, as a `.uproject` folder

## Usage

1. In the s&box editor, open **Tools → Unreal Importer**.
2. Pick the Unreal project folder (the one containing the `.uproject`).
3. Tick the static meshes you want.
4. Choose an output folder (defaults to `Assets/unrealimport`), pick a layout, and export.

The editor may be unresponsive while Unreal runs.

### Output layouts

| Layout | FBX + vmdl | vmat + textures | Map prefab |
| --- | --- | --- | --- |
| **Grouped** (default) | `<output>/models` | `<output>/materials`, `<output>/textures` | `<output>` |
| **Flat** | `<output>` | `<output>` | `<output>` |
| **Classic Source** | `Assets/models/<sub>` | `Assets/materials/<sub>` | `Assets/prefabs/<sub>` |

Classic Source ignores the picked output folder and hangs off the project's `Assets` root
instead, type-first like a Source game. Its subfolder is free text (`Props/Barrels` nests);
leave it empty to write straight into `Assets/models` and `Assets/materials`.

## How it works

| Stage | Where | What |
| --- | --- | --- |
| Export | `Tools/ue_export.py` | Runs inside headless Unreal. Emits FBX + PNG + `manifest.json` per mesh. Produces nothing s&box specific. |
| Import | `Editor/Import/AssetImporter.cs` | Reads the manifest, generates `.vmdl` / `.vmat` via `Kv3Writer`. |
| Textures | `Editor/Import/TextureProcessor.cs` | RMA channel splitting, normal-map green flip, tint baking. |
| Engine lookup | `Editor/Import/UnrealLocator.cs` | Finds `UnrealEditor-Cmd.exe` for the project's engine version. |

## Installing as a submodule

```
git submodule add https://github.com/MrBrax/unrealimporter.git Libraries/unrealimporter
```

It must live at `Libraries/unrealimporter` — `HeadlessExporter.FindExportScript()` looks
for `Tools/ue_export.py` there first (it does fall back to a recursive search of the
project root).
