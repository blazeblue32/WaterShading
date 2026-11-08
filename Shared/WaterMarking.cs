using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;

namespace WaterShading
{
    // The particle shader has no way to tell the sea from any other particle, so the plugin tags the sea's particles on their way to the GPU:
    // the renderer fills one data record per particle and then groups them into batches by material. When a batch of a water material is
    // closed, this writes a marker number into an input the shader does not use otherwise, and the rewritten shader looks for that number.
    public static class WaterMarking
    {
        // Must match the number the shader rewrite compares with.
        public const float Marker = 12345f;

        static readonly Dictionary<object, string> names = new Dictionary<object, string>();
        static readonly HashSet<string> reportedMaterials = new HashSet<string>();
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static long nextModLookup;
        static HashSet<string> modTextures;
        static FieldInfo arrayField, dataField, idField;
        static Action<Array, int, int> mark;
        static bool broken, reported;
        static string[] extra = new string[0];

        public static bool Apply(Harmony harmony, Type renderer)
        {
            MethodInfo addBatch = AccessTools.Method(renderer, "AddBatch");
            arrayField = AccessTools.Field(renderer, "m_arrayDataBillboards");
            Type arrayType = arrayField?.FieldType;
            dataField = arrayType == null ? null : AccessTools.Field(arrayType, "Data");
            Type element = dataField?.FieldType.GetElementType();
            FieldInfo saturation = element == null ? null : AccessTools.Field(element, "AlphaSaturation");
            if (addBatch == null || dataField == null || saturation == null) { Log.Write("billboard batching not found; the game has changed, shadows cannot be drawn"); return false; }

            mark = Build(dataField.FieldType, saturation);
            extra = (Settings.ExtraMaterials ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < extra.Length; i++) extra[i] = extra[i].Trim();
            harmony.Patch(addBatch, postfix: new HarmonyMethod(typeof(WaterMarking), nameof(Postfix)));
            return true;
        }

        // Compiles "for i in [start, end): data[i].AlphaSaturation = Marker" for the game's record type.
        static Action<Array, int, int> Build(Type arrayType, FieldInfo saturation)
        {
            var data = Expression.Parameter(typeof(Array), "data");
            var start = Expression.Parameter(typeof(int), "start");
            var end = Expression.Parameter(typeof(int), "end");
            var typed = Expression.Variable(arrayType, "typed");
            var i = Expression.Variable(typeof(int), "i");
            var done = Expression.Label("done");
            var body = Expression.Block(new[] { typed, i },
                Expression.Assign(typed, Expression.Convert(data, arrayType)),
                Expression.Assign(i, start),
                Expression.Loop(
                    Expression.IfThenElse(
                        Expression.LessThan(i, end),
                        Expression.Block(
                            Expression.Assign(Expression.Field(Expression.ArrayAccess(typed, i), saturation), Expression.Constant(Marker)),
                            Expression.PreIncrementAssign(i)),
                        Expression.Break(done)),
                    done));
            return Expression.Lambda<Action<Array, int, int>>(body, data, start, end).Compile();
        }

        // Arguments by position: 0 = end index, 1 = first index, 3 = the batch's material.
        public static void Postfix(int __0, int __1, object __3)
        {
            if (!PixelBindings.Enabled || broken || __3 == null || __0 <= __1) return;
            try
            {
                if (!IsWater(__3)) return;
                var data = dataField.GetValue(arrayField.GetValue(null)) as Array;
                if (data == null) return;
                mark(data, Math.Max(0, __1), Math.Min(__0, data.Length));
                if (!reported) { reported = true; Log.Write("marking sea particles of material " + NameOf(__3)); }
            }
            catch (Exception e)
            {
                broken = true;
                Log.Write("marking sea particles failed, shadows on water are off: " + e);
            }
        }

        // Rules, in order: the name contains "water" or "foam"; the name is in ExtraMaterials; the name is one of the textures the Water Mod itself
        // offers for the sea (its /wtexture command: JLava, JCheese and so on, plus the ones other water mods add). The first two need nothing from the mod.
        static bool IsWater(object material)
        {
            string name;
            if (!names.TryGetValue(material, out name)) names[material] = name = NameOf(material);
            bool water = name.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("foam", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!water) foreach (string e in extra) if (e.Length > 0 && name.Equals(e, StringComparison.OrdinalIgnoreCase)) water = true;
            if (!water)
            {
                var modList = ModTextures();
                water = modList != null && modList.Contains(name);
            }
            if (water && reportedMaterials.Add(name)) Log.Write("particle material treated as sea: " + name);
            return water;
        }

        // The Water Mod's own list of selectable sea textures (a static set in its script assembly), looked up again every few seconds
        // so a new world, or textures added by another water mod, are picked up.
        static HashSet<string> ModTextures()
        {
            long now = clock.ElapsedMilliseconds;
            if (now < nextModLookup) return modTextures;
            nextModLookup = now + 5000;
            HashSet<string> found = null;
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string asmName = a.GetName().Name;
                    if (asmName == null || asmName.IndexOf("sbm_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Type t = a.GetType("Jakaria.WaterData", false);
                    var f = t == null ? null : t.GetField("WaterTextures", BindingFlags.Public | BindingFlags.Static);
                    var set = f == null ? null : f.GetValue(null) as HashSet<string>;
                    if (set != null) found = set;
                }
            }
            catch { }
            if (found != null && !ReferenceEquals(found, modTextures))
                Log.Write("Water Mod texture list found (" + found.Count + " entries): " + string.Join(", ", found));
            modTextures = found;
            return modTextures;
        }

        static string NameOf(object material)
        {
            idField = idField ?? AccessTools.Field(material.GetType(), "Id");
            return idField?.GetValue(material)?.ToString() ?? "";
        }
    }
}
