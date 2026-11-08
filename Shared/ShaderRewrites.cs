using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WaterShading
{
    // The text change made to the player's own billboard shader as it is compiled: a pattern on identifiers plus a few lines of this plugin's own HLSL.
    public static class ShaderRewrites
    {
        public const string BillboardFile = "Billboards.hlsl";
        public const string AtmosphereFile = "AtmosphereCommon.hlsli";

        // Set by the shader mirror before it is built: true when the game's atmosphere include has the shape this plugin expects, and the plugin can feed it.
        public static bool AtmosphereReady;

        // Where the particle shader's own includes end; the atmosphere include goes right after them.
        static readonly Regex FrameInclude = new Regex(@"(#[ \t]*include[ \t]*<Frame\.hlsli>)", RegexOptions.Compiled);

        // The game's atmosphere function was written for a full-screen pass: it ends the pixel with clip/discard when the ray is short or misses the
        // atmosphere. In a particle shader that would delete the particle, so those three statements become early returns of "no atmosphere".
        static readonly Regex AtmosphereClip = new Regex(@"clip[ \t]*\([ \t]*depth[ \t]*-[ \t]*ATMOSPHERE_DISTANCE_MIN[ \t]*\)[ \t]*;", RegexOptions.Compiled);
        static readonly Regex AtmosphereDiscard = new Regex(@"\bdiscard[ \t]*;", RegexOptions.Compiled);

        // The end of the pixel shader's colour work: lit particles multiply their colour by the light the vertex shader worked out.
        // Unlit particles (the sea) skip that, so the shadow step goes right after it.
        static readonly Regex LitMultiply = new Regex(@"(#[ \t]*ifdef[ \t]+LIT_PARTICLE[ \t]*\r?\n[ \t]*resultColor\.xyz[ \t]*\*=[ \t]*vertex\.light[ \t]*;[ \t]*\r?\n[ \t]*#[ \t]*endif)", RegexOptions.Compiled);

        // Decides whether the sea can get atmosphere: wanted, the game's classes found, and the game's atmosphere include has the shape RewriteAtmosphere expects.
        // shadersRoot is the game's shader folder. Logs why not, once.
        public static void CheckAtmosphere(string shadersRoot)
        {
            AtmosphereReady = false;
            if (!Settings.Atmosphere) return;
            string include = System.IO.Path.Combine(shadersRoot, "Transparent", "Atmosphere", AtmosphereFile);
            if (!AtmosphereBinding.Available || !System.IO.File.Exists(include))
            {
                Log.Write("atmosphere: not available (" + (AtmosphereBinding.Available ? "include not found" : "game classes not found") + "); the sea gets no atmosphere");
                return;
            }
            string original = System.IO.File.ReadAllText(include);
            AtmosphereReady = RewriteAtmosphere(original) != original;
            if (!AtmosphereReady) Log.Write("atmosphere: the game's atmosphere include has changed shape; the sea gets no atmosphere");
        }

        // Files the shader mirror carries as rewritten copies: the particle shader, and (when usable) the atmosphere include it pulls in.
        public static bool Wants(string fileName)
        {
            return IsBillboardShader(fileName) || (AtmosphereReady && NameIs(fileName, AtmosphereFile) && fileName.IndexOf("Atmosphere", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // The one shader whose compiles are pointed at the mirror.
        public static bool IsBillboardShader(string fileName)
        {
            return NameIs(fileName, BillboardFile) && fileName.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool NameIs(string path, string name)
        {
            if (path == null) return false;
            return path.Substring(path.LastIndexOfAny(new[] { '/', '\\' }) + 1).Equals(name, StringComparison.OrdinalIgnoreCase);
        }

        // The game's atmosphere include with its clip/discard statements turned into early returns; the text itself if it does not have that shape.
        public static string RewriteAtmosphere(string text)
        {
            if (!AtmosphereClip.IsMatch(text) || !AtmosphereDiscard.IsMatch(text)) return text;
            string none = "return float4(0, 0, 0, 1);";
            string result = AtmosphereClip.Replace(text, "if (depth < ATMOSPHERE_DISTANCE_MIN) " + none);
            return AtmosphereDiscard.Replace(result, none);
        }

        public static string Rewrite(string fileName, string text, bool withAtmosphere = true)
        {
            return NameIs(fileName, AtmosphereFile) ? RewriteAtmosphere(text) : Rewrite(text, withAtmosphere);
        }

        // Returns the rewritten text; the text itself when the pattern is missing (the caller logs that and uses the original).
        public static string Rewrite(string text, bool withAtmosphere = true)
        {
            if (!LitMultiply.IsMatch(text)) return text;

            string floor = F(Settings.ShadowFloor);
            string marker = F(WaterMarking.Marker - 1f);
            bool atmosphere = withAtmosphere && AtmosphereReady && FrameInclude.IsMatch(text);
            string block =
                "$1\r\n" +
                "#ifndef LIT_PARTICLE\r\n" +
                "    // Water Shading: particles the plugin tagged as sea read the sun shadow map per pixel.\r\n" +
                "    if (BillboardBuffer[vertex.index].AlphaSaturation > " + marker + ")\r\n" +
                "    {\r\n" +
                "        float wsShadow = CalculateShadowFast(vertex.wposition);\r\n" +
                (Settings.DebugColors
                    ? "        resultColor = float4(lerp(float3(1, 0, 0), float3(0, 1, 0), wsShadow), 1.0f);\r\n"
                    : "        resultColor.xyz *= lerp(" + floor + ", 1.0f, wsShadow);\r\n") +
                (atmosphere
                    ? "        // The planet's haze between the camera and this point, added as the game adds it to terrain; premultiplied, so scaled by the particle's alpha.\r\n" +
                      "        // Intensity comes from the atmosphere constants; it is 0 where there is no atmosphere (or nothing is bound), and then the call is skipped.\r\n" +
                      (Settings.AtmosphereDebug == 2
                          ? "        resultColor = float4(-linearDepth > 1000.0f ? float3(0, 1, 0) : float3(1, 0, 0), 1.0f);\r\n"
                          : "        if (Intensity > 0.0001f)\r\n" +
                            "        {\r\n" +
                            "            float4 wsAtmosphere = ComputeAtmosphere(-vertex.wposition, vertex.wposition, frame_.Light.directionalLightVec, -linearDepth, 0.5f, " + Settings.AtmosphereSteps.ToString(CultureInfo.InvariantCulture) + ");\r\n" +
                            (Settings.AtmosphereDebug == 1
                                ? "            resultColor = dot(wsAtmosphere.xyz, wsAtmosphere.xyz) > 0 ? float4(wsAtmosphere.xyz * " + F(Settings.AtmosphereStrength) + ", 1.0f) : float4(1, 1, 0, 1);\r\n"
                                : "            resultColor.xyz += wsAtmosphere.xyz * " + F(Settings.AtmosphereStrength) + " * resultColor.w;\r\n") +
                            "        }\r\n" +
                            (Settings.AtmosphereDebug == 1 ? "        else resultColor = float4(1, 0, 1, 1);\r\n" : ""))
                    : "") +
                "    }\r\n" +
                "#endif";

            string result = LitMultiply.Replace(text, block, 1);
            if (atmosphere) result = FrameInclude.Replace(result, "$1\r\n#include <Transparent/Atmosphere/AtmosphereCommon.hlsli>", 1);
            return result;
        }

        static string F(float v) { return v.ToString("0.0###", CultureInfo.InvariantCulture) + "f"; }
    }
}
