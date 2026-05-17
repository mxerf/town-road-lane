# Path B — implementation guide (extruded box + DefaultDecalShader)

> **Resume-here document.** After `/compact`, read this + `DECISION_path_forward.md`
> + `RESEARCH_vanilla_decal_pipeline.md` and you have full context to execute Path B.

---

## Goal

Replace `HDRP/Unlit + flat ribbon` with `BH/Decals/DefaultDecalShader + thin
extruded box` so the marking is projected onto the road surface (like a real
decal). Should fix all three visual gaps in one move: floating, no lighting,
weird texture.

## Starting state

- Branch: `v2`
- Working PoC: commit `512149c` ("phase 4 step 3b: Bezier ribbon + borrowed
  vanilla texture")
- File to modify: `src/TownRoadLane/MarkingMeshRenderSystem.cs`
- HDMaterial.ValidateMaterial pattern already in place — keep it
- mesh.bounds.Expand workaround already in place — likely still needed

## What we know from research

- `DefaultDecalShader` exists in CS2 (histogram showed 2 materials use it)
- Previous attempt borrowed `EU_RoadArrow_*` material with this shader →
  zero pixels on flat quad
- Agent's deep dive identified the diagnosis: **shader needs a CUBE volume,
  not flat strip.** Shader projects texture onto whatever surface exists
  inside the volume; with zero-volume input, nothing to project onto
- `colossal_DecalLayerMask = 2` for Roads (so road surface receives it)
- `colossal_TextureArea` is the UV rectangle in the decal atlas (for borrowed
  texture we just want full 0..1)

## Implementation — 3 steps

### Step 1 — Change BuildRibbonMesh to extrude into thin box

Current `BuildRibbonMesh` produces 2 vertices per Bezier sample (left + right
of the curve). Change to 4 vertices per sample: left-bottom, right-bottom,
left-top, right-top. Triangulate top, bottom, sides.

Layout per segment (looking from above):

```
   L_top  ───── R_top         ← box top (kHeightOffset + kBoxHeight)
   ╱│            │╲
  ╱ │            │ ╲
 L_bot ───── R_bot           ← box bottom (kHeightOffset)
```

Per sample point (i):
- `verts[i*4 + 0]` = pos - right*halfW + lift_bottom (L_bot)
- `verts[i*4 + 1]` = pos + right*halfW + lift_bottom (R_bot)
- `verts[i*4 + 2]` = pos - right*halfW + lift_top    (L_top)
- `verts[i*4 + 3]` = pos + right*halfW + lift_top    (R_top)

Per segment (i → i+1): 8 triangles (top quad + bottom quad + 2 side quads).
Be careful with winding — outside should face out.

Suggested constants:
```csharp
private const float kHeightOffset = -0.5f;  // bottom of box below road surface
private const float kBoxHeight    =  1.5f;  // top of box above road surface
// → box spans -0.5m to +1.0m relative to endpoint y, covers road surface
// for typical slopes and crowning
private const float kMarkingWidth = 0.3f;   // narrower for proper paint look
```

Vertex count: `(kSegments+1) * 4`. Triangle count: `kSegments * 8 * 3` (8 tris
per segment × 3 verts each).

UVs: only top face needs UVs for texture (bottom + sides are inside the
projection volume, not visible). For simplicity give all 4 verts per sample
the same UV (u=0 or 1, v=arclen/kTextureTileMeters) — DefaultDecalShader
remaps via colossal_TextureArea anyway.

### Step 2 — Switch material to DefaultDecalShader

Replace current GetMaterial() body:

```csharp
private Material GetMaterial()
{
    if (_material != null) return _material;
    if (_materialAttempted) return null;
    _materialAttempted = true;
    try
    {
        var shader = Shader.Find("BH/Decals/DefaultDecalShader");
        if (shader == null)
        {
            log.Warn("MarkingMeshRenderSystem: DefaultDecalShader not loaded; falling back to HDRP/Unlit");
            return BuildUnlitMaterial();  // keep old path as fallback
        }

        _material = new Material(shader) { name = "TRL_PairPoC_Decal" };

        // Borrowed texture from vanilla LaneLine_* material on our EdgeLineClone
        var tex = TryBorrowLaneLineTexture();
        if (tex != null)
        {
            if (_material.HasProperty("_BaseColorMap")) _material.SetTexture("_BaseColorMap", tex);
            if (_material.HasProperty("_MainTex"))      _material.SetTexture("_MainTex",      tex);
            _material.mainTexture = tex;
        }

        // Decal-specific properties — these are what ManagedBatchSystem
        // sets up for vanilla decal materials, see decomp:
        //   ManagedBatchSystem.cs:1408 → _DecalLayerMask
        //   plus colossal_TextureArea, colossal_MeshSize, colossal_GeometryTiling
        if (_material.HasProperty("colossal_DecalLayerMask"))
            _material.SetFloat("colossal_DecalLayerMask", 2f);  // Roads
        if (_material.HasProperty("colossal_TextureArea"))
            _material.SetVector("colossal_TextureArea", new Vector4(0f, 0f, 1f, 1f));  // full texture
        // _DrawOrder — render priority among decals
        if (_material.HasProperty("_DrawOrder"))
            _material.SetFloat("_DrawOrder", 0f);

        HDMaterial.ValidateMaterial(_material);

        var kw = _material.enabledKeywords;
        string kwStr = ""; for (int i = 0; i < kw.Length; i++) { if (i > 0) kwStr += ","; kwStr += kw[i].name; }
        log.Info($"MarkingMeshRenderSystem: decal material '{_material.name}' shader='{_material.shader.name}' renderQueue={_material.renderQueue} passes={_material.passCount} keywords=[{kwStr}]");
        _materialAttempted = false;
        return _material;
    }
    catch (System.Exception ex)
    {
        log.Error(ex, "MarkingMeshRenderSystem: decal material build threw");
        return null;
    }
}
```

Keep the old `HDRP/Unlit` path as `BuildUnlitMaterial()` so we have a fallback
if `Shader.Find("BH/Decals/DefaultDecalShader")` returns null or the resulting
material gives zero pixels.

### Step 3 — Build, test, log

Diagnostic logging already in place (heartbeat in OnUpdate + Render). On first
load, the material acquisition line will tell us:
- Whether shader was found
- Render queue (should be 2000–3000 range for decals)
- Pass count (should be 2+)
- Keywords (look for `_MATERIAL_AFFECTS_ALBEDO`, NOT `ENABLE_VT`)

Visual test: draw a pair on a flat road, then on an inclined road. Look for:
- ✓ marking on flat road, visible only on road (not on grass alongside)
- ✓ marking follows road slope on hill
- ✓ marking dims at night, brightens in day
- ✓ no z-fighting, no floating gap

## Possible failure modes & responses

| Symptom | Likely cause | Fix |
|---|---|---|
| Shader.Find returns null | DefaultDecalShader not in loaded shaders | Fall back to HDRP/Lit (Path 2) |
| Material constructed but zero pixels | Box geometry wrong — internal faces, wrong winding | Inspect mesh in Unity profiler / RenderDoc |
| Marking renders on EVERYTHING (grass, water) | DecalLayerMask not picked up | Try other property names, see RESEARCH for full list |
| Marking renders ONLY through transparent surfaces | Wrong render queue | Set `_material.renderQueue = 2500` (vanilla decal range) |
| Box visible as 3D shape, not projected | Shader doesn't read texture as decal; treats as regular Lit | DefaultDecalShader may need MaterialPropertyBlock setup per draw — check if missing colossal_MeshSize uniform |
| Performance hits / Editor freeze | Box volume too big, draws too much surface area | Reduce kBoxHeight |

If shader-not-found → instant fallback to Path 2 (HDRP/Lit) and document
result. If geometry/material issue → try increment fixes from the table above;
budget 1h before giving up and falling to Path 11 or Path 2.

## Budget

- Step 1 (mesh code): ~1.5h
- Step 2 (material): ~30min
- Step 3 (build + test + log analysis): ~30min–1h
- Iteration on failure modes: ~1h
- **Total budget: 3-4h (one session)**

If after 4h there's still no improvement over current PoC visual, **stop and
fall back** — keep current MarkingMeshRenderSystem as-is, switch to HDRP/Lit
shader (Path 2 — 30min) for cheap night-glow fix, and move on to segmentation
feature work. Visual quality compromise documented in SESSION snapshot.

## Files to modify

Only one file: `src/TownRoadLane/MarkingMeshRenderSystem.cs`
- Constants block (heights, segments count likely fine at 24)
- `BuildRibbonMesh` → renamed `BuildBoxRibbonMesh` (or just modify in place)
- `GetMaterial` body replaced
- `BuildUnlitMaterial` extracted (current GetMaterial body, kept as fallback)
- `TryBorrowLaneLineTexture` unchanged

No changes to: `Mod.cs`, `EdgeLineCloneSystem.cs`, csproj, anything else.

## Commit strategy

- Commit before starting: working PoC (already at `512149c` — verify)
- After Step 1 only: `phase 4 step 3c-1: extruded box ribbon mesh` (still
  Unlit material, geometry change only — should still render as opaque box;
  visual confirmation that mesh is built correctly)
- After Step 2: `phase 4 step 3c-2: DefaultDecalShader material` (the real test)
- If fallback to Path 2: separate commit `phase 4 step 3d: HDRP/Lit fallback`

## Acceptance criteria

Path B is "done" when ONE of these is true:
- **Best case**: marking renders as proper decal — flush with road, lights
  correctly, no z-fight. Then commit + move to segmentation roadmap step 1
- **Fallback case**: clear data on why DefaultDecalShader doesn't work, plus
  working HDRP/Lit fallback for night-glow. Then update SESSION snapshot with
  findings + close visual-polish chapter for now

## Cross-references (for /compact resume)

- `research/DECISION_path_forward.md` — why Path B was chosen
- `research/RESEARCH_vanilla_decal_pipeline.md` — full vanilla pipeline analysis
- `research/RESEARCH_rendering_approaches.md` — full approach catalogue + web
- `SESSION_2026-05-17.md` — overall session state
- `IMPLEMENTATION_PLAN.md` — long-term plan (Layer 4 still references ECS sublane
  approach in historical section; Path B doesn't need updating it)
- Commits to know:
  - `385b545` — initial working DrawMesh PoC (white quad)
  - `512149c` — current state, Bezier ribbon + borrowed texture
  - `5b3d304`, `18ea12c`, `2ac5981` — research docs
