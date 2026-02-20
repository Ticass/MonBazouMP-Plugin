using UniStorm;
using UnityEngine;

namespace MultiplayerMod
{
    /// <summary>
    /// Handles server-authoritative world state:
    ///   - Time sync via UniStormSystem
    ///   - Sleep detection via Gameplay.isSleeping (bool field, not an enum)
    ///
    /// From Gameplay.cs decompile:
    ///   - Gameplay.isSleeping is the bool flag, set true in Sleep_Start coroutine
    ///   - Time is driven by uniStormSystem.AddOneSecond() calls
    ///   - UniStormSystem.Instance.Hour is the readable current hour (float 0-24)
    /// </summary>
    public class WorldStateManager
    {
        private bool  _sleepPending  = false;
        private float _syncTimer     = 0f;
        private const float SYNC_INTERVAL = 30f;

        public void Update()
        {
            if (!MultiplayerPlugin.Instance.Network.IsConnected) return;

            _syncTimer += Time.deltaTime;

            CheckSleep();
        }

        private void CheckSleep()
        {
            var gameplay = FusionModdingAPI.Module.Game.Gameplay;
            if (gameplay == null) return;

            // isSleeping is the correct field from Gameplay.cs — not a PlayerState enum
            bool sleeping = gameplay.isSleeping;

            if (!_sleepPending && sleeping)
            {
                _sleepPending = true;
                // Mon Bazou wakes at Tick_SkipNight_Stop which is 8am
                float wakeHour = 8f;
                MultiplayerPlugin.Log.LogInfo($"[Time] Sleep detected → requesting skip to {wakeHour}h");
                MultiplayerPlugin.Instance.Network.SendSleepRequest(wakeHour);
            }

            if (!sleeping)
                _sleepPending = false;
        }

        /// <summary>
        /// Called from NetworkClient on main thread when server sends SetTime or TimeSync.
        /// instantSnap=true for sleep skips, false for periodic drift correction.
        /// </summary>
        public void ApplyServerTime(float targetHour, bool instantSnap)
        {
            var uni = UniStormSystem.Instance;
            if (uni == null)
            {
                MultiplayerPlugin.Log.LogWarning("[Time] UniStormSystem.Instance is null");
                return;
            }

            float currentHour = uni.Hour;
            float diff = Mathf.Abs(currentHour - targetHour);

            // For periodic sync: only correct if drift > 3 minutes (0.05h) to avoid jitter
            if (!instantSnap && diff < 0.05f) return;

            MultiplayerPlugin.Log.LogInfo(
                $"[Time] Applying server time {targetHour:F2}h " +
                $"(was {currentHour:F2}h, snap={instantSnap})");

            // UniStorm stores time as Hour (0-24 float). Setting it directly
            // updates the sky/lighting on the next UniStorm tick automatically.
            uni.Hour = (int)targetHour;

            // If it's a big jump (sleep skip), also update the day/night internals
            // via reflection since UniStorm doesn't expose a public "jump to hour" method.
            if (instantSnap)
                ForceUniStormUpdate(uni);
        }

        private static void ForceUniStormUpdate(UniStormSystem uni)
        {
            // UniStorm recalculates sun/moon position internally every frame via Update().
            // For an instant snap we want it to apply NOW, so we poke its internal
            // CalculateSunAndMoonPosition method via reflection.
            try
            {
                var t = uni.GetType();
                foreach (var methodName in new[] {
                    "CalculateSunAndMoonPosition",
                    "UpdateTime",
                    "UniStormUpdate" })
                {
                    var m = t.GetMethod(methodName,
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance);
                    if (m != null)
                    {
                        m.Invoke(uni, null);
                        MultiplayerPlugin.Log.LogInfo($"[Time] Called UniStorm.{methodName}()");
                        return;
                    }
                }
                MultiplayerPlugin.Log.LogWarning("[Time] No suitable UniStorm update method found via reflection");
            }
            catch (System.Exception ex)
            {
                MultiplayerPlugin.Log.LogWarning($"[Time] UniStorm reflect error: {ex.Message}");
            }
        }
    }
}