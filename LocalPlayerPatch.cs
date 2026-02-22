using FusionModdingAPI.Module;
using NWH;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerMod
{
    public static class LocalPlayerTracker
    {
        public static Vector3    Position    { get; private set; }
        public static Vector3    Rotation    { get; private set; }
        public static bool       IsReady     { get; private set; }

        // Car state for the vehicle the LOCAL player is currently driving
        public static bool         IsInCar        { get; private set; }
        public static Vehicle_Type CurrentCarType { get; private set; } = Vehicle_Type.Useless;
        public static Vector3      CarPosition    { get; private set; }
        public static Quaternion   CarRotation    { get; private set; }
        public static float        CarSpeed       { get; private set; }

        // All vehicles in the scene — used to broadcast parked car positions
        // so remote players see cars where we left them even when we're on foot.
        public static IReadOnlyList<NWH.Vehicle> AllVehicles => _allVehicles;
        private static readonly List<NWH.Vehicle> _allVehicles = new();

        public static void Poll()
        {
            if (!Game.IsInGame) return;
            var gameplay = Game.Gameplay;
            if (gameplay == null) { IsReady = false; return; }

            // Use the API's canonical vehicle array — covers all types including buggy.
            _allVehicles.Clear();
            var apiVehicles = FusionModdingAPI.Module.Vehicle.Vehicles;
            if (apiVehicles != null)
                foreach (var v in apiVehicles)
                    if (v != null) _allVehicles.Add(v);

            // Vehicle the local player is currently driving (same source as API)
            var vehicle = FusionModdingAPI.Module.Vehicle.CurrentVehicle;
            if (vehicle != null)
            {
                IsInCar        = true;
                CurrentCarType = vehicle.vehicle_type;
                var t = vehicle.vehicleTransform;
                Position    = t.position;
                Rotation    = t.eulerAngles;
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