---
applyTo: "Assets/ZenGarden/**,src/SensoryStudio.WinUI/Experiences/ZenGardenExperience.xaml.cs,src/SensoryStudio.WinUI/Services/HapticService.cs"
---

# Zen Garden haptics and physics instructions

Use these rules when changing Zen Garden interaction feedback or rock physics.

Zen Garden is a self-contained WebGL page in `Assets/ZenGarden/zen-garden.html`,
hosted by `src/SensoryStudio.WinUI/Experiences/ZenGardenExperience.xaml.cs`.
It raises semantic string events (`rock-aligned`, `rock-drag-start`, ...) that the
host maps to `HapticsType`. WinUI is the only implementation; preserve consistency
between the page's events and the native host.

## Interaction vocabulary

Keep each waveform tied to one physical meaning:

| Interaction | Haptic |
| --- | --- |
| Rake moving through sand | Distance-driven `Hover`, using `HapticService` fallback routing |
| Rock pickup | One `Hover` |
| Rock drag begins | One `Grow` after movement crosses the drag threshold |
| Rock moving through sand | No repeated haptic |
| Rock alignment locks | One `Align` (carried stones only) |
| Rock hits a rock or canvas edge | Cooldown-gated `Collide` |
| Rock lands on sand or another rock | Cooldown-gated `Collide` |
| Sweep erases disturbed sand | Low-intensity `Collide` |

Do not use `Step` for rake or rock dragging. Hardware testing found it muddy. Do not repeat
`Hover`, `Grow`, or `Collide` during a rock drag; repeated rock texture competes with semantic
alignment and impact feedback.

Resolve named waveforms through `HapticService`. Preserve its documented fallback
IDs and the `Send` / `SendTextured` routing. Honor the global haptics toggle and intensity slider.
Devices without intensity support must still receive an unscaled pulse rather than losing feedback.

## Rake behavior

- Drive rake texture by accumulated world-space distance, not a fixed timer. Faster movement must
  naturally cross more grain intervals rather than slowing the pulse cadence.
- Preserve deterministic spacing variation so sand does not feel mechanically uniform.
- Keep the page's `RAKE_HAPTIC_FLOOR_MS` send floor to avoid saturating the device.
- Emit no stationary rake pulses.
- Use high-resolution input timestamps for speed calculations.
- Retain only a bounded amount of unplayed travel so delayed input cannot create a burst.
- Keep high-speed raking filled in by modestly reducing grain spacing and increasing intensity;
  do not make fast raking feel empty.
- Stop drawing, audio, and haptics when the rake reaches a grounded rock or exits the canvas.
- Treat a rock or canvas exit as a temporary suspension while the button remains held. Start a
  fresh stroke when the pointer clears the rock or re-enters the canvas; never draw a bridge across
  the blocked or outside region.
- Detect rock contact against the oriented rake head. The full rake width is transverse to motion;
  do not treat it as a circular radius in the travel direction.

Current tuned rake constants in `zen-garden.html` are intentional:

- grain spacing: 14% of rake width;
- deterministic spacing jitter: +/-18%;
- high-speed spacing reduction: up to 30%;
- minimum send interval: 32 ms;
- fast-speed normalization: 1.25 world units/second.

Change these only with physical-device testing.

## Rock dragging and impacts

- A click that picks up a rock emits `Hover`.
- A press becomes a drag only after crossing the existing movement threshold. Emit exactly one
  `Grow` at that transition, not on pointer-down and not continuously.
- Keep `Collide` available during dragging for actual rock, rake, and canvas impacts.
- Alignment guides, magnetic snap, and the `Align` haptic apply only to a carried (picked-up)
  stone. A press-drag must never snap or show guide lines.
- Route all collision pulses through the shared collision emitter. Preserve the 90 ms cooldown so
  a landing followed immediately by physics contact produces one felt impact.
- A rock landing must emit `Collide` whether it lands on sand, another rock, or destabilizes a
  tower.
- Canvas-edge contact should emit once when contact begins, not buzz while held against the edge.
  Reset the edge latch after the rock leaves that edge.

## Rock contour and collision geometry

- Match collision and canvas constraints to the rendered silhouette, including rotation, `Radius`,
  `Sx`, `Sy`, large-rock wobble, pebble wobble, small-rock mixing, and lift scale.
- Keep the page's CPU collision contours synchronized with its rendering shaders.
- Use direction-aware contour support distances. Do not replace them with nominal circular radii.
- Do not add a safety multiplier to rock contact distance. Rendered silhouettes should visually
  touch at collision without a gap or overlap.
- Preserve swept collision detection for dragged rocks so fast movement cannot tunnel through
  another rock.
- Re-apply canvas containment after collision displacement to prevent one-frame escapes.

## Stacking rules

- A smaller rock dropped over a larger support should stack and center on it even when the drop is
  somewhat off-center.
- A larger rock dropped onto a smaller supporting rock or tower must destabilize and scatter that
  stack; do not silently reorder it into a stable size-sorted tower.
- Stack support decisions must use visible overlap and support relationships, not only nominal
  center distance.
- Keep actively dragged rocks grounded so they push neighboring rocks instead of climbing over
  them.

## Event and implementation boundaries

`Assets/ZenGarden/zen-garden.html` owns simulation, contours, event intensity, and
cadence. The native `ZenGardenExperience.xaml.cs` validates messages and maps
semantic events to waveforms. `Services/HapticService.cs` owns WinRT availability,
device routing, waveform fallbacks, and intensity compatibility.

- Keep free-motion and impact events separate. Do not trigger device waveforms directly from
  multiple physics branches when a semantic interaction message can represent the event.

## Validation

Build the WinUI x64 target and run the isolated feedback/bridge checks:

```powershell
dotnet build .\src\SensoryStudio.WinUI\SensoryStudio.WinUI.csproj --configuration Debug -p:Platform=x64 --verbosity minimal
pwsh -NoProfile -File .\scripts\Test-HapticFallback.ps1
pwsh -NoProfile -File .\scripts\Test-WebViewBoundaries.ps1
node .\scripts\test-zen-garden-audio.cjs
```

If a output executable is locked, stop only the process whose path exactly matches the build output
under `src\<project>\bin\x64\Debug\`.

Manually validate on both supported haptic mouse and touchpad hardware:

1. Rake slowly and quickly; cadence must rise with distance and remain filled at high speed.
2. Rake into and past a rock while holding; feedback and marks must stop, then resume beyond it.
3. Leave and re-enter the canvas while holding; no outside feedback or bridging marks may occur.
4. Pick up a rock, begin dragging, align it, hit another rock, and hit each canvas edge; each
   semantic cue must remain distinguishable and impacts must not double-fire.
5. Drop rocks on sand and on other rocks; each landing should produce one `Collide`.
6. Stack a smaller rock off-center on a larger one, then drop a larger rock onto a smaller tower;
   the first must stack and the second must fall apart.
7. Confirm rocks touch exactly at collision and remain fully inside the canvas.
