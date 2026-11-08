using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace WaterShading
{
    // On the Linux compatibility layer shaders are compiled by a native compiler that reads the shader folder itself.
    // This builds a local mirror of the player's own shader folder in which every file is a link to the original except the one
    // that carries the change (a rewritten copy), and points that shader's compiles at the mirror.
    // Nothing from the game is stored in the plugin; the mirror is generated on the player's machine from their own installation.
    public static class ShaderOverlay
    {
        const string CompilerTypeName = "ClientPlugin.Compatibility.Rendering.D3DCompilerLinux";

        static string gameRoot, mirrorRoot;

        public static Type FindCompiler()
        {
            return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(CompilerTypeName, false)).FirstOrDefault(t => t != null);
        }

        public static bool Apply(Harmony harmony, Type compiler, string shadersPath)
        {
            gameRoot = Normalize(shadersPath);
            mirrorRoot = Build(gameRoot);
            if (mirrorRoot == null) return false;

            // Both entry points take the shader file path and the shader folder; they are told apart by their first parameter being a string.
            var preprocess = compiler.GetMethods(AccessTools.all).FirstOrDefault(m => m.Name == "Preprocess" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string));
            var compile = compiler.GetMethods(AccessTools.all).FirstOrDefault(m => m.Name == "Compile" && m.GetParameters().Length == 7 && m.GetParameters()[0].ParameterType == typeof(string));
            if (preprocess == null || compile == null) { Log.Write("compatibility layer's compiler entry points not found; no overlay"); return false; }

            harmony.Patch(preprocess, prefix: new HarmonyMethod(typeof(ShaderOverlay), nameof(PreprocessPrefix)));
            harmony.Patch(compile, prefix: new HarmonyMethod(typeof(ShaderOverlay), nameof(CompilePrefix)));
            Log.Write("billboard shader will be compiled from " + mirrorRoot);
            return true;
        }

        // Arguments by position: 0 = shader file, 2 = shader folder.
        public static void PreprocessPrefix(ref string __0, ref string __2) { Redirect(ref __0, ref __2); }

        // Arguments by position: 0 = shader file, 5 = shader folder.
        public static void CompilePrefix(ref string __0, ref string __5) { Redirect(ref __0, ref __5); }

        static bool reported, reportedMiss;

        static void Redirect(ref string file, ref string root)
        {
            try
            {
                if (file == null || root == null || mirrorRoot == null) return;
                string f = Normalize(file);
                if (!ShaderRewrites.Wants(f) || !f.StartsWith(gameRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    if (f.IndexOf("illboard", StringComparison.OrdinalIgnoreCase) >= 0 && !reportedMiss) { reportedMiss = true; Log.Write("billboard shader seen but not redirected: " + file + " (root " + root + ")"); }
                    return;
                }
                file = Path.Combine(mirrorRoot, f.Substring(gameRoot.Length + 1));
                if (Normalize(root).Equals(gameRoot, StringComparison.OrdinalIgnoreCase)) root = mirrorRoot;
                if (!reported) { reported = true; Log.Write("billboard shader compile redirected to the mirror"); }
            }
            catch (Exception e) { Log.Write("overlay redirect failed: " + e.Message); }
        }

        static string Normalize(string path)
        {
            return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        }

        // Mirrors the shader folder: real directories, file links, and a rewritten copy of the file that carries the change. Returns the mirror's root.
        static string Build(string source)
        {
            try
            {
                string mirror = Path.Combine(Settings.Folder, "Shaders");
                int links = 0, copies = 0;
                bool rewritten = false;
                foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Prepend(source))
                    Directory.CreateDirectory(Path.Combine(mirror, Relative(source, dir)));

                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    string rel = Relative(source, file);
                    string target = Path.Combine(mirror, rel);
                    if (ShaderRewrites.Wants(rel))
                    {
                        string text = File.ReadAllText(file);
                        string patched = ShaderRewrites.Rewrite(text);
                        if (patched == text) { Log.Write("WARNING: the pattern to rewrite in " + rel + " was not found; the game has changed. Water is left as the game draws it."); return null; }
                        DeleteIfExists(target);
                        File.WriteAllText(target, patched, new UTF8Encoding(false));
                        copies++;
                        rewritten = true;
                        continue;
                    }
                    if (IsLinkTo(target, file)) continue;
                    DeleteIfExists(target);
                    if (TryLink(target, file)) links++;
                    else { File.Copy(file, target, true); copies++; }
                }
                if (!rewritten) { Log.Write("WARNING: the billboard shader was not found in " + source); return null; }
                Log.Write("shader mirror ready (" + links + " new links, " + copies + " copies)");
                return Normalize(mirror);
            }
            catch (Exception e)
            {
                Log.Write("could not build the shader mirror: " + e);
                return null;
            }
        }

        // The newer file APIs (symbolic links) do not exist on .NET Framework, so they are reached by reflection and the code falls back to copying.
        static readonly MethodInfo createLink = typeof(File).GetMethod("CreateSymbolicLink", new[] { typeof(string), typeof(string) });
        static readonly PropertyInfo linkTarget = typeof(FileSystemInfo).GetProperty("LinkTarget");

        static bool TryLink(string link, string target)
        {
            if (createLink == null) return false;
            try { createLink.Invoke(null, new object[] { link, target }); return true; }
            catch { return false; }
        }

        static bool IsLinkTo(string path, string target)
        {
            if (linkTarget == null) return false;
            try { var t = linkTarget.GetValue(new FileInfo(path), null) as string; return t != null && Path.GetFullPath(t) == Path.GetFullPath(target); }
            catch { return false; }
        }

        // Removes the file or link if there is one (a link whose target is gone does not count as existing, so delete is simply attempted).
        static void DeleteIfExists(string path)
        {
            try { File.GetAttributes(path); File.Delete(path); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        static string Relative(string root, string path)
        {
            string full = Path.GetFullPath(path), start = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            return full.Length <= start.Length ? "" : full.Substring(start.Length + 1);
        }
    }
}
