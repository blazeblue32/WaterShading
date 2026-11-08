# Water Shading

Companion plugin for the Water Mod (Steam Workshop 2200451495). The mod draws the sea as particles, and the game does not apply
the sun's shadows to those, so shadows from mountains, buildings or ships never fall on the water. This plugin makes the game read the
sun shadow map for each pixel of the sea and foam and darken shadowed water.

**Status: shadows work on Linux with the LinuxCompat plugin, Render Performance off.** Atmosphere (water showing through thick air) is planned as a second step.
Not yet supported: the Render Performance plugin (it replaces the game's particle renderer), and Windows.

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

A line starting `WARNING` or saying "the game has changed" means a game update moved something the plugin relies on; it then leaves the water as the game draws it.
The first start with the plugin recompiles the particle shaders (a short, one-time delay).

## Settings
`<user data>/Storage/WaterShading/WaterShading.cfg` is created on first start. Restart the game after changing it.

| Key | Default | Meaning |
|---|---|---|
| `ShadowFloor` | `0.30` | Brightness of water fully in shadow compared with sunlit water (0 = black, 1 = no effect) |
| `ExtraMaterials` | empty | Comma separated particle material names to treat as sea, for water textures the rules above miss |
| `DebugColors` | `0` | `1` paints the sea green where the shadow lookup says lit and red where it says shadowed |
| `Diagnostics` | `0` | `1` writes extra detail about the particle renderer to the game log |

## Platforms
Linux with the LinuxCompat plugin only for now: LinuxCompat compiles shaders with a native compiler that reads the shader folder itself, so the plugin
makes a local mirror of your own shader folder under `<user data>/Storage/WaterShading/Shaders` (links to your original files plus one rewritten copy)
and points only the particle shader compiles at it. It relies on LinuxCompat's class and method names; if they change the plugin logs that and does nothing.
Windows support is not done yet.

## What it touches
- Reads the game's shader folder and writes under `<user data>/Storage/WaterShading/`.
- Patches two renderer methods: one adds two resource bindings for the particle pixel stage, one tags the sea's particles as they are batched.
- Rebuilds the game's particle pixel shaders once, on the first frame that draws particles.

## Build
You need the .NET 10 SDK and a Space Engineers install. `Directory.Build.props` finds the game from Steam; to override, create `Directory.Build.props.user`
(see the comments in `Directory.Build.props`). Then:

    dotnet build ClientPlugin/ClientPlugin.csproj

To load it in Pulsar, add the repository folder as a local plugin source (the descriptor is `WaterShading.xml`).
