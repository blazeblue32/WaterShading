using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace WaterShading
{
    // Log-only helpers for finding out why the water does not change (setting Diagnostics=1). Nothing here changes rendering.
    public static class Diagnostics
    {
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static long calls, nextReportMs = 0;
        static int reports;
        static int[] snapshot;
        static FieldInfo bundleField, batchesField, indexField, litField, numField, cutoutField, singleField, textureField;
        static Type renderer;

        public static void Install(Harmony harmony, Type billboardRenderer)
        {
            renderer = billboardRenderer;
            bundleField = AccessTools.Field(renderer, "m_psBundle");
            batchesField = AccessTools.Field(renderer, "m_batches");
            foreach (string name in new[] { "Init", "GeneratePS", "OnDeviceReset", "OnDeviceEnd", "OnSessionEnd" })
            {
                MethodInfo m = AccessTools.Method(renderer, name);
                if (m != null) harmony.Patch(m, prefix: new HarmonyMethod(typeof(Diagnostics), nameof(Called)) { });
            }
            Type shaders = Log.FindType("VRageRender.MyPixelShaders");
            foreach (string name in new[] { "Recompile", "OnDeviceEnd" })
            {
                MethodInfo m = shaders == null ? null : AccessTools.Method(shaders, name);
                if (m != null) harmony.Patch(m, prefix: new HarmonyMethod(typeof(Diagnostics), nameof(Called)));
            }
            Log.Write("diagnostics on");
        }

        // __originalMethod is filled in by Harmony.
        public static void Called(MethodBase __originalMethod)
        {
            try
            {
                var frames = new StackTrace(2, false).GetFrames();
                var sb = new StringBuilder();
                for (int i = 0; frames != null && i < Math.Min(frames.Length, 6); i++)
                    sb.Append(frames[i].GetMethod()?.DeclaringType?.Name).Append('.').Append(frames[i].GetMethod()?.Name).Append(" < ");
                Log.Write("diag: " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " called; " + sb);
            }
            catch { }
        }

        // Called from the bind postfix: once the pixel shaders have been rebuilt, remember which shader slots they use and watch for changes.
        public static void AfterRebuild()
        {
            snapshot = ReadBundle();
            Log.Write("diag: pixel shader slots after rebuild: " + (snapshot == null ? "(unreadable)" : string.Join(",", snapshot)));
        }

        public static void Tick()
        {
            calls++;
            long now = clock.ElapsedMilliseconds;
            if (now < nextReportMs || reports >= 14) return;
            nextReportMs = now + 4000;
            reports++;
            try
            {
                var sb = new StringBuilder("diag: report " + reports + ", bind calls so far " + calls);
                int[] now2 = ReadBundle();
                if (snapshot != null && now2 != null)
                {
                    bool same = now2.Length == snapshot.Length;
                    for (int i = 0; same && i < now2.Length; i++) same = now2[i] == snapshot[i];
                    sb.Append("; shader slots ").Append(same ? "unchanged" : "CHANGED to " + string.Join(",", now2));
                }
                var list = batchesField?.GetValue(null) as IList;
                if (list != null)
                {
                    sb.Append("; batches ").Append(list.Count).Append(": ");
                    int shown = 0;
                    foreach (object b in list)
                    {
                        if (b == null) continue;
                        Type t = b.GetType();
                        litField = litField ?? AccessTools.Field(t, "Lit"); numField = numField ?? AccessTools.Field(t, "Num");
                        cutoutField = cutoutField ?? AccessTools.Field(t, "AlphaCutout"); singleField = singleField ?? AccessTools.Field(t, "SingleChannel");
                        textureField = textureField ?? AccessTools.Field(t, "Texture");
                        if (shown++ >= 12) { sb.Append("..."); break; }
                        sb.Append("[n=").Append(numField?.GetValue(b)).Append(" lit=").Append(litField?.GetValue(b)).Append(" cut=").Append(cutoutField?.GetValue(b))
                          .Append(" 1ch=").Append(singleField?.GetValue(b)).Append(" ").Append(textureField?.GetValue(b)).Append("] ");
                    }
                }
                Log.Write(sb.ToString());
            }
            catch (Exception e) { Log.Write("diag: report failed: " + e.Message); }
        }

        static int[] ReadBundle()
        {
            var arr = bundleField?.GetValue(null) as Array;
            if (arr == null) return null;
            var result = new int[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                object id = arr.GetValue(i);
                indexField = indexField ?? AccessTools.Field(id.GetType(), "Index");
                result[i] = indexField == null ? -1 : Convert.ToInt32(indexField.GetValue(id));
            }
            return result;
        }
    }
}
