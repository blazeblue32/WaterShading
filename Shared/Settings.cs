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

        // 1 = log extra detail about what the billboard renderer is doing (for finding problems).
        public static bool Diagnostics = false;

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
                        "# Diagnostics: 1 logs extra detail about the particle renderer (for finding problems), 0 normal.\n" +
                        "Diagnostics=0\n");
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
                    else if (key.Equals("ExtraMaterials", StringComparison.OrdinalIgnoreCase))
                        ExtraMaterials = value;
                    else if (key.Equals("Diagnostics", StringComparison.OrdinalIgnoreCase))
                        Diagnostics = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    else if (key.Equals("DebugColors", StringComparison.OrdinalIgnoreCase))
                        DebugColors = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception e) { Log.Write("could not read the settings file, using defaults: " + e.Message); }
        }
    }
}
