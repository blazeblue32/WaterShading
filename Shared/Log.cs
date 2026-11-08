using System;
using System.Linq;

namespace WaterShading
{
    public static class Log
    {
        public static void Write(string message)
        {
            string line = "[WaterShading] " + message;
            try
            {
                if (VRage.Utils.MyLog.Default != null) VRage.Utils.MyLog.Default.WriteLineAndConsole(line);
                else Console.WriteLine(line);
            }
            catch { Console.WriteLine(line); }
        }

        public static Type FindType(string fullName)
        {
            // Not AccessTools.TypeByName: it builds an array of every type in the process on each miss, and the names asked for here are full names.
            return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);
        }

        // The folder the game itself compiles shaders from (its compiler class exposes it), falling back to the content folder's shader directory.
        public static string ShadersPath()
        {
            Type compiler = FindType("VRageRender.MyShaderCompiler");
            var prop = compiler == null ? null : HarmonyLib.AccessTools.Property(compiler, "ShadersPath");
            string path = prop?.GetValue(null) as string;
            return !string.IsNullOrEmpty(path) ? path : System.IO.Path.Combine(VRage.FileSystem.MyFileSystem.ShadersBasePath, "Shaders");
        }

        // <user data>/Storage, or null when the game's user data folder is not known (then nothing is written: the plugin runs on its defaults and the shader mirror is not made).
        public static string StorageRoot()
        {
            try { return System.IO.Path.Combine(VRage.FileSystem.MyFileSystem.UserDataPath, "Storage"); }
            catch (Exception e) { Write("the game's user data folder is not available (" + e.Message + "); settings and the shader mirror are not used"); return null; }
        }
    }
}
