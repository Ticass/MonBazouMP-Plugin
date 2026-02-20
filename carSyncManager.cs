using NWH;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerMod
{
    /// <summary>
    /// Syncs vehicles driven by remote players.
    ///
    /// From the decompile:
    ///   - Gameplay.mainVehicles = VehicleController[] (NWH physics vehicles)
    ///   - Gameplay.buggyVehicle = the buggy
    ///   - Vehicle_Type enum is the stable, human-readable car ID across sessions
    ///   - NWH.Vehicle.vehicleTransform is the root transform
    ///   - NWH.Vehicle has a Rigidbody accessible via vehicleRigidbody
    ///
    /// We use Vehicle_Type (sent as int over the wire) as the key because
    /// GetInstanceID() is not stable but Vehicle_Type is a deterministic enum.
    /// The driver sends CarUpdate; receivers find the matching NWH.Vehicle by
    /// type and move it kinematically.
    /// </summary>
    public class CarSyncManager
    {
        // Vehicle_Type (cast to int) → proxy
        private readonly Dictionary<int, CarProxy> _proxies  = new();
        // Vehicle_Type (cast to int) → remote player ID driving it
        private readonly Dictionary<int, int>      _owners   = new();

        public void OnCarUpdate(int playerId, int carTypeInt,
            Vector3 position, Quaternion rotation, float speed)
        {
            if (!_owners.ContainsKey(carTypeInt))
            {
                _owners[carTypeInt] = playerId;
                MultiplayerPlugin.Log.LogInfo(
                    $"[Car] Player #{playerId} driving {(Vehicle_Type)carTypeInt}");
            }

            if (!_proxies.TryGetValue(carTypeInt, out var proxy))
            {
                var vehicle = FindVehicleByType((Vehicle_Type)carTypeInt);
                if (vehicle == null)
                {
                    // Vehicle not in scene yet (e.g. not purchased), skip silently
                    return;
                }
                proxy = new CarProxy(vehicle);
                _proxies[carTypeInt] = proxy;
                proxy.SetRemoteControlled(true);
            }

            proxy.TargetPosition = position;
            proxy.TargetRotation = rotation;
        }

        public void OnPlayerLeft(int playerId)
        {
            var toRelease = new List<int>();
            foreach (var kv in _owners)
                if (kv.Value == playerId) toRelease.Add(kv.Key);

            foreach (var key in toRelease)
            {
                _owners.Remove(key);
                if (_proxies.TryGetValue(key, out var proxy))
                {
                    proxy.SetRemoteControlled(false);
                    _proxies.Remove(key);
                }
                MultiplayerPlugin.Log.LogInfo(
                    $"[Car] Released {(Vehicle_Type)key} (owner #{playerId} left)");
            }
        }

        public void Update()
        {
            foreach (var p in _proxies.Values)
                p.Tick();
        }

        public void ClearAll()
        {
            foreach (var p in _proxies.Values)
                p.SetRemoteControlled(false);
            _proxies.Clear();
            _owners.Clear();
        }

        // ── Find vehicle in scene by type ─────────────────────────────────────

        private static NWH.Vehicle FindVehicleByType(Vehicle_Type type)
        {
            var gameplay = FusionModdingAPI.Module.Game.Gameplay;
            if (gameplay == null) return null;

            // Check mainVehicles array first
            if (gameplay.mainVehicles != null)
            {
                foreach (var vc in gameplay.mainVehicles)
                {
                    if (vc != null && vc.vehicle_type == type)
                        return vc;
                }
            }

            // Check buggy separately
            if (gameplay.buggyVehicle != null && gameplay.buggyVehicle.vehicle_type == type)
                return gameplay.buggyVehicle;

            return null;
        }
    }

    // ── CarProxy: smooth kinematic remote vehicle ─────────────────────────────

    public class CarProxy
    {
        private readonly NWH.Vehicle _vehicle;
        private readonly Rigidbody   _rb;

        public Vector3    TargetPosition;
        public Quaternion TargetRotation = Quaternion.identity;

        private const float POS_LERP     = 15f;
        private const float ROT_LERP     = 15f;
        private const float SNAP_DIST    = 8f;   // metres before we hard-snap

        public CarProxy(NWH.Vehicle vehicle)
        {
            _vehicle       = vehicle;
            _rb            = vehicle.vehicleRigidbody;
            TargetPosition = vehicle.vehicleTransform.position;
            TargetRotation = vehicle.vehicleTransform.rotation;
        }

        public void SetRemoteControlled(bool enabled)
        {
            if (_rb != null)
                _rb.isKinematic = enabled;
        }

        public void Tick()
        {
            if (_vehicle == null) return;
            var t = _vehicle.vehicleTransform;

            if (Vector3.Distance(t.position, TargetPosition) > SNAP_DIST)
            {
                t.position = TargetPosition;
                t.rotation = TargetRotation;
                return;
            }

            t.position = Vector3.Lerp(t.position, TargetPosition, Time.deltaTime * POS_LERP);
            t.rotation = Quaternion.Slerp(t.rotation, TargetRotation, Time.deltaTime * ROT_LERP);
        }
    }
}