# TODO

Open work that is not yet in `DESIGN.md`'s build order (§4.9, §12). Keep entries short: what, why, and what would tell us to do it.

## HUD: share more between desktop and browser

**Today.** The desktop HUD is ImGui (`HudTheme`, `GameHud.*`); the browser HUD is DOM and CSS in `src/Emberward.Browser/wwwroot`,
fed by `BrowserFrameBuilder`. Each panel is built twice. As the HUD grows (energy labels, per-machine upgrade panels,
generator and turret readouts) the cost of keeping the two in step grows with it.

**Plan, in order.**

1. **One view-model per panel.** Rows, text, colour levels, enabled states and which machines get a label are computed
   once, in code that needs no window, and tested once. Each platform only maps the record to widgets. Do this for every
   new HUD element from now on.
2. **World-space overlays in the shared renderer.** Charge and flow labels, gauges above machines and fog-aware markers
   belong with the entity layer, which both platforms already share through the HLSL-to-GLSL shaders. Add small text
   quads there (font atlas or SDF) instead of drawing them twice.
3. **Panels and menus stay per-platform** (ImGui and DOM). A shared widget toolkit would need text layout, hit-testing,
   focus, scrolling and touch, and the browser would lose selectable text, page zoom, IME and accessibility.

**Not now: dropping ImGui.** Revisit if a third platform appears, if one panel change routinely needs edits in both
clients, or if the HUD turns out to be mostly read-only gauges.

**First step.** Count HUD code per platform, split into read-only and interactive, to see how much a shared renderer
would actually replace.
