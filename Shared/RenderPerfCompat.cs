using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace WaterShading
{
    // Render Performance (a separate plugin) replaces the game's particle renderer with its own, which draws with its own shader and never goes through the
    // places the rest of this plugin works in. This makes the same shadows and haze work there, using only that plugin's public-looking structure, reached by
    // name: nothing of it is copied or edited on disk. Every piece is checked when it is installed; anything missing or different is reported to the player
    // and that part is left alone, and if the changed shader fails to compile, Render Performance is given its own shader back, so it never breaks because of this.
    //
    //   tag    - its per-particle intake (BillboardRenderGroup.Add) gets the sea marker, carried in the particle's alpha-cutout value
    //   shader - the text of its particle shader is changed as its compiler preprocesses it (the same rewrite idea as for the game's shader)
    //   bind   - its resource binding gets the shadow map and the atmosphere constants (in a slot of its own choosing: its own data uses slot 1)
    //   reload - once, its own shader reload is asked for so it recompiles with the change
    public static class RenderPerfCompat
    {
        public const int AtmosphereSlot = 6;

        const string RendererType = "Prism.RenderPerf.Billboards.BillboardRenderer";
        const string GroupType = "Prism.RenderPerf.Billboards.BillboardRenderGroup";
        const string CompilerType = "Prism.Common.FileShaderCompiler";
        const string IncludeHandlerType = "Prism.Common.FileShaderCompiler+FileIncludeHandler";

        static int polls;
        static bool installed, gaveUp, reloaded, atmosphereReady, reportedShape, reportedWorking, tagBroken;
        static volatile bool disabled;
        static Type includeHandlerType;
        static MethodInfo reload, compile;
        static Action<object> tag;
        static bool[] seaKnown = new bool[1024], isSea = new bool[1024];
        static ConstructorInfo stringIdCtor;
        static PropertyInfo stringIdText;

        [ThreadStatic] static bool inPreprocess, ourRewrite;

        // Called every frame for a while after start: the other plugin may load after this one.
        public static void Poll(Harmony harmony)
        {
            if (installed || gaveUp || !Settings.RenderPerf) return;
            if (++polls > 1800) { gaveUp = true; return; }
            if (polls % 10 != 1) return; // looking through every loaded assembly is not free: every tenth frame is plenty
            Type renderer = Log.FindType(RendererType);
            if (renderer == null) return;
            try { Install(harmony, renderer); }
            catch (Exception e) { gaveUp = true; Notifier.Warn("Render Performance is installed but Water Shading could not work with it (" + e.Message + "); the sea gets no shadows or haze while it is on."); }
        }

        static void Install(Harmony harmony, Type renderer)
        {
            Type group = Log.FindType(GroupType), compiler = Log.FindType(CompilerType);
            includeHandlerType = Log.FindType(IncludeHandlerType);
            MethodInfo add = group == null ? null : AccessTools.Method(group, "Add");
            MethodInfo bind = AccessTools.Method(renderer, "BindCommonResources");
            MethodInfo gather = AccessTools.Method(renderer, "Gather");
            reload = AccessTools.Method(renderer, "ReloadShaders");
            compile = compiler == null ? null : AccessTools.Method(compiler, "CompileBytecodeInternal");
            MethodInfo open = includeHandlerType == null ? null : AccessTools.Method(includeHandlerType, "Open");
            Type bytecode = Log.FindType("SharpDX.D3DCompiler.ShaderBytecode");
            MethodInfo preprocess = bytecode == null ? null : bytecode.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Preprocess" && m.GetParameters().Length == 5 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType.IsByRef);
            bool tagReady = BuildTagger();

            string missing = string.Join(", ", new[]
            {
                add == null ? "particle intake" : null, bind == null ? "resource binding" : null, gather == null ? "frame gather" : null,
                reload == null || !reload.IsStatic ? "shader reload" : null, compile == null ? "shader compile" : null, open == null ? "include reader" : null,
                preprocess == null ? "preprocessor" : null, tagReady ? null : "particle layout", PixelBindings.ResolveStage() ? null : "pixel stage"
            }.Where(n => n != null).ToArray());
            if (missing.Length > 0)
            {
                gaveUp = true;
                Notifier.Warn("Render Performance is installed but this version of it is not what Water Shading expects (" + missing + " not found); the sea gets no shadows or haze while it is on.");
                return;
            }

            if (Settings.Atmosphere && !AtmosphereBinding.Available) AtmosphereBinding.Resolve();
            atmosphereReady = CheckAtmosphere();
            WaterMarking.LoadExtra();

            harmony.Patch(add, prefix: new HarmonyMethod(typeof(RenderPerfCompat), nameof(AddPrefix)));
            harmony.Patch(bind, postfix: new HarmonyMethod(typeof(RenderPerfCompat), nameof(BindPostfix)));
            harmony.Patch(gather, prefix: new HarmonyMethod(typeof(RenderPerfCompat), nameof(GatherPrefix)));
            harmony.Patch(preprocess, prefix: new HarmonyMethod(typeof(RenderPerfCompat), nameof(PreprocessPrefix)), finalizer: new HarmonyMethod(typeof(RenderPerfCompat), nameof(PreprocessFinalizer)));
            harmony.Patch(open, postfix: new HarmonyMethod(typeof(RenderPerfCompat), nameof(OpenPostfix)));
            harmony.Patch(compile, finalizer: new HarmonyMethod(typeof(RenderPerfCompat), nameof(CompileFinalizer)));
            installed = true;
            Log.Write("Render Performance found; its particle shader will be changed the first time it draws particles (atmosphere " + (atmosphereReady ? "on" : "off") + ")");
        }

        static bool CheckAtmosphere()
        {
            if (!Settings.Atmosphere || !AtmosphereBinding.Available) return false;
            try
            {
                string include = Path.Combine(Log.ShadersPath(), "Transparent", "Atmosphere", ShaderRewrites.AtmosphereFile);
                if (!File.Exists(include)) return false;
                string original = File.ReadAllText(include);
                bool ready = ShaderRewrites.RewriteAtmosphere(original, AtmosphereSlot) != original;
                if (!ready) Log.Write("atmosphere: the game's atmosphere include cannot be used in Render Performance's shader; its sea gets no atmosphere");
                return ready;
            }
            catch (Exception e) { Log.Write("atmosphere for Render Performance not available: " + e.Message); return false; }
        }

        // ---- tag ----

        // Compiles "if the particle's material is a sea material, set its alpha-cutout value to the marker". The decision is cached per material id.
        static bool BuildTagger()
        {
            Type billboard = Log.FindType("VRageRender.MyBillboard");
            Type stringId = Log.FindType("VRage.Utils.MyStringId");
            FieldInfo material = billboard == null ? null : AccessTools.Field(billboard, "Material");
            FieldInfo cutout = billboard == null ? null : AccessTools.Field(billboard, "AlphaCutout");
            PropertyInfo id = stringId == null ? null : AccessTools.Property(stringId, "Id");
            stringIdText = stringId == null ? null : AccessTools.Property(stringId, "String");
            stringIdCtor = stringId == null ? null : stringId.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
            if (material == null || cutout == null || id == null || stringIdText == null || stringIdCtor == null || material.FieldType != stringId || cutout.FieldType != typeof(float)) return false;

            var input = Expression.Parameter(typeof(object), "b");
            var typed = Expression.Variable(billboard, "typed");
            var body = Expression.Block(new[] { typed },
                Expression.Assign(typed, Expression.Convert(input, billboard)),
                Expression.IfThen(
                    Expression.Call(typeof(RenderPerfCompat).GetMethod(nameof(IsSeaId), BindingFlags.Static | BindingFlags.Public), Expression.Property(Expression.Field(typed, material), id)),
                    Expression.Assign(Expression.Field(typed, cutout), Expression.Constant(WaterMarking.Marker))));
            tag = Expression.Lambda<Action<object>>(body, input).Compile();
            return true;
        }

        public static bool IsSeaId(int id)
        {
            if (id < 0 || id >= 1 << 20) return false;
            if (id >= seaKnown.Length)
            {
                int size = seaKnown.Length;
                while (size <= id) size *= 2;
                Array.Resize(ref seaKnown, size); Array.Resize(ref isSea, size);
            }
            if (seaKnown[id]) return isSea[id];
            bool sea = false;
            try { sea = WaterMarking.IsSeaName((string)stringIdText.GetValue(stringIdCtor.Invoke(new object[] { id }), null)); }
            catch { }
            isSea[id] = sea; seaKnown[id] = true;
            return sea;
        }

        // Argument 0: the particle.
        public static void AddPrefix(object __0)
        {
            if (tag == null || tagBroken || disabled) return;
            try { tag(__0); }
            catch (Exception e) { tagBroken = true; Log.Write("marking Render Performance's sea particles failed: " + e.Message); }
        }

        // ---- reload ----

        // Runs on the render thread at the start of each particle gather; the first time after the hooks are in, Render Performance is asked to rebuild its shaders
        // (it builds them when it starts, before this plugin can have changed anything).
        public static void GatherPrefix()
        {
            if (reloaded || disabled) return;
            reloaded = true;
            try { reload.Invoke(null, null); Log.Write("asked Render Performance to rebuild its particle shaders"); }
            catch (Exception e) { Notifier.Warn("could not ask Render Performance to rebuild its shaders (" + (e.InnerException ?? e).Message + "); the sea gets no shadows or haze while it is on."); disabled = true; }
        }

        // ---- shader ----

        // The preprocessor call Render Performance's compiler makes: arguments 0 = source text, 2 = the include handler, 4 = the file name. Only calls made with
        // Render Performance's own include handler are touched (the game's own compile, on setups that use this call, uses its own handler).
        public static void PreprocessPrefix(ref string __0, object __2, string __4)
        {
            if (disabled || __2 == null || __2.GetType() != includeHandlerType) return;
            string patched = ShaderRewrites.RewriteRenderPerf(__0, atmosphereReady);
            if (patched == __0)
            {
                if (!reportedShape) { reportedShape = true; Notifier.Warn("Render Performance's particle shader is not shaped the way Water Shading expects (it has changed); the sea gets no shadows or haze while it is on."); }
                disabled = true;
                return;
            }
            __0 = patched;
            inPreprocess = true;
            ourRewrite = true;
        }

        public static void PreprocessFinalizer() { inPreprocess = false; }

        // The atmosphere include is read through Render Performance's own include handler; while its particle shader is being preprocessed the include is rewritten
        // (and its constants moved to a slot Render Performance does not use). Arguments by position: 1 = the file name; the result is the stream.
        public static void OpenPostfix(string __1, ref Stream __result)
        {
            if (!inPreprocess || !atmosphereReady || __result == null || __1 == null || !__1.EndsWith(ShaderRewrites.AtmosphereFile, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                string text;
                using (var reader = new StreamReader(__result, new UTF8Encoding(false), true)) text = reader.ReadToEnd();
                string patched = ShaderRewrites.RewriteAtmosphere(text, AtmosphereSlot);
                if (patched == text) Log.Write("WARNING: nothing to rewrite in the atmosphere include (Render Performance); the game has changed");
                __result = new MemoryStream(new UTF8Encoding(false).GetBytes(patched), false);
            }
            catch (Exception e) { Log.Write("failed to rewrite the atmosphere include for Render Performance: " + e.Message); }
        }

        // The safety net: if the changed particle shader does not compile (or Render Performance's compile throws on it), compile the original text instead, so
        // Render Performance keeps working, and tell the player. Arguments by position: 0..3 = file, entry point, profile, macros.
        public static Exception CompileFinalizer(Exception __exception, object __instance, string __0, string __1, string __2, object __3, ref object __result)
        {
            bool ours = ourRewrite;
            ourRewrite = false;
            if (!ours) return __exception;
            string reason = __exception != null ? (__exception.InnerException ?? __exception).Message : __result == null ? "no result" : ResultFailure(__result);
            if (reason == null)
            {
                if (!reportedWorking) { reportedWorking = true; Log.Write("Render Performance's particle shader compiled with the sea changes"); }
                return __exception;
            }
            disabled = true;
            Notifier.Warn("Render Performance's particle shader did not compile with Water Shading's changes (" + reason + "); it was given its own shader back, and the sea gets no shadows or haze while it is on.");
            try { __result = compile.Invoke(__instance, new object[] { __0, __1, __2, __3 }); return null; }
            catch (TargetInvocationException e) { return e.InnerException ?? e; }
        }

        // null when the compile result is fine, otherwise the reason.
        static string ResultFailure(object result)
        {
            try
            {
                object code = result.GetType().GetProperty("ResultCode")?.GetValue(result, null);
                if (code == null) return null;
                object failure = code.GetType().GetProperty("Failure")?.GetValue(code, null);
                if (failure is bool && (bool)failure)
                {
                    string message = result.GetType().GetProperty("Message")?.GetValue(result, null) as string;
                    return string.IsNullOrEmpty(message) ? "compile error" : message.Split('\n')[0];
                }
            }
            catch { }
            return null;
        }

        // ---- bind ----

        // Argument 0: the render context. Runs after Render Performance has bound its own resources for a particle draw.
        public static void BindPostfix(object __0)
        {
            if (disabled || __0 == null) return;
            try { PixelBindings.BindResources(__0, AtmosphereSlot, atmosphereReady); }
            catch (Exception e)
            {
                // Without the shadow map bound the changed shader would draw the sea fully shadowed: hand the shader back.
                disabled = true;
                Notifier.Warn("could not bind the shadow map for Render Performance's particles (" + (e.InnerException ?? e).Message + "); its shader is being given back and the sea gets no shadows or haze while it is on.");
                try { reload.Invoke(null, null); } catch { }
            }
        }
    }
}
