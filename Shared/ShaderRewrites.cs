using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WaterShading
{
    // The text change made to the player's own billboard shader as it is compiled: a pattern on identifiers plus a few lines of this plugin's own HLSL.
    public static class ShaderRewrites
    {
        public const string BillboardFile = "Billboards.hlsl";

        // The end of the pixel shader's colour work: lit particles multiply their colour by the light the vertex shader worked out.
        // Unlit particles (the sea) skip that, so the shadow step goes right after it.
        static readonly Regex LitMultiply = new Regex(@"(#[ \t]*ifdef[ \t]+LIT_PARTICLE[ \t]*\r?\n[ \t]*resultColor\.xyz[ \t]*\*=[ \t]*vertex\.light[ \t]*;[ \t]*\r?\n[ \t]*#[ \t]*endif)", RegexOptions.Compiled);

        public static bool Wants(string fileName)
        {
            if (fileName == null) return false;
            string file = fileName.Substring(fileName.LastIndexOfAny(new[] { '/', '\\' }) + 1);
            return file.Equals(BillboardFile, StringComparison.OrdinalIgnoreCase) && fileName.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Returns the rewritten text; the text itself when the pattern is missing (the caller logs that and uses the original).
        public static string Rewrite(string text)
        {
            if (!LitMultiply.IsMatch(text)) return text;

            string floor = F(Settings.ShadowFloor);
            string marker = F(WaterMarking.Marker - 1f);
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
                "    }\r\n" +
                "#endif";

            return LitMultiply.Replace(text, block, 1);
        }

        static string F(float v) { return v.ToString("0.0###", CultureInfo.InvariantCulture) + "f"; }
    }
}
