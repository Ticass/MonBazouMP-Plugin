// carSyncManager.cs
// HOST: reads every vehicle transform each tick, broadcasts.
// GUEST: receives, finds matching vehicle, drives it kinematically via FixedUpdate.
// Ghost visibility: driver ghost BODY hidden, nametag floats above car roof.
using NWH;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerMod
{
    public class CarSyncManager
    {
        // Guest only: one entry per vehicle type we're tracking
        private readonly Dictionary<int, VehicleEntry> _entries =
            new Dictionary<int, VehicleEntry>();

        // ─────────────────────────────────────────────────────────────────────
        // HOST — called every network tick (20 Hz) by NetworkClient
        // Iterates ALL vehicles via LocalPlayerTracker.AllVehicles (already
        // populated by LocalPlayerPatch every frame) and builds one packet each.
        // ─────────────────────────────────────────────────────────────────────
        public List<byte[]> BuildHostBroadcasts()
        {
            var out_ = new List<byte[]>();

            // Who is the local player driving right now?
            int localId  = MultiplayerPlugin.Instance.Network.LocalPlayerId;
            int inCarType = LocalPlayerTracker.IsInCar
                            ? (int)LocalPlayerTracker.CurrentCarType
                            : -1;

            var vehicles = LocalPlayerTracker.AllVehicles;
            if (vehicles == null || vehicles.Count == 0) return out_;

            var seen = new HashSet<int>();
            foreach (var v in vehicles)
            {
                if (v == null) continue;
                int vt = (int)v.vehicle_type;
                if (!seen.Add(vt)) continue; // deduplicate

                var t  = v.vehicleTransform;
                var rb = v.vehicleRigidbody;
                if (t == null) continue;

                Vector3 vel    = rb != null ? rb.velocity        : Vector3.zero;
                Vector3 angVel = rb != null ? rb.angularVelocity : Vector3.zero;

                // Mark which vehicle the host is driving
                int driverId = (vt == inCarType) ? localId : -1;

                out_.Add(PacketWriter.WriteVehicleState(
                    vt, t.position, t.rotation, vel, angVel, v.Speed,
                    driverId, -1));
            }

            return out_;
        }

        // ─────────────────────────────────────────────────────────────────────
        // GUEST — called from NetworkClient on main thread when packet arrives
        // ─────────────────────────────────────────────────────────────────────
        public void OnVehicleState(PacketReader.VehicleState s)
        {
            var entry = GetOrCreate(s.vt);
            if (entry == null) return;

            entry.TargetPos   = s.pos;
            entry.TargetRot   = s.rot;
            entry.TargetVel   = s.vel;
            entry.TargetAng   = s.angVel;
            entry.Speed       = s.speed;
            entry.ReceivedAt  = Time.time;
            entry.HasData     = true;

            bool changed = entry.DriverId    != s.driverId
                        || entry.PassengerId != s.passengerId;
            entry.DriverId    = s.driverId;
            entry.PassengerId = s.passengerId;

            if (changed) RefreshGhosts(entry);
        }

        public void OnPlayerLeft(int playerId)
        {
            foreach (var e in _entries.Values)
            {
                bool changed = false;
                if (e.DriverId    == playerId) { e.DriverId    = -1; changed = true; }
                if (e.PassengerId == playerId) { e.PassengerId = -1; changed = true; }
                if (changed) RefreshGhosts(e);
            }
        }

        public void Update() { /* kinematic work is in FixedUpdate via MonoBehaviour */ }

        public void ClearAll()
        {
            foreach (var e in _entries.Values) Teardown(e);
            _entries.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Entry lifecycle
        // ─────────────────────────────────────────────────────────────────────
        private VehicleEntry GetOrCreate(int vt)
        {
            if (_entries.TryGetValue(vt, out var existing))
            {
                if (existing.Vehicle != null) return existing;
                Teardown(existing);
            }

            // Find the vehicle in the guest's own scene
            NWH.Vehicle vehicle = null;
            foreach (var v in LocalPlayerTracker.AllVehicles)
            {
                if (v != null && (int)v.vehicle_type == vt)
                {
                    vehicle = v;
                    break;
                }
            }

            if (vehicle == null)
            {
                // Scene not ready yet — don't spam log
                return null;
            }

            // Make this vehicle kinematic: we drive it, not NWH physics
            var rb = vehicle.vehicleRigidbody;
            if (rb != null)
            {
                rb.isKinematic   = true;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
            }

            var entry = new VehicleEntry(vehicle);

            // Attach a MonoBehaviour to the vehicle's GO so FixedUpdate works correctly
            var driver = vehicle.vehicleTransform.gameObject
                             .AddComponent<KinematicDriver>();
            driver.Entry = entry;
            entry.Driver = driver;

            _entries[vt] = entry;
            MultiplayerPlugin.Log.LogInfo(
                $"[Cars] Tracking vt={vt} '{vehicle.vehicleTransform.name}'");
            return entry;
        }

        private static void Teardown(VehicleEntry e)
        {
            if (e.Driver != null)
            {
                Object.Destroy(e.Driver);
                e.Driver = null;
            }
            var rb = e.Vehicle?.vehicleRigidbody;
            if (rb != null)
            {
                rb.isKinematic   = false;
                rb.interpolation = RigidbodyInterpolation.None;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Ghost visibility
        //
        // Driver ghost:
        //   Body + head renderers DISABLED. The car itself shows where they are.
        //   The nametag (child "Nametag" GO) is re-parented to the vehicle and
        //   floated above it so you can see who's driving.
        //
        // Passenger ghost (future): body visible, parented to seat bone.
        // ─────────────────────────────────────────────────────────────────────
        private static void RefreshGhosts(VehicleEntry entry)
        {
            var players = MultiplayerPlugin.Instance.Players;
            int localId = MultiplayerPlugin.Instance.Network.LocalPlayerId;

            HandleDriver   (entry, players, localId);
            HandlePassenger(entry, players, localId);
        }

        private static void HandleDriver(VehicleEntry entry, PlayerManager players, int localId)
        {
            int driverId = entry.DriverId;
            if (driverId < 0 || driverId == localId) return;

            var remote = players.GetPlayer(driverId);
            if (remote?.RootObject == null) return;

            // Hide body/head geometry but keep nametag
            SetBodyRenderersEnabled(remote.RootObject, false);
            remote.IsSeated = true; // stop Tick() from repositioning this ghost

            // Float the nametag above the vehicle roof
            // Find the "Nametag" child of the ghost and re-parent it to the vehicle
            var vehicleTf = entry.Vehicle?.vehicleTransform;
            if (vehicleTf == null) return;

            var nametagGo = FindChild(remote.RootObject.transform, "Nametag");
            if (nametagGo != null)
            {
                nametagGo.SetParent(vehicleTf, false);
                nametagGo.localPosition = new Vector3(0f, 2.3f, 0f);
                nametagGo.localRotation = Quaternion.identity;
            }
        }

        private static void HandlePassenger(VehicleEntry entry, PlayerManager players, int localId)
        {
            int passId = entry.PassengerId;
            if (passId < 0 || passId == localId) return;

            var remote = players.GetPlayer(passId);
            if (remote?.RootObject == null) return;

            SetBodyRenderersEnabled(remote.RootObject, true);
            remote.IsSeated = true;

            // Try to parent to passenger seat bone
            var vehicleTf = entry.Vehicle?.vehicleTransform;
            if (vehicleTf == null) return;

            Transform seat = FindDeep(vehicleTf, "Location_SeatRight")
                           ?? vehicleTf;

            var t = remote.RootObject.transform;
            t.SetParent(seat, false);
            // Ghost pivot is at its feet; seat bone origin is at seat-cushion level.
            // Offset down by 1 m so the ghost appears seated rather than hovering.
            t.localPosition = new Vector3(0f, -1f, 0.1f);
            t.localRotation = Quaternion.identity;
        }

        // Disable every MeshRenderer/SkinnedMeshRenderer on the ghost except the nametag TextMesh
        private static void SetBodyRenderersEnabled(GameObject root, bool enabled)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.GetComponent<TextMesh>() != null) continue; // keep nametag
                r.enabled = enabled;
            }
        }

        private static Transform FindChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c.name == name) return c;
            }
            return null;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-vehicle state on the guest
    // ─────────────────────────────────────────────────────────────────────────
    public class VehicleEntry
    {
        public readonly NWH.Vehicle Vehicle;
        public KinematicDriver Driver;

        // Latest authoritative state from host
        public Vector3    TargetPos;
        public Quaternion TargetRot  = Quaternion.identity;
        public Vector3    TargetVel;
        public Vector3    TargetAng;
        public float      Speed;
        public float      ReceivedAt;
        public bool       HasData;

        public int DriverId    = -1;
        public int PassengerId = -1;

        public VehicleEntry(NWH.Vehicle v)
        {
            Vehicle   = v;
            TargetPos = v.vehicleTransform.position;
            TargetRot = v.vehicleTransform.rotation;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MonoBehaviour attached to vehicle's own GameObject on the guest.
    // Must live on the vehicle GO so Unity's FixedUpdate timing is correct for
    // Rigidbody.MovePosition / MoveRotation.
    // ─────────────────────────────────────────────────────────────────────────
    public class KinematicDriver : MonoBehaviour
    {
        public VehicleEntry Entry;

        // Snap thresholds — if we're further than this just teleport
        private const float SNAP_DIST_SQ = 10f * 10f; // 10 m
        private const float SNAP_ANGLE   = 40f;        // degrees

        // Exponential smoothing gains (higher = snappier)
        private const float POS_GAIN = 18f;
        private const float ROT_GAIN = 18f;
        private const float VEL_GAIN = 12f;

        // Max extrapolation window — don't extrapolate more than 200 ms
        private const float MAX_EXTRAP = 0.2f;

        private void FixedUpdate()
        {
            if (Entry == null || !Entry.HasData) return;

            var rb = Entry.Vehicle?.vehicleRigidbody;
            var tf = Entry.Vehicle?.vehicleTransform;
            if (rb == null || tf == null) return;

            float dt  = Time.fixedDeltaTime;
            float age = Mathf.Min(Time.time - Entry.ReceivedAt, MAX_EXTRAP);

            // Dead-reckoning: where should the car be right now?
            Vector3 predicted = Entry.TargetPos + Entry.TargetVel * age;

            float distSq = (tf.position - predicted).sqrMagnitude;
            float angle  = Quaternion.Angle(tf.rotation, Entry.TargetRot);

            if (distSq > SNAP_DIST_SQ || angle > SNAP_ANGLE)
            {
                // Hard snap — teleport is better than sliding forever
                rb.MovePosition(predicted);
                rb.MoveRotation(Entry.TargetRot);
                rb.velocity        = Entry.TargetVel;
                rb.angularVelocity = Entry.TargetAng;
                return;
            }

            // Smooth exponential approach toward predicted state
            float ap = 1f - Mathf.Exp(-POS_GAIN * dt);
            float ar = 1f - Mathf.Exp(-ROT_GAIN * dt);
            float av = 1f - Mathf.Exp(-VEL_GAIN * dt);

            rb.MovePosition(Vector3.Lerp(tf.position, predicted,        ap));
            rb.MoveRotation(Quaternion.Slerp(tf.rotation, Entry.TargetRot, ar));
            rb.velocity        = Vector3.Lerp(rb.velocity,        Entry.TargetVel, av);
            rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, Entry.TargetAng, av);
        }
    }
}