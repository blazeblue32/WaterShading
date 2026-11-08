# Water Shading

Companion plugin for the Water Mod (Steam Workshop 2200451495). The mod draws the sea as particles, and the game applies neither the sun's shadows
nor the planet's atmosphere to those: shadows from mountains, buildings or ships never fall on the water, and the sea looks pasted over the haze.
This plugin makes the game read the sun shadow map for each pixel of the sea and foam and darken shadowed water, and adds the planet's atmospheric haze to it.

**Status: shadows and atmosphere work on Linux with the LinuxCompat plugin (tested, with and without Render Performance). Stock Windows, and the game under Wine, use a second way of changing the shader that is written and checked against stand-ins but has not been run on Windows.**
Works with the Render Performance plugin too (see below); tested in game on Linux.
It only changes particles of water materials, so there is nothing to see in worlds without the Water Mod (the shader rebuild and the bindings still happen).

## Atmosphere
The game adds the planet's atmospheric haze to terrain with a screen pass that reads the depth of what is behind each pixel. The sea's particles write no depth, so
the haze behind them is covered by the water colour and the sea looks pasted over the atmosphere. The plugin makes the game's own atmosphere function run for each
sea pixel (the ray from the camera to that point) and adds the result to the sea's colour, scaled by the particle's alpha. The function comes from your own install:
the plugin turns the three statements that would delete a particle (a distance clip and two discards) into early returns in the local shader copy. The game's own
code works out the planet's atmosphere constants for the nearest planet; the plugin uploads them and the density lookup texture for the particle pixel stage.
As on terrain, nothing is added closer than 1000 m. Settings: `Atmosphere`, `AtmosphereStrength`, `AtmosphereSteps`, `AtmosphereDebug` (below).

## Render Performance
The Render Performance plugin replaces the game's particle renderer with its own, which draws with its own shader. When it is installed this plugin also works there,
by reaching into it by name (nothing of it is copied or edited on disk): the sea's particles are marked as they enter its renderer (in the particle's alpha-cutout value, which
it does not use for the sea), its particle shader's text is swapped for a changed copy at the moment it is compiled (the same change as for the game's shader, with the game's atmosphere code,
made from your own install, pasted in and its constants moved to constant buffer slot 6 because Render Performance uses slot 1 itself), its resource binding gets the shadow map and the
atmosphere constants, and once it is asked to rebuild its shaders. The atmosphere code is pasted into the text rather than included by name because on Linux the compiler reads include files itself.
It is built so this plugin cannot break Render Performance: every piece it relies on is checked when it is installed; if something is missing or looks different, or the changed
shader does not compile, Render Performance is given its own shader back, the log gets a `WARNING` line and a notification appears on the HUD
("Water Shading: ... the sea gets no shadows or haze while it is on"). If Render Performance changes in a way that breaks this, the sea simply looks as before. Turn it off with `RenderPerf=0`.

## Known limits
- The sea is drawn as two layers (a clear top and a darker one below), and a shadow can show on both, so it can look doubled.
- Nothing is added to the sea closer than 1000 m, as on terrain.
- The stock Windows path has not been run on Windows.

## Which particles count as sea
The plugin tags a particle as sea by its material: the name contains `water` or `foam`, or the name is one of the textures the Water Mod itself offers
(its `/wtexture` command, plus those added by other water mods, such as `JLava`, `JCheese` or `TealWater`), or it is listed in `ExtraMaterials` below.
Changing the sea texture with the mod's command therefore keeps the shadows.

## Use
Only the player's own view changes, so every player who wants the effect needs the plugin. The game log shows lines starting with `[WaterShading]`:

    [WaterShading] shader mirror ready (...)                                        (Linux only)
    [WaterShading] installed (Linux compatibility layer); shadow floor 0.3          (or "installed (game's own compiler)" on stock Windows)
    [WaterShading] billboard shader compile redirected to the mirror               (or "billboard shader preprocessed by the plugin" on stock Windows)
    [WaterShading] building 63 billboard pixel shaders again ... / billboard pixel shaders rebuilt     (once the first particles are drawn)
    [WaterShading] shadow map bound for the billboard pixel stage
    [WaterShading] particle material treated as sea: <name>
    [WaterShading] atmosphere: planet <id> bound (atmosphere radius ..., intensity ..., lookup found)
    [WaterShading] Render Performance found; ...                                  (only when it is installed)
    [WaterShading] Render Performance's particle shader compiled with the sea changes

A line starting `WARNING`, or saying "the game has changed", "not found" or "failed", means a game update moved something the plugin relies on. The plugin then switches the affected part off:
a problem with the atmosphere leaves the shadows working, a problem with the shadows switches the plugin off, and if that happens after the particle shaders were rebuilt it hands them back to the game's own.
The particle shaders are recompiled when the first particles are drawn after joining a world: expect a pause of about two seconds then.
For the first launch or two after installing or updating the plugin (or after a game update), the first ten to fifteen seconds in a world can also stutter: the graphics layer builds its
own copies of the changed shaders as they first appear on screen, and keeps them for later launches. It settles by itself over the next launch or two.

## Settings
`<user data>/Storage/WaterShading/WaterShading.cfg` is created on first start. Restart the game after changing it.

| Key | Default | Meaning |
|---|---|---|
| `ShadowFloor` | `0.30` | Brightness of water fully in shadow compared with sunlit water (0 = black, 1 = no effect) |
| `RenderPerf` | `1` | Also work with the Render Performance plugin's particle renderer when it is installed |
| `ExtraMaterials` | empty | Comma separated particle material names to treat as sea, for water textures the rules above miss |
| `Atmosphere` | `1` | The sea gets the planet's atmospheric haze like terrain does |
| `AtmosphereStrength` | `1` | Multiplier on the sea's haze (1 = as strong as on terrain) |
| `AtmosphereSteps` | `4` | Integration steps along the view ray, 1 to 8 (the game uses 4 on high quality, 2 on medium) |
| `AtmosphereDebug` | `0` | To find problems: `1` shows only the atmosphere term on the sea (magenta where the planet constants did not arrive, yellow where the atmosphere function returned nothing); `2` paints the sea red under 1000 m and green over |
| `DebugColors` | `0` | `1` paints the sea green where the shadow lookup says lit and red where it says shadowed |

## Platforms
The shader change has to get into the game's shader compile, and that differs by setup:

| Setup | How |
|---|---|
| Linux with the LinuxCompat plugin (tested) | LinuxCompat compiles shaders with a native compiler that reads the shader folder itself, so the plugin makes a local mirror of your own shader folder under `<user data>/Storage/WaterShading/Shaders` (links to your original files plus two rewritten copies) and points only the particle shader compiles at it. It relies on LinuxCompat's class and method names; if they change the plugin logs that and does nothing. |
| Stock Windows, or the game under Wine (not yet run on Windows) | The game's own compiler reads the files. Its preprocessing step reads the top-level particle shader straight from disk, so for that one shader the plugin runs the step itself on a rewritten copy of the text, using the game's own preprocessor and include handler. The atmosphere include, which the game's include handler reads, is rewritten as it is read, but only while the particle shader is being preprocessed, so the game's own atmosphere shaders keep reading the original. If the rewrite did not take, the particle shader is built without atmosphere instead of risking missing sea pixels. Nothing is written to disk. |

Which one is used is decided at start: a Linux compatibility compiler present means the first; Windows (or Wine) means the second; Linux without the compatibility compiler means the plugin does nothing.

## What it touches
- Reads the game's shader folder. On the Linux compatibility layer it writes the shader mirror under `<user data>/Storage/WaterShading/`; the settings file is written there on every setup.
- On stock Windows it also patches the game's shader preprocessing and its include reader (see Platforms).
- Patches two renderer methods: one adds the shadow map and its sampler (and the atmosphere constants and density lookup) to the particle pixel stage, one tags the sea's particles as they are batched.
- Writes the nearest planet's atmosphere constants, worked out by the game's own routine, into a constant buffer of its own (made with the game's own buffer factory) once per particle draw. If that buffer cannot be made, the game's atmosphere constant buffer is used instead.
- When Render Performance is installed it also patches four of its methods and two SharpDX calls, the preprocessor and the native compile call (the last two only for Render Performance's own shader compile); see above.
- Reads the Water Mod's list of sea textures from its loaded script (read only, optional).
- No network access, no processes started, no game files changed.
- Rebuilds the game's particle pixel shaders once, on the first frame that draws particles.

## Build
You need the .NET 10 SDK and a Space Engineers install. `Directory.Build.props` finds the game from Steam; to override, create `Directory.Build.props.user`
(see the comments in `Directory.Build.props`). Then:

    dotnet build ClientPlugin/ClientPlugin.csproj

To load it in Pulsar, add the repository folder as a local plugin source (the descriptor is `WaterShading.xml`).

## Cost
Per frame the plugin adds on the order of 0.1 ms of CPU. On the GPU the shadow lookup is one sample per sea pixel, and the atmosphere is the game's own function (4 steps by default) per sea pixel,
only beyond 1000 m, for each of the sea's layers. Far sea in view costs the most; `Atmosphere=0` or `AtmosphereSteps=2` lowers it.
The first particle frame of a session pauses for about 2 seconds while the particle shaders are rebuilt.
