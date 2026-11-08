# Water Shading

Companion plugin for the Water Mod (Steam Workshop 2200451495). The mod draws the sea as particles, and the game applies neither the sun's shadows
nor the planet's atmosphere to those: shadows from mountains, buildings or ships never fall on the water, and the sea looks pasted over the haze.
This plugin makes the game read the sun shadow map for each pixel of the sea and foam and darken shadowed water, and adds the planet's atmospheric haze to it.

**Status: shadows and atmosphere work on Linux with the LinuxCompat plugin, Render Performance off.**
Not supported: Windows (the plugin does nothing there), and the Render Performance plugin (it replaces the game's particle renderer with its own, so this plugin's changes have no effect while it is on).
It only changes particles of water materials, so there is nothing to see in worlds without the Water Mod (the shader rebuild and the bindings still happen).

## Atmosphere
The game adds the planet's atmospheric haze to terrain with a screen pass that reads the depth of what is behind each pixel. The sea's particles write no depth, so
the haze behind them is covered by the water colour and the sea looks pasted over the atmosphere. The plugin makes the game's own atmosphere function run for each
sea pixel (the ray from the camera to that point) and adds the result to the sea's colour, scaled by the particle's alpha. The function comes from your own install:
the plugin turns the three statements that would delete a particle (a distance clip and two discards) into early returns in the local shader copy. The game's own
code works out the planet's atmosphere constants for the nearest planet; the plugin uploads them and the density lookup texture for the particle pixel stage.
As on terrain, nothing is added closer than 1000 m. Settings: `Atmosphere`, `AtmosphereStrength`, `AtmosphereSteps`, `AtmosphereDebug` (below).

## Which particles count as sea
The plugin tags a particle as sea by its material: the name contains `water` or `foam`, or the name is one of the textures the Water Mod itself offers
(its `/wtexture` command, plus those added by other water mods, such as `JLava`, `JCheese` or `TealWater`), or it is listed in `ExtraMaterials` below.
Changing the sea texture with the mod's command therefore keeps the shadows.

## Use
Only the player's own view changes, so every player who wants the effect needs the plugin. The game log shows lines starting with `[WaterShading]`:

    [WaterShading] shader mirror ready (...)
    [WaterShading] installed; shadow floor 0.3
    [WaterShading] billboard shader compile redirected to the mirror
    [WaterShading] building 63 billboard pixel shaders again ... / billboard pixel shaders rebuilt     (once the first particles are drawn)
    [WaterShading] shadow map bound for the billboard pixel stage
    [WaterShading] particle material treated as sea: <name>
    [WaterShading] atmosphere: planet <id> bound (atmosphere radius ..., intensity ..., lookup found)

A line starting `WARNING`, or saying "the game has changed", "not found" or "failed", means a game update moved something the plugin relies on. The plugin then switches the affected part off:
a problem with the atmosphere leaves the shadows working, a problem with the shadows switches the plugin off, and if that happens after the particle shaders were rebuilt it hands them back to the game's own.
The first start with the plugin recompiles the particle shaders (a short, one-time delay).

## Settings
`<user data>/Storage/WaterShading/WaterShading.cfg` is created on first start. Restart the game after changing it.

| Key | Default | Meaning |
|---|---|---|
| `ShadowFloor` | `0.30` | Brightness of water fully in shadow compared with sunlit water (0 = black, 1 = no effect) |
| `ExtraMaterials` | empty | Comma separated particle material names to treat as sea, for water textures the rules above miss |
| `Atmosphere` | `1` | The sea gets the planet's atmospheric haze like terrain does |
| `AtmosphereStrength` | `1` | Multiplier on the sea's haze (1 = as strong as on terrain) |
| `AtmosphereSteps` | `4` | Integration steps along the view ray, 1 to 8 (the game uses 4 on high quality, 2 on medium) |
| `AtmosphereDebug` | `0` | To find problems: `1` shows only the atmosphere term on the sea (magenta where the planet constants did not arrive, yellow where the atmosphere function returned nothing); `2` paints the sea red under 1000 m and green over |
| `DebugColors` | `0` | `1` paints the sea green where the shadow lookup says lit and red where it says shadowed |

## Platforms
Linux with the LinuxCompat plugin only for now: LinuxCompat compiles shaders with a native compiler that reads the shader folder itself, so the plugin
makes a local mirror of your own shader folder under `<user data>/Storage/WaterShading/Shaders` (links to your original files plus one rewritten copy)
and points only the particle shader compiles at it. It relies on LinuxCompat's class and method names; if they change the plugin logs that and does nothing.
Windows support is not done yet.

## What it touches
- Reads the game's shader folder and writes under `<user data>/Storage/WaterShading/`.
- Patches two renderer methods: one adds the shadow map and its sampler (and the atmosphere constants and density lookup) to the particle pixel stage, one tags the sea's particles as they are batched.
- Writes the nearest planet's atmosphere constants, worked out by the game's own routine, into the game's own atmosphere constant buffer once per particle draw; the game fills that buffer again before each of its own uses.
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
