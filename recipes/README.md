# Recipes

A recipe = one JSON file = one **sequence of Solid Edge operations that has actually been run on a
real machine**. It is the layer between "tool" and "knowledge": instead of an AI re-assembling a COM
call chain from memory every time, the chain lives here as reusable, static-validatable data.

How they are used:

- Run: `se_recipe_run(name, args)` (MCP tool; never writes a recipe)
- List: `solidedge-mcp --recipes` — also prints the search paths actually in effect
- Validate: `solidedge-mcp --recipe-validate <name|path>` — static check, never touches COM

If `SE_MCP_RECIPES_DIR` is unset, the server looks for a `recipes/` directory walking up from the
exe (this repo's layout works out of the box), then falls back to
`%LOCALAPPDATA%\SolidEdgeSpy\recipes`. Files beginning with `_` are drafts and cannot be run by name.

Most recipes take a single `target` input: the document handle, obtained from `se_get_selection`
(which registers the active document as `obj-1` when nothing is selected).

## What's here

| Recipe | What it does |
|---|---|
| `extrude_rect_profile.json` | Extrude — rectangle profile (4 lines + auto-closing keypoints) → new Model |
| `revolve_circle_profile.json` | Revolve — circle profile + axis line → new Model |
| `cut_round_hole_through_next.json` | Cut — round hole, through-next, on a face-local reference plane |
| `dim_drive_geom.json` | Parametric driving dimension: `SetValueEx` changes a value, then read geometry back to confirm it followed |
| `read_part_overview.json` | Read — model / extrusion / cutout counts in one call |
| `read_material.json` | Read — walk to the `Material` property object |
| `feature_health_check.json` | Read — enumerate feature `Status` to find zombie features (`1216476311` = geometry not generated) |
| `probe_destructive_guard.json` | Guardrail self-test — deliberately contains a `Delete`; a correct run **refuses** it and lists the offending step |

These recipes are examples of the recipe format and of verified call chains, not a full modeling
library. See the [main README](../README.md) for the tool overview and the feature-spec IR.
