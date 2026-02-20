using FusionModdingAPI.Module;
using NWH;
using UnityEngine;

namespace MultiplayerMod
{
    public static class LocalPlayerTracker
    {
        public static Vector3    Position    { get; private set; }
        public static Vector3    Rotation    { get; private set; }
        public static bool       IsReady     { get; private set; }

        // Car state — uses Vehicle_Type as the stable car ID since
        // GetInstanceID() is not stable across scene loads but Vehicle_Type is an enum
        public static bool         IsInCar      { get; private set; }
        public static Vehicle_Type CurrentCarType { get; private set; } = Vehicle_Type.Useless;
        public static Vector3      CarPosition  { get; private set; }
        public static Quaternion   CarRotation  { get; private set; }
        public static float        CarSpeed     { get; private set; }

        public static void Poll()
        {
            if (!Game.IsInGame) return;
            var gameplay = Game.Gameplay;
            if (gameplay == null) { IsReady = false; return; }

            // Gameplay.vehicleController is the NWH.Vehicle the player is in
            var vehicle = gameplay.Get_CurrentVehicleController; // returns NWH.Vehicle
            if (vehicle != null)
            {
                IsInCar        = true;
                CurrentCarType = vehicle.vehicle_type;

                var t = vehicle.vehicleTransform;
                Position   = t.position;
                Rotation   = t.eulerAngles;
                CarPosition = t.position;
                CarRotation = t.rotation;
                CarSpeed    = vehicle.Speed;
                IsReady     = true;
                return;
            }

            // On foot
            IsInCar        = false;
            CurrentCarType = Vehicle_Type.Useless;

            var mc = Player.MovementController;
            if (mc != null)
            {
                var t = mc.gameObject.transform;
                Position = t.position;
                Rotation = t.eulerAngles;
                IsReady  = true;
                return;
            }

            IsReady = false;
        }
    }

    public class PlayerTrackerComponent : MonoBehaviour
    {
        private void Update() => LocalPlayerTracker.Poll();
    }

    public static class PlayerTrackerBootstrap
    {
        // Hold a real reference to the GO so we can detect when Unity destroys
        // it on scene load (DontDestroyOnLoad is intentionally NOT used because
        // it causes UniStorm.FollowPlayer NullRef spam, so we re-inject instead).
        private static GameObject _trackerGo;

        public static void EnsureInjected()
        {
            // Unity overloads == on UnityEngine.Object so this correctly returns
            // true when the GO has been destroyed by a scene transition.
            if (_trackerGo != null) return;

            _trackerGo = new GameObject("[MP_PlayerTracker]");
            _trackerGo.AddComponent<PlayerTrackerComponent>();
            MultiplayerPlugin.Log.LogInfo("PlayerTracker injected (or re-injected after scene load).");
        }
    }
}