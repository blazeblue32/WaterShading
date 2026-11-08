using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;

namespace WaterShading
{
    // Tells the player about a problem the plugin cannot fix itself: always a line in the game log, and once a world is running also a notification on the HUD.
    public static class Notifier
    {
        static readonly List<string> pending = new List<string>();
        static bool unavailable;

        public static void Warn(string message)
        {
            Log.Write("WARNING: " + message);
            lock (pending) pending.Add("Water Shading: " + message);
        }

        // Called every frame (cheap when there is nothing to say).
        public static void Tick()
        {
            if (unavailable || pending.Count == 0) return;
            try
            {
                Type session = Log.FindType("Sandbox.Game.World.MySession");
                object current = session == null ? null : (AccessTools.Property(session, "Static")?.GetValue(null, null) ?? AccessTools.Field(session, "Static")?.GetValue(null));
                if (current == null) return; // no world yet: keep the message for later

                Type hud = Log.FindType("Sandbox.Game.Gui.MyHud");
                object notifications = hud == null ? null : AccessTools.Field(hud, "Notifications")?.GetValue(null);
                Type notificationType = Log.FindType("Sandbox.Game.Gui.MyHudNotification");
                Type stringId = Log.FindType("VRage.Utils.MyStringId");
                if (notifications == null || notificationType == null || stringId == null) return;

                string[] messages;
                lock (pending) { messages = pending.ToArray(); pending.Clear(); }
                MethodInfo getId = AccessTools.Method(stringId, "GetOrCompute");
                ConstructorInfo ctor = notificationType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(c => c.GetParameters().Length == 6);
                MethodInfo add = null;
                foreach (MethodInfo m in notifications.GetType().GetMethods())
                    if (m.Name == "Add" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(notificationType)) { add = m; break; }
                if (getId == null || ctor == null || add == null) { unavailable = true; return; }

                ParameterInfo[] p = ctor.GetParameters();
                foreach (string text in messages)
                {
                    object notification = ctor.Invoke(new object[]
                    {
                        getId.Invoke(null, new object[] { text }), 15000, "Red",
                        Enum.ToObject(p[3].ParameterType, 0), 0, Level(p[5].ParameterType)
                    });
                    add.Invoke(notifications, new object[] { notification });
                }
            }
            catch (Exception e) { unavailable = true; Log.Write("could not show a HUD notification: " + e.Message); }
        }

        static object Level(Type levelType)
        {
            try { return Enum.Parse(levelType, "Important"); }
            catch { return Enum.ToObject(levelType, 0); }
        }
    }
}
