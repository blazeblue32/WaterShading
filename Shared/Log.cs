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
            return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null)
                   ?? HarmonyLib.AccessTools.TypeByName(fullName);
        }

        // The folder the game itself compiles shaders from (its compiler class exposes it), falling back to the content folder's shader directory.
        public static string ShadersPath()
        {
            Type compiler = FindType("VRageRender.MyShaderCompiler");
            var prop = compiler == null ? null : HarmonyLib.AccessTools.Property(compiler, "ShadersPath");
            string path = prop?.GetValue(null) as string;
            return !string.IsNullOrEmpty(path) ? path : System.IO.Path.Combine(VRage.FileSystem.MyFileSystem.ShadersBasePath, "Shaders");
        }

        public static string StorageRoot()
        {
            try { return System.IO.Path.Combine(VRage.FileSystem.MyFileSystem.UserDataPath, "Storage"); }
            catch { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceEngineers", "Storage"); }
        }
    }
}
