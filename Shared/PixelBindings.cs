using System;
using System.Reflection;
using HarmonyLib;

namespace WaterShading
{
    // The renderer binds the sun shadow map and its sampler for the billboard vertex stage only (particles are lit per vertex).
    // The rewritten billboard shader reads the shadow map per pixel, so this also binds the same two resources for the pixel stage
    // right after the renderer has bound its own.
    public static class PixelBindings
    {
        const int ShadowSamplerSlot = 15; // the shader's sampler slot for the shadow map
        const int ShadowMapSlot = 16;     // the shader's texture slot for the cascade shadow map array

        // Set by the plugin once the shader redirect is installed; until then the postfix does nothing.
        public static volatile bool Enabled;

        static MethodInfo getPixelShader, setSampler, setSrv, generatePixelShaders;
        static bool regenerated;
        static object samplerState; // fetched on use: the managers may not exist when the plugin loads
        static Func<object> shadowMap;
        static bool broken, reportedBound, reportedWaiting;

        public static bool Apply(Harmony harmony)
        {
            Type renderer = Log.FindType("VRageRender.MyBillboardRenderer");
            MethodInfo bind = renderer == null ? null : AccessTools.Method(renderer, "BindResourcesCommon");
            if (bind == null) { Log.Write("billboard resource binding not found; the game has changed, shadows cannot be drawn"); return false; }

            Type context = Log.FindType("VRage.Render11.RenderContext.MyRenderContext");
            getPixelShader = context == null ? null : AccessTools.PropertyGetter(context, "PixelShader");
            Type stage = getPixelShader?.ReturnType;
            setSampler = stage == null ? null : AccessTools.Method(stage, "SetSampler");
            setSrv = stage == null ? null : AccessTools.Method(stage, "SetSrv");
            if (getPixelShader == null || setSampler == null || setSrv == null) { Log.Write("pixel stage binding calls not found; the game has changed, shadows cannot be drawn"); return false; }

            // The renderer builds its billboard pixel shaders once at start-up, which can be before this plugin loads (they then come
            // from the game's precompiled cache and never pass through the redirect). Building them again once the redirect is in place fixes that.
            generatePixelShaders = AccessTools.Method(renderer, "GeneratePS");
            if (generatePixelShaders == null) { Log.Write("billboard shader builder not found; the game has changed, shadows cannot be drawn"); return false; }

            if (Settings.Atmosphere) AtmosphereBinding.Resolve();
            if (!WaterMarking.Apply(harmony, renderer)) return false;
            harmony.Patch(bind, postfix: new HarmonyMethod(typeof(PixelBindings), nameof(Postfix)));
            return true;
        }

        // Arguments by position: 0 = the render context.
        public static void Postfix(object __0)
        {
            if (!Enabled || broken || __0 == null) return;
            try
            {
                if (shadowMap == null && !Resolve()) { if (!reportedWaiting) { reportedWaiting = true; Log.Write("shadow resources not ready yet, retrying each frame"); } return; }
                // Only once the shadow map can be bound: the rebuilt shaders read it, and an unbound map would read as full shadow.
                // This runs on the render thread, where the renderer itself builds shaders.
                if (!regenerated)
                {
                    regenerated = true;
                    RebuildPixelShaders();
                    if (broken) return;
                }
                object ps = getPixelShader.Invoke(__0, null);
                setSampler.Invoke(ps, new object[] { ShadowSamplerSlot, samplerState });
                setSrv.Invoke(ps, new object[] { ShadowMapSlot, shadowMap() });
                if (!reportedBound) { reportedBound = true; Log.Write("shadow map bound for the billboard pixel stage"); }
                if (ShaderRewrites.AtmosphereReady) AtmosphereBinding.Bind(__0, ps, setSrv);
            }
            catch (Exception e)
            {
                broken = true;
                Log.Write("binding the shadow map failed, shadows on water are off: " + e);
            }
        }

        // The renderer keeps each pixel shader under a key (file + macros) and hands back the existing one when asked again.
        // So: forget the billboard shaders' keys, then ask the renderer to build its billboard shaders again; this time they are compiled
        // (through the redirect) instead of coming from the game's precompiled cache. The old shader objects stay allocated but unused.
        static void RebuildPixelShaders()
        {
            try
            {
                Type shaders = Log.FindType("VRageRender.MyPixelShaders");
                var table = shaders == null ? null : AccessTools.Field(shaders, "m_keyToId")?.GetValue(null) as System.Collections.IDictionary;
                if (table == null) { Fail("pixel shader table not found"); return; }
                FieldInfo fileField = AccessTools.Field(table.GetType().GetGenericArguments()[0], "FileId");
                if (fileField == null) { Fail("pixel shader key layout not found"); return; }

                int removed = 0;
                lock (table)
                {
                    var stale = new System.Collections.Generic.List<object>();
                    foreach (System.Collections.DictionaryEntry entry in table)
                        if (string.Equals(fileField.GetValue(entry.Key)?.ToString(), BillboardShader, StringComparison.OrdinalIgnoreCase)) stale.Add(entry.Key);
                    foreach (object key in stale) { table.Remove(key); removed++; }
                }
                Log.Write("building " + removed + " billboard pixel shaders again so they pass through the redirect");
                if (removed == 0) { Fail("no billboard pixel shaders were registered"); return; }
                generatePixelShaders.Invoke(null, null);
                Log.Write("billboard pixel shaders rebuilt");
            }
            catch (Exception e)
            {
                broken = true;
                Log.Write("rebuilding the billboard pixel shaders failed, shadows on water are off: " + e);
            }
        }

        const string BillboardShader = "Transparent/Billboards.hlsl";

        // Finds the game's shadow sampler and the cascade shadow map array; false (and retried next call) while they do not exist yet.
        static bool Resolve()
        {
            Type samplers = Log.FindType("VRage.Render11.Resources.MySamplerStateManager");
            Type managers = Log.FindType("VRage.Render11.Common.MyManagers");
            if (samplers == null || managers == null) { Fail("sampler or manager types not found"); return false; }

            MemberInfo samplerMember = (MemberInfo)AccessTools.Field(samplers, "Shadowmap") ?? AccessTools.Property(samplers, "Shadowmap");
            MemberInfo shadowsMember = (MemberInfo)AccessTools.Field(managers, "Shadows") ?? AccessTools.Property(managers, "Shadows");
            if (samplerMember == null || shadowsMember == null) { Fail("shadow members not found"); return false; }

            samplerState = Read(samplerMember, null);
            object shadows = Read(shadowsMember, null);
            if (samplerState == null || shadows == null) return false; // not created yet, try again on a later call

            PropertyInfo cascades = AccessTools.Property(shadows.GetType(), "ShadowCascades");
            if (cascades == null) { Fail("ShadowCascades not found"); return false; }
            object cascadeSet = cascades.GetValue(shadows, null);
            if (cascadeSet == null) return false;
            PropertyInfo array = AccessTools.Property(cascadeSet.GetType(), "CascadeShadowmapArray");
            if (array == null) { Fail("CascadeShadowmapArray not found"); return false; }

            // Re-read through the owner each frame: the array can be recreated when shadow settings change.
            shadowMap = () => array.GetValue(cascades.GetValue(shadows, null), null);
            return true;
        }

        static object Read(MemberInfo member, object instance)
        {
            var f = member as FieldInfo;
            if (f != null) return f.GetValue(instance);
            return ((PropertyInfo)member).GetValue(instance, null);
        }

        static void Fail(string why)
        {
            broken = true;
            Log.Write(why + "; the game has changed, shadows on water are off");
        }
    }
}
