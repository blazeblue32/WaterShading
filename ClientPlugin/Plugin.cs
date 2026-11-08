using System;
using HarmonyLib;
using VRage.Plugins;
using WaterShading;
// Define assembly version when compiled by Pulsar
#if !LOCAL_BUILD
using System.Reflection;

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

#endif

namespace ClientPlugin
{
    // Pulsar entry point. Loads once at game start and applies the shader change and the pixel stage binding in Shared/.
    public class Plugin : IPlugin
    {
        readonly Harmony harmony = new Harmony("WaterShading");
        int attempts = 0;
        bool done;

        public void Init(object gameInstance)
        {
            Settings.Load();
            Try();
        }

        // On Linux the compatibility layer's assembly may load after this plugin, so look for it again for a short while.
        public void Update()
        {
            if (done || attempts >= 600) return;
            Try();
            if (!done && attempts == 600) Log.Write("no Linux compatibility compiler found; the plugin does nothing on this setup");
        }

        void Try()
        {
            attempts++;
            // Two ways in, depending on who compiles the shaders. With the Linux compatibility layer a native compiler reads the shader folder itself (ShaderOverlay);
            // everywhere else (stock Windows, or the game under Wine) the game's own compiler does and is hooked directly (ShaderHook).
            Type compiler = ShaderOverlay.FindCompiler();
            bool unix = Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX;
            if (compiler == null && unix)
            {
                if (attempts == 1) Log.Write("no Linux compatibility compiler found yet; will keep looking briefly");
                return;
            }
            string shaders;
            try { shaders = Log.ShadersPath(); }
            catch (Exception) { return; } // paths not ready yet; try again on a later frame
            done = true;
            // The shader change and the binding go together: without the binding the shader would read an empty shadow map,
            // so the binding is checked first and the shader is only changed when it is in place.
            try
            {
                bool shaderPart = false;
                if (PixelBindings.Apply(harmony))
                    shaderPart = compiler != null ? ShaderOverlay.Apply(harmony, compiler, shaders) : ShaderHook.Apply(harmony, shaders);
                if (shaderPart)
                {
                    PixelBindings.Enabled = true;
                    Log.Write("installed (" + (compiler != null ? "Linux compatibility layer" : "game's own compiler") + "); shadow floor " + Settings.ShadowFloor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }
            }
            catch (Exception e) { Log.Write("failed to install: " + e); }
            Log.Write("not active (see the lines above)");
        }

        public void Dispose()
        {
            // Do not UnpatchAll here: it may break other plugins.
        }
    }
}
