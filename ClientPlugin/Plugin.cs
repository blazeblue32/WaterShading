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

        // The compatibility layer's assembly may load after this plugin, so look for it again for a short while.
        public void Update()
        {
            if (!done && attempts < 600) Try();
        }

        void Try()
        {
            attempts++;
            Type compiler = ShaderOverlay.FindCompiler();
            if (compiler == null)
            {
                if (attempts == 1) Log.Write("no Linux compatibility compiler found yet (this plugin supports the Linux compatibility layer only for now); will keep looking briefly");
                return;
            }
            string shaders;
            try { shaders = Log.ShadersPath(); }
            catch (Exception) { return; } // paths not ready yet; try again on a later frame
            done = true;
            // The shader change and the binding go together: without the binding the shader would read an empty shadow map,
            // so the binding is checked first and the shader is only redirected when it is in place.
            if (PixelBindings.Apply(harmony) && ShaderOverlay.Apply(harmony, compiler, shaders))
            {
                PixelBindings.Enabled = true;
                Log.Write("installed; shadow floor " + Settings.ShadowFloor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
                Log.Write("not active (see the lines above)");
        }

        public void Dispose()
        {
            // Do not UnpatchAll here: it may break other plugins.
        }
    }
}
