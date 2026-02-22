using UniStorm;
using UnityEngine;

namespace MultiplayerMod
{
    /// <summary>
    /// Applies server-authoritative time to the local UniStorm clock.
    ///
    /// DESIGN:
    ///   The server owns the clock — it ticks independently and broadcasts
    ///   TimeSync every 30s and SetTime on connect/sleep. Clients never send
    ///   time to the server on their own initiative. This prevents two clocks
    ///   fighting each other.
    ///
    ///   Sleep is the one exception: when the local player sleeps, we send a
    ///   SleepRequest so the server can jump its clock and broadcast SetTime
    ///   to all clients simultaneously.
    /// </summary>
    public class WorldStateManager
    {
        private bool _sleepPending = false;

        public void Update()
        {
            if (!MultiplayerPlugin.Instance.Network.IsConnected) return;
            CheckSleep();
        }

        // ── Sleep detection ───────────────────────────────────────────────────

        private void CheckSleep()
        {
            var gameplay = FusionModdingAPI.Module.Game.Gameplay;
            if (gameplay == null) return;

            bool sleeping = gameplay.isSleeping;

            if (!_sleepPending && sleeping)
            {
                _sleepPending = true;
                const float wakeHour = 8f;
                MultiplayerPlugin.Log.LogInfo($"[Time] Sleep → requesting skip to {wakeHour}h");
                MultiplayerPlugin.Instance.Network.SendSleepRequest(wakeHour);
            }

            if (!sleeping)
                _sleepPending = false;
        }

        // ── Apply time from server ────────────────────────────────────────────

        /// <summary>
        /// Called by NetworkClient on the main thread for SetTime and TimeSync packets.
        /// instantSnap=true  → hard jump (connect / sleep skip)
        /// instantSnap=false → soft correction (periodic drift fix)
        /// </summary>
        public void ApplyServerTime(float targetHour, bool instantSnap)
        {
            var uni = UniStormSystem.Instance;
            if (uni == null)
            {
                MultiplayerPlugin.Log.LogWarning("[Time] UniStormSystem.Instance is null");
                return;
            }

            // Convert float hour (e.g. 8.75) → int hour (8) + int minute (45)
            int targetH = Mathf.FloorToInt(targetHour);
            int targetM = Mathf.RoundToInt((targetHour - targetH) * 60f);

            float currentHourFloat = uni.Hour + uni.Minute / 60f;
            float diff = Mathf.Abs(currentHourFloat - targetHour);

            // Ignore tiny drift on periodic sync (< 3 real minutes)
            if (!instantSnap && diff < 0.05f) return;

            MultiplayerPlugin.Log.LogInfo(
                $"[Time] {(instantSnap ? "Snap" : "Drift fix")} → {targetH:D2}:{targetM:D2} (was {uni.Hour:D2}:{uni.Minute:D2})");

            uni.Hour   = targetH;
            uni.Minute = targetM;

            if (instantSnap)
                ForceUniStormUpdate(uni);
        }

        private static void ForceUniStormUpdate(UniStormSystem uni)
        {
            try
            {
                var t = uni.GetType();
                foreach (var name in new[] { "CalculateSunAndMoonPosition", "UpdateTime", "UniStormUpdate" })
                {
                    var m = t.GetMethod(name,
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);
                    if (m != null)
                    {
                        m.Invoke(uni, null);
                        MultiplayerPlugin.Log.LogInfo($"[Time] Called UniStorm.{name}()");
                        return;
                    }
                }
                MultiplayerPlugin.Log.LogWarning("[Time] No UniStorm update method found via reflection");
            }
            catch (System.Exception ex)
            {
                MultiplayerPlugin.Log.LogWarning($"[Time] Reflect error: {ex.Message}");
            }
        }
    }
}