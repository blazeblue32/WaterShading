using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace WaterShading
{
    // Setups where the game compiles its shaders itself (stock Windows, or the game under Wine): there is no mirror to build, the game's own compiler reads the files.
    // Its preprocessing step reads the top-level shader file straight from disk, so for the particle shader the plugin does that step itself on a rewritten copy of the
    // text (calling the same preprocessor with the game's own include handler); the atmosphere include, which the game's include handler reads, is rewritten as it is read,
    // but only while that particle shader is being preprocessed, so the game's own atmosphere shaders keep reading the original.
    public static class ShaderHook
    {
        // Set to stop doing this (used to hand the particle shaders back to the game if the plugin cannot finish its job).
        public static volatile bool Disabled;

        [ThreadStatic] static bool preprocessingBillboards;
        static string gameRoot;
        static Type includeType;
        static MethodInfo sharpPreprocess;
        static bool reported, warned, warnedAtmosphere;
        static readonly System.Text.RegularExpressions.Regex DiscardWord = new System.Text.RegularExpressions.Regex(@"\bdiscard\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        public static bool Apply(Harmony harmony, string shadersPath)
        {
            gameRoot = Path.GetFullPath(shadersPath).Replace('\\', '/').TrimEnd('/');
            ShaderRewrites.CheckAtmosphere(gameRoot);

            Type compiler = Log.FindType("VRageRender.MyShaderCompiler");
            MethodInfo preprocess = compiler == null ? null : AccessTools.Method(compiler, "PreprocessShader");
            includeType = Log.FindType("VRageRender.MyShaderCompiler+MyIncludeProcessor");
            MethodInfo open = includeType == null ? null : AccessTools.Method(includeType, "Open");
            Type bytecode = Log.FindType("SharpDX.D3DCompiler.ShaderBytecode");
            // Preprocess(string source, ShaderMacro[] defines, Include include, out string errors, string sourceFileName)
            sharpPreprocess = bytecode == null ? null : bytecode.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Preprocess" && m.GetParameters().Length == 5 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType.IsByRef);
            if (preprocess == null || open == null || sharpPreprocess == null)
            {
                Log.Write("the game's shader preprocessing was not found (" + (preprocess == null ? "PreprocessShader " : "") + (open == null ? "Open " : "") + (sharpPreprocess == null ? "Preprocess" : "") + "); the game has changed, shadows cannot be drawn");
                return false;
            }
            harmony.Patch(preprocess, prefix: new HarmonyMethod(typeof(ShaderHook), nameof(PreprocessPrefix)));
            harmony.Patch(open, postfix: new HarmonyMethod(typeof(ShaderHook), nameof(OpenPostfix)));
            Log.Write("billboard shader will be preprocessed by the plugin from " + gameRoot);
            return true;
        }

        // Arguments by position: 0 = shader file, 1 = macros, 2 = the error text (an out parameter of the game's method); the result is the preprocessed text.
        // Returning false skips the game's own preprocessing.
        public static bool PreprocessPrefix(string __0, object __1, ref string __result, ref string __2)
        {
            if (Disabled || __0 == null || !__0.EndsWith(ShaderRewrites.BillboardFile, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                string full = Path.GetFullPath(__0).Replace('\\', '/');
                if (!ShaderRewrites.IsBillboardShader(full) || !full.StartsWith(gameRoot + "/", StringComparison.OrdinalIgnoreCase)) return true;

                string text = File.ReadAllText(__0);
                string patched = ShaderRewrites.Rewrite(full, text);
                if (patched == text)
                {
                    if (!warned) { warned = true; Log.Write("WARNING: the pattern to rewrite in the billboard shader was not found; the game has changed. Water is left as the game draws it."); }
                    return true;
                }

                string error;
                string result = Preprocess(patched, __1, __0, out error);

                // The atmosphere function begins with a clip and discards that would delete sea pixels if the include had not been rewritten while it was read
                // (it is rewritten by OpenPostfix, which relies on the include being read on this thread). If any survived, build the particle shader without atmosphere.
                if (result != null && ShaderRewrites.AtmosphereReady && DiscardWord.IsMatch(result))
                {
                    if (!warnedAtmosphere) { warnedAtmosphere = true; Log.Write("WARNING: the atmosphere include was not rewritten as it was read; the particle shader is built without atmosphere"); }
                    result = Preprocess(ShaderRewrites.Rewrite(full, text, false), __1, __0, out error);
                }
                __result = result;
                __2 = error;
                if (!reported) { reported = true; Log.Write("billboard shader preprocessed by the plugin"); }
                return false;
            }
            catch (Exception e)
            {
                Log.Write("preprocessing the billboard shader failed, it is left as the game draws it: " + (e.InnerException ?? e).Message);
                return true;
            }
        }

        static string Preprocess(string source, object macros, string file, out string error)
        {
            object include = Activator.CreateInstance(includeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { file }, null);
            var args = new object[] { source, macros, include, null, file };
            preprocessingBillboards = true;
            try { return (string)sharpPreprocess.Invoke(null, args); }
            finally { preprocessingBillboards = false; error = (string)args[3]; }
        }

        // Arguments by position: 1 = the requested file name; the result is the file's stream.
        public static void OpenPostfix(string __1, ref Stream __result)
        {
            if (!preprocessingBillboards || !ShaderRewrites.AtmosphereReady || __result == null || __1 == null) return;
            if (!__1.EndsWith(ShaderRewrites.AtmosphereFile, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                string text;
                using (var reader = new StreamReader(__result, new UTF8Encoding(false), true))
                    text = reader.ReadToEnd();
                string patched = ShaderRewrites.RewriteAtmosphere(text);
                if (patched == text) Log.Write("WARNING: nothing to rewrite in the atmosphere include; the game has changed");
                __result = new MemoryStream(new UTF8Encoding(false).GetBytes(patched), false);
            }
            catch (Exception e) { Log.Write("failed to rewrite the atmosphere include: " + e.Message); }
        }
    }
}
