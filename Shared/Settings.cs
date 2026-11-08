using System;
using System.Globalization;
using System.IO;

namespace WaterShading
{
    // A tiny key=value file next to the plugin's shader mirror: <user data>/Storage/WaterShading/WaterShading.cfg.
    // It is read when the game starts; change it and restart. The file is created with the defaults when missing.
    public static class Settings
    {
        // Brightness of water that sits fully in a shadow, as a fraction of its sunlit brightness (0 = black, 1 = no shadow effect).
        public static float ShadowFloor = 0.30f;

        // Extra particle material names (comma separated) to treat as sea. Names containing "water" or "foam" are always included.
        public static string ExtraMaterials = "";

        // 1 = paint water green where the shadow lookup says lit and red where it says shadowed, to check the lookup works.
        public static bool DebugColors = false;

        // The sea gets the planet's atmospheric in-scatter (haze) like terrain does.
        public static bool Atmosphere = true;

        // Multiplier on the sea's atmosphere (1 = as strong as on terrain).
        public static float AtmosphereStrength = 1f;

        // Integration steps along the view ray (the game's high quality setting uses 4, medium 2).
        public static int AtmosphereSteps = 4;

        // To find problems: 1 = show only the atmosphere term on the sea (magenta where the planet's constants did not arrive, yellow where the atmosphere function returned nothing);
        // 2 = show the distance the shader sees (red under 1000 m, green over).
        public static int AtmosphereDebug = 0;

        public static string Folder { get { return Path.Combine(Log.StorageRoot(), "WaterShading"); } }

        public static void Load()
        {
            try
            {
                string path = Path.Combine(Folder, "WaterShading.cfg");
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(path,
                        "# Water Shading settings. Restart the game after changing.\n" +
                        "# ShadowFloor: brightness of water fully in shadow compared with sunlit water (0 black .. 1 no shadow effect).\n" +
                        "ShadowFloor=" + ShadowFloor.ToString(CultureInfo.InvariantCulture) + "\n" +
                        "# ExtraMaterials: comma separated particle material names to treat as sea (names containing water or foam are always included).\n" +
                        "ExtraMaterials=\n" +
                        "# DebugColors: 1 paints water green where the shadow lookup says lit and red where it says shadowed (to check it works), 0 normal.\n" +
                        "DebugColors=0\n" +
                        "# Atmosphere: 1 = the sea gets the planet's atmospheric haze like terrain does, 0 = off.\n" +
                        "Atmosphere=1\n" +
                        "# AtmosphereStrength: multiplier on the sea's haze (1 = as strong as on terrain).\n" +
                        "AtmosphereStrength=1\n" +
                        "# AtmosphereSteps: integration steps along the view ray, 1 to 8 (the game uses 4 on high quality, 2 on medium).\n" +
                        "AtmosphereSteps=4\n" +
                        "# AtmosphereDebug (to find problems): 1 shows only the atmosphere term on the sea, magenta where the planet constants did not arrive and yellow where the atmosphere function returned nothing; 2 paints the sea red under 1000 m and green over (the distance the shader sees); 0 normal.\n" +
                        "AtmosphereDebug=0\n");
                    return;
                }
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    float f;
                    if (key.Equals("ShadowFloor", StringComparison.OrdinalIgnoreCase) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                        ShadowFloor = Math.Max(0f, Math.Min(1f, f));
                    else if (key.Equals("Atmosphere", StringComparison.OrdinalIgnoreCase))
                        Atmosphere = !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
                    else if (key.Equals("AtmosphereStrength", StringComparison.OrdinalIgnoreCase) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                        AtmosphereStrength = Math.Max(0f, Math.Min(10f, f));
                    else if (key.Equals("AtmosphereSteps", StringComparison.OrdinalIgnoreCase) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                        AtmosphereSteps = Math.Max(1, Math.Min(8, (int)f));
                    else if (key.Equals("AtmosphereDebug", StringComparison.OrdinalIgnoreCase) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                        AtmosphereDebug = Math.Max(0, Math.Min(2, (int)f));
                    else if (key.Equals("ExtraMaterials", StringComparison.OrdinalIgnoreCase))
                        ExtraMaterials = value;
                    else if (key.Equals("DebugColors", StringComparison.OrdinalIgnoreCase))
                        DebugColors = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception e) { Log.Write("could not read the settings file, using defaults: " + e.Message); }
        }
    }
}
