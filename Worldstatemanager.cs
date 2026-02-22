// WorldStateManager.cs — Time synchronisation.
//
// UniStorm.Hour / .Minute / .Day are READ-ONLY properties (no setter).
// We must call the game's own time-setting methods via reflection.
// SetHour(int) and TickToHour exist on UniStormSystem; we try them in order
// and fall back to writing the StartingHour field + forcing an internal update.
//
// Time authority lives on the relay server (server.ts).  The server sends
// TimeSync every 10 s and immediately on connect.  If a player sleeps they
// send SleepRequest; the server jumps to 08:00 next day and broadcasts.
using System.Reflection;
using UniStorm;
using UnityEngine;

namespace MultiplayerMod
{
    public class WorldStateManager
    {
        private bool _sleepPending;

        // Cached reflection members — populated once on first ApplyTimeSync call
        private static bool       _cached;
        private static MethodInfo _miSetHour;     // UniStormSystem.SetHour(int)
        private static MethodInfo _miTickToHour;  // UniStormSystem.TickToHour(int or float)
        private static MethodInfo _miForceUpdate; // any internal recalc method
        private static FieldInfo  _fiStartHour;   // UniStormSystem.StartingHour (int)
        private static FieldInfo  _fiStartMin;    // UniStormSystem.StartingMinute (int)

        private const BindingFlags BF =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // ─────────────────────────────────────────────────────────────────────
        public void Update()
        {
            if (!MultiplayerPlugin.Instance.Network.IsConnected) return;
            CheckSleep();
        }

        // ─────────────────────────────────────────────────────────────────────
        // TIME
        // ─────────────────────────────────────────────────────────────────────
        public void ApplyTimeSync(int hour, int minute, int day)
        {
            var uni = UniStormSystem.Instance;
            if (uni == null)
            {
                MultiplayerPlugin.Log.LogWarning("[Time] UniStormSystem.Instance null");
                return;
            }

            // Skip if already within 1 minute
            if (uni.Hour == hour && Mathf.Abs(uni.Minute - minute) <= 1) return;

            MultiplayerPlugin.Log.LogInfo(
                $"[Time] {uni.Hour:D2}:{uni.Minute:D2} → {hour:D2}:{minute:D2} day={day}");

            BuildCache(uni);

            // Strategy 1 — SetHour(int)
            if (TryInvoke(_miSetHour, uni, hour))   { ForceUpdate(uni); return; }
            // Strategy 2 — TickToHour
            if (TryInvoke(_miTickToHour, uni, hour)) { ForceUpdate(uni); return; }
            // Strategy 3 — write StartingHour field directly
            if (_fiStartHour != null)
            {
                try
                {
                    _fiStartHour.SetValue(uni, hour);
                    if (_fiStartMin != null) _fiStartMin.SetValue(uni, minute);
                    ForceUpdate(uni);
                    MultiplayerPlugin.Log.LogInfo("[Time] Applied via StartingHour field");
                    return;
                }
                catch (System.Exception ex)
                {
                    MultiplayerPlugin.Log.LogWarning($"[Time] StartingHour field failed: {ex.Message}");
                }
            }

            MultiplayerPlugin.Log.LogError("[Time] ALL strategies failed — time not synced!");
        }

        private static bool TryInvoke(MethodInfo mi, object obj, int arg)
        {
            if (mi == null) return false;
            try
            {
                // Some overloads take float, some int — handle both
                object boxed = mi.GetParameters()[0].ParameterType == typeof(float)
                    ? (object)(float)arg
                    : (object)arg;
                mi.Invoke(obj, new[] { boxed });
                MultiplayerPlugin.Log.LogInfo($"[Time] Applied via {mi.Name}({arg})");
                return true;
            }
            catch (System.Exception ex)
            {
                MultiplayerPlugin.Log.LogWarning($"[Time] {mi.Name} failed: {ex.Message}");
                return false;
            }
        }

        private static void ForceUpdate(UniStormSystem uni)
        {
            if (_miForceUpdate == null) return;
            try { _miForceUpdate.Invoke(uni, null); }
            catch { /* best-effort */ }
        }

        private static void BuildCache(UniStormSystem uni)
        {
            if (_cached) return;
            _cached = true;
            var t = uni.GetType();

            _miSetHour    = t.GetMethod("SetHour",    BF, null, new[] { typeof(int)   }, null)
                         ?? t.GetMethod("SetHour",    BF, null, new[] { typeof(float) }, null);

            _miTickToHour = t.GetMethod("TickToHour", BF, null, new[] { typeof(int)   }, null)
                         ?? t.GetMethod("TickToHour", BF, null, new[] { typeof(float) }, null);

            foreach (var name in new[] {
                "CalculateSunAndMoonPosition", "UpdateTime", "UniStormUpdate", "UpdateClock" })
            {
                _miForceUpdate = t.GetMethod(name, BF);
                if (_miForceUpdate != null) break;
            }

            _fiStartHour = t.GetField("StartingHour",   BF);
            _fiStartMin  = t.GetField("StartingMinute", BF);

            MultiplayerPlugin.Log.LogInfo(
                $"[Time] Cache: SetHour={_miSetHour?.Name ?? "null"}" +
                $" TickToHour={_miTickToHour?.Name ?? "null"}" +
                $" ForceUpdate={_miForceUpdate?.Name ?? "null"}" +
                $" StartingHour={_fiStartHour != null}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // SLEEP
        // ─────────────────────────────────────────────────────────────────────
        private void CheckSleep()
        {
            var gameplay = FusionModdingAPI.Module.Game.Gameplay;
            if (gameplay == null) return;

            bool sleeping = gameplay.isSleeping;

            if (!_sleepPending && sleeping)
            {
                _sleepPending = true;
                MultiplayerPlugin.Log.LogInfo("[Time] Sleep → SleepRequest");
                MultiplayerPlugin.Instance.Network.SendRawPublic(
                    PacketWriter.Frame(PacketWriter.WriteSleepRequest()));
            }

            if (!sleeping) _sleepPending = false;
        }
    }
}