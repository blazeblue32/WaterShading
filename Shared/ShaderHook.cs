using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;

namespace WaterShading
{
    // Setups where the game compiles its shaders itself (stock Windows, or the game under Wine): there is no mirror to build, the game's own compiler reads the files.
    // The game's compile reads the top-level shader file from disk, preprocesses it only to work out the shader cache key, and then compiles the ORIGINAL text. So both steps
    // are changed for the particle shader, to the same rewritten text: the preprocessing step (so the cache key is the key of the changed shader) and the native compile call
    // (so the compiler gets the changed shader). The game's atmosphere code, with its clip and discards turned into early returns, is pasted into the text rather than
    // included by name, so no include has to be rewritten on its way in and the game's own atmosphere shaders keep reading the original file.
    public static class ShaderHook
    {
        // Set to stop doing this (used to hand the particle shaders back to the game if the plugin cannot finish its job).
        public static volatile bool Disabled;

        static string gameRoot, atmosphereText;
        static Type includeType;
        static MethodInfo sharpPreprocess, sharpCompile;
        static bool reported, reportedCompile, warned, warnedCompile;

        // The compile in progress on this thread when its text was swapped: the arguments as the game made them, and the memory holding the changed text.
        [ThreadStatic] static object[] originalArguments;
        [ThreadStatic] static IntPtr swappedSource;

        public static bool Apply(Harmony harmony, string shadersPath)
        {
            gameRoot = Path.GetFullPath(shadersPath).Replace('\\', '/').TrimEnd('/');
            ShaderRewrites.CheckAtmosphere(gameRoot);
            atmosphereText = null;
            if (ShaderRewrites.AtmosphereReady)
            {
                string original = File.ReadAllText(Path.Combine(gameRoot, "Transparent", "Atmosphere", ShaderRewrites.AtmosphereFile));
                string rewritten = ShaderRewrites.RewriteAtmosphere(original);
                if (rewritten != original) atmosphereText = rewritten;
                else ShaderRewrites.AtmosphereReady = false;
            }

            Type compiler = Log.FindType("VRageRender.MyShaderCompiler");
            MethodInfo preprocess = compiler == null ? null : AccessTools.Method(compiler, "PreprocessShader");
            includeType = Log.FindType("VRageRender.MyShaderCompiler+MyIncludeProcessor");
            Type bytecode = Log.FindType("SharpDX.D3DCompiler.ShaderBytecode");
            // Preprocess(string source, ShaderMacro[] defines, Include include, out string errors, string sourceFileName)
            sharpPreprocess = bytecode == null ? null : bytecode.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Preprocess" && m.GetParameters().Length == 5 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType.IsByRef);
            // Compile(IntPtr source, int size, string entryPoint, string profile, ShaderFlags, EffectFlags, ShaderMacro[] defines, Include include, string sourceFileName, ...): the call every
            // other Compile overload ends in.
            sharpCompile = bytecode == null ? null : bytecode.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Compile" && m.GetParameters().Length == 11 && m.GetParameters()[0].ParameterType == typeof(IntPtr) && m.GetParameters()[1].ParameterType == typeof(int));
            if (preprocess == null || includeType == null || sharpPreprocess == null || sharpCompile == null)
            {
                Log.Write("the game's shader compile was not found (" + (preprocess == null ? "PreprocessShader " : "") + (includeType == null ? "include handler " : "") + (sharpPreprocess == null ? "Preprocess " : "") + (sharpCompile == null ? "Compile" : "") + "); the game has changed, shadows cannot be drawn");
                return false;
            }
            harmony.Patch(preprocess, prefix: new HarmonyMethod(typeof(ShaderHook), nameof(PreprocessPrefix)));
            harmony.Patch(sharpCompile, prefix: new HarmonyMethod(typeof(ShaderHook), nameof(CompilePrefix)), finalizer: new HarmonyMethod(typeof(ShaderHook), nameof(CompileFinalizer)));
            Log.Write("billboard shader will be changed at the game's own compile, from " + gameRoot);
            return true;
        }

        // The changed text of the particle shader at the given path, or null when it is not that shader or the pattern to change is missing (reported once).
        static string Patched(string file, string text)
        {
            if (file == null || !file.EndsWith(ShaderRewrites.BillboardFile, StringComparison.OrdinalIgnoreCase)) return null;
            string full = Path.GetFullPath(file).Replace('\\', '/');
            if (!ShaderRewrites.IsBillboardShader(full) || !full.StartsWith(gameRoot + "/", StringComparison.OrdinalIgnoreCase)) return null;
            string patched = ShaderRewrites.Rewrite(full, text, true, atmosphereText);
            if (patched == text)
            {
                if (!warned) { warned = true; Log.Write("WARNING: the pattern to rewrite in the billboard shader was not found; the game has changed. Water is left as the game draws it."); }
                return null;
            }
            return patched;
        }

        // Arguments by position: 0 = shader file, 1 = macros, 2 = the error text (an out parameter of the game's method); the result is the preprocessed text.
        // Returning false skips the game's own preprocessing.
        public static bool PreprocessPrefix(string __0, object __1, ref string __result, ref string __2)
        {
            if (Disabled || __0 == null) return true;
            try
            {
                string patched = Patched(__0, File.ReadAllText(__0));
                if (patched == null) return true;

                string error;
                __result = Preprocess(patched, __1, __0, out error);
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
            try { return (string)sharpPreprocess.Invoke(null, args); }
            finally { error = (string)args[3]; }
        }

        // The native compile call, arguments by position: 0 = pointer to the text, 1 = its size in bytes, 7 = the include handler, 8 = the file name. The game hands its original
        // text here; for the particle shader it is replaced by the changed text for the length of the call.
        public static void CompilePrefix(ref IntPtr __0, ref int __1, string __2, string __3, object __4, object __5, object __6, object __7, string __8, object __9, object __10)
        {
            if (Disabled || __0 == IntPtr.Zero || __1 <= 0 || __7 == null || __7.GetType() != includeType || __8 == null) return;
            try
            {
                if (!__8.EndsWith(ShaderRewrites.BillboardFile, StringComparison.OrdinalIgnoreCase)) return;
                var bytes = new byte[__1];
                Marshal.Copy(__0, bytes, 0, __1);
                string text = new UTF8Encoding(false).GetString(bytes);
                string patched = Patched(__8, text);
                if (patched == null) return;

                byte[] changed = new UTF8Encoding(false).GetBytes(patched);
                IntPtr pointer = Marshal.AllocHGlobal(changed.Length + 1);
                Marshal.Copy(changed, 0, pointer, changed.Length);
                Marshal.WriteByte(pointer, changed.Length, 0);
                originalArguments = new object[] { __0, __1, __2, __3, __4, __5, __6, __7, __8, __9, __10 };
                swappedSource = pointer;
                __0 = pointer; __1 = changed.Length;
                if (!reportedCompile) { reportedCompile = true; Log.Write("billboard shader compiled from the changed text"); }
            }
            catch (Exception e)
            {
                Log.Write("changing the billboard shader's text for the compile failed, it is left as the game draws it: " + (e.InnerException ?? e).Message);
            }
        }

        // Frees the changed text; if the changed shader did not compile, compiles the game's original text instead (so the game is never left without its particle shader),
        // turns the change off and tells the player.
        public static Exception CompileFinalizer(Exception __exception, ref object __result)
        {
            IntPtr pointer = swappedSource;
            object[] args = originalArguments;
            swappedSource = IntPtr.Zero; originalArguments = null;
            if (pointer == IntPtr.Zero) return __exception;
            try
            {
                string reason = __exception != null ? (__exception.InnerException ?? __exception).Message : __result == null ? "no result" : RenderPerfCompat.ResultFailure(__result);
                if (reason == null) return __exception;
                Disabled = true;
                if (!warnedCompile) { warnedCompile = true; Notifier.Warn("the particle shader did not compile with Water Shading's changes (" + reason + "); it was given the game's own shader back and the sea gets no shadows or haze."); }
                try { __result = sharpCompile.Invoke(null, args); return null; }
                catch (TargetInvocationException e) { return e.InnerException ?? e; }
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
    }
}
