using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace WaterShading
{
    // The sea's particles get the same atmospheric in-scatter the game adds to terrain. The shader side (ShaderRewrites) calls the game's own atmosphere
    // function; this side gives that function what it reads in the particle pixel stage: the planet's atmosphere constants (constant buffer 1) and the density
    // lookup texture (texture slot 5). Both are worked out by the game's own code for the nearest planet; the plugin only calls it and uploads the result.
    public static class AtmosphereBinding
    {
        public const int ConstantsSlot = 1; // the game's own slot for these constants
        const int DensityLutSlot = 5;

        public static bool Available;

        static FieldInfo atmospheres, luts, constantsBuffer, environment, matrices, cameraPosition, planet, intensityMultiplier, lutTexture;
        static MethodInfo fill, nearest, mapDiscard, write, unmap, setConstantBuffer;
        static Type constantsType;
        static FieldInfo radiusField, intensityField;
        static bool broken, reportedNone;
        static object lastId;

        // Looks up everything the game provides; false (atmosphere off, shadows unaffected) when anything is missing.
        public static bool Resolve()
        {
            try
            {
                Type renderer = Log.FindType("VRageRender.MyAtmosphereRenderer");
                Type mapping = Log.FindType("VRageRender.MyMapping");
                Type render11 = Log.FindType("VRageRender.MyRender11");
                constantsType = Log.FindType("VRageRender.AtmosphereConstants");
                if (renderer == null || mapping == null || render11 == null || constantsType == null) return Missing("atmosphere types");

                atmospheres = AccessTools.Field(renderer, "m_atmospheres");
                luts = AccessTools.Field(renderer, "AtmosphereLUT");
                constantsBuffer = AccessTools.Field(renderer, "m_cb");
                fill = AccessTools.Method(renderer, "FillAtmosphereConstants");
                nearest = AccessTools.Method(renderer, "GetNearestAtmosphereId");
                environment = AccessTools.Field(render11, "Environment");
                matrices = environment == null ? null : AccessTools.Field(environment.FieldType, "Matrices");
                cameraPosition = matrices == null ? null : AccessTools.Field(matrices.FieldType, "CameraPosition");
                planet = environment == null ? null : AccessTools.Field(environment.FieldType, "Planet");
                intensityMultiplier = planet == null ? null : AccessTools.Field(planet.FieldType, "AtmosphereIntensityMultiplier");
                Type lutsType = luts?.FieldType.GetGenericArguments().LastOrDefault();
                lutTexture = lutsType == null ? null : AccessTools.Field(lutsType, "TransmittanceLut");
                radiusField = AccessTools.Field(constantsType, "AtmosphereRadius");
                intensityField = AccessTools.Field(constantsType, "Intensity");

                var all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                mapDiscard = mapping.GetMethods(all).FirstOrDefault(m => m.Name == "MapDiscard" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.Name == "MyRenderContext");
                var instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var generic = mapping.GetMethods(instance).FirstOrDefault(m => m.Name == "WriteAndPosition" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsByRef);
                write = generic == null ? null : generic.MakeGenericMethod(constantsType);
                unmap = mapping.GetMethod("Unmap", instance);

                string missing = Names("m_atmospheres", atmospheres, "AtmosphereLUT", luts, "m_cb", constantsBuffer, "FillAtmosphereConstants", fill, "GetNearestAtmosphereId", nearest,
                    "MyRender11.Environment", environment, "Matrices", matrices, "CameraPosition", cameraPosition, "Planet", planet, "AtmosphereIntensityMultiplier", intensityMultiplier,
                    "TransmittanceLut", lutTexture, "MapDiscard", mapDiscard, "WriteAndPosition", write, "Unmap", unmap, "Intensity", intensityField);
                if (missing.Length > 0) return Missing(missing);
                Available = true;
                return true;
            }
            catch (Exception e) { return Missing("lookup failed: " + e.Message); }
        }

        // The names (given as name, value, name, value ...) whose value is missing.
        static string Names(params object[] pairs)
        {
            var missing = new System.Collections.Generic.List<string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                if (pairs[i + 1] == null) missing.Add((string)pairs[i]);
            return string.Join(", ", missing.ToArray());
        }

        static bool Missing(string what)
        {
            Log.Write("atmosphere: not found in the game (" + what + "); the sea gets no atmosphere");
            Available = false;
            return false;
        }

        // Called on the render thread each time the particle renderer binds its resources: works out the constants for the nearest planet,
        // uploads them into the game's own atmosphere constant buffer (the game maps it again before every use of its own), and binds it
        // and the density lookup for the particle pixel stage. ps is the pixel stage, setSrv its texture-binding method.
        public static void Bind(object context, object ps, MethodInfo setSrv, int constantsSlot = ConstantsSlot)
        {
            if (!Available || broken) return;
            try
            {
                object camera = cameraPosition.GetValue(matrices.GetValue(environment.GetValue(null)));
                object buffer = constantsBuffer.GetValue(null);
                if (camera == null || buffer == null) return;

                object id = nearest.Invoke(null, new object[] { camera });
                object constants;
                object lut = null;
                if (id == null)
                {
                    constants = Activator.CreateInstance(constantsType); // all zero: no atmosphere here (the shader skips it when Intensity is 0)
                    if (!reportedNone) { reportedNone = true; Log.Write("atmosphere: no planet atmosphere near the camera"); }
                }
                else
                {
                    object atmosphere = ((IDictionary)atmospheres.GetValue(null))[id];
                    object multiplier = intensityMultiplier.GetValue(planet.GetValue(environment.GetValue(null)));
                    constants = Activator.CreateInstance(constantsType);
                    var args = new object[] { camera, atmosphere, multiplier, 0f, constants };
                    fill.Invoke(null, args);
                    constants = args[4];
                    var luts2 = (IDictionary)luts.GetValue(null);
                    if (luts2.Contains(id)) lut = lutTexture.GetValue(luts2[id]);
                    if (!id.Equals(lastId))
                    {
                        lastId = id;
                        Log.Write("atmosphere: planet " + id + " bound (atmosphere radius " + radiusField?.GetValue(constants) + ", intensity " + intensityField.GetValue(constants) + ", lookup " + (lut == null ? "missing" : "found") + ")");
                    }
                }

                object mapping = mapDiscard.Invoke(null, new object[] { context, buffer });
                write.Invoke(mapping, new object[] { constants });
                unmap.Invoke(mapping, null);

                setConstantBuffer = setConstantBuffer ?? AccessTools.Method(ps.GetType(), "SetConstantBuffer");
                setConstantBuffer.Invoke(ps, new object[] { constantsSlot, buffer });
                if (lut != null) setSrv.Invoke(ps, new object[] { DensityLutSlot, lut });
            }
            catch (Exception e)
            {
                broken = true;
                Log.Write("atmosphere: binding failed, the sea gets no atmosphere: " + e);
            }
        }
    }
}
