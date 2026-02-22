using NWH;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerMod
{
    /// <summary>
    /// Vehicle sync for remote players.
    ///
    /// KEY INSIGHT FROM DECOMPILE:
    ///   NWH.Vehicle already has SetMultiplayerInstanceType(instanceType, isKinematic)
    ///   and a MultiplayerInstanceType enum (Local / Remote). The game was built with
    ///   multiplayer in mind — use this instead of manually poking isKinematic.
    ///
    /// TWO CASES per incoming CarUpdate:
    ///   A) We own that Vehicle_Type locally → call SetMultiplayerInstanceType(Remote)
    ///      on the real NWH vehicle and lerp it to the network transform.
    ///   B) We don't own it → spawn a generic visual ghost car (box + wheels) and
    ///      lerp that instead. No NWH, no physics, purely visual.
    ///
    ///   In both cases the remote player's walking avatar is re-parented into the
    ///   car at the driver seat so they appear to be sitting inside it.
    ///
    /// DRIVER-EXIT:
    ///   2-second packet timeout → restore Local instance type / destroy ghost,
    ///   un-parent the avatar back to world space.
    /// </summary>
    public class CarSyncManager
    {
        // playerId → active driving state
        private readonly Dictionary<int, RemoteDriverState> _drivers = new();

        private const float DRIVE_TIMEOUT = 2.0f;

        // ── Incoming packet ───────────────────────────────────────────────────

        public void OnCarUpdate(int playerId, int carTypeInt,
            Vector3 position, Quaternion rotation, float speed)
        {
            if (!_drivers.TryGetValue(playerId, out var state))
            {
                state = CreateDriverState(playerId, carTypeInt, position, rotation);
                if (state == null) return;
                _drivers[playerId] = state;
            }
            else if (state.CarTypeInt != carTypeInt)
            {
                // Player switched vehicles mid-session
                DestroyDriverState(state);
                state = CreateDriverState(playerId, carTypeInt, position, rotation);
                if (state == null) return;
                _drivers[playerId] = state;
            }

            state.TargetPosition = position;
            state.TargetRotation = rotation;
            state.LastPacketTime = Time.realtimeSinceStartup;
        }

        // ── Player disconnect ─────────────────────────────────────────────────

        public void OnPlayerLeft(int playerId)
        {
            if (_drivers.TryGetValue(playerId, out var state))
            {
                DestroyDriverState(state);
                _drivers.Remove(playerId);
                MultiplayerPlugin.Log.LogInfo($"[Car] Driver state cleared for #{playerId}");
            }
        }

        // ── Per-frame update ──────────────────────────────────────────────────

        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            var timedOut = new List<int>();

            foreach (var kv in _drivers)
            {
                if (now - kv.Value.LastPacketTime > DRIVE_TIMEOUT)
                    timedOut.Add(kv.Key);
                else
                    kv.Value.Tick();
            }

            foreach (var pid in timedOut)
            {
                MultiplayerPlugin.Log.LogInfo($"[Car] #{pid} exited vehicle (timeout)");
                DestroyDriverState(_drivers[pid]);
                _drivers.Remove(pid);
            }
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        public void ClearAll()
        {
            foreach (var s in _drivers.Values)
                DestroyDriverState(s);
            _drivers.Clear();
        }

        // ── State factory ─────────────────────────────────────────────────────

        private RemoteDriverState CreateDriverState(int playerId, int carTypeInt,
            Vector3 spawnPos, Quaternion spawnRot)
        {
            var type         = (Vehicle_Type)carTypeInt;
            var localVehicle = FindLocalVehicle(type);
            var remotePlayer = MultiplayerPlugin.Instance.Players.GetPlayer(playerId);

            GameObject ghostCar   = null;
            bool       usingLocal = localVehicle != null;

            if (usingLocal)
            {
                // ── Case A: vehicle exists in our scene ───────────────────────
                // IMPORTANT: never take control of the vehicle the local player
                // is currently driving — that would lock them out of their own car.
                if (localVehicle == FusionModdingAPI.Module.Vehicle.CurrentVehicle)
                {
                    MultiplayerPlugin.Log.LogInfo(
                        $"[Car] #{playerId} claims {type} but we're driving it — spawning ghost instead");
                    usingLocal = false;
                    ghostCar   = BuildGhostCar(type, spawnPos, spawnRot);
                }
                else
                {
                    // Directly set kinematic — SetMultiplayerInstanceType is abstract
                    // in the base class and its concrete implementation may disable
                    // engine input / wheel physics in ways we don't want.
                    var rb = localVehicle.vehicleRigidbody;
                    if (rb != null) rb.isKinematic = true;

                    localVehicle.vehicleTransform.position = spawnPos;
                    localVehicle.vehicleTransform.rotation = spawnRot;

                    MultiplayerPlugin.Log.LogInfo(
                        $"[Car] #{playerId} → local {type} kinematic");
                }
            }
            else
            {
                // ── Case B: we don't own this vehicle ─────────────────────────
                // Spawn a generic ghost car so the remote player is visible.
                ghostCar = BuildGhostCar(type, spawnPos, spawnRot);

                MultiplayerPlugin.Log.LogInfo(
                    $"[Car] #{playerId} → ghost car spawned for {type}");
            }

            // Seat the remote player's avatar inside the car
            if (remotePlayer?.RootObject != null)
            {
                var seat = usingLocal
                    ? localVehicle.vehicleTransform
                    : ghostCar?.transform;

                if (seat != null)
                {
                    remotePlayer.RootObject.transform.SetParent(seat, false);
                    remotePlayer.RootObject.transform.localPosition =
                        new Vector3(-0.35f, 0.55f, 0.1f);
                    remotePlayer.RootObject.transform.localRotation = Quaternion.identity;
                    remotePlayer.RootObject.transform.localScale    = Vector3.one * 0.6f;
                    remotePlayer.IsSeated = true;   // stop Tick() from fighting the parent
                }
            }

            return new RemoteDriverState(
                playerId, carTypeInt,
                localVehicle, ghostCar, usingLocal,
                remotePlayer, spawnPos, spawnRot);
        }

        private void DestroyDriverState(RemoteDriverState state)
        {
            // Restore avatar to world space and resume normal lerping
            if (state.RemotePlayer?.RootObject != null)
            {
                var avatar = state.RemotePlayer.RootObject;
                // worldPositionStays=true keeps it where the car left it visually
                avatar.transform.SetParent(null, worldPositionStays: true);
                avatar.transform.localScale = Vector3.one;
                // Snap TargetPosition to current world position so Tick() doesn't
                // suddenly lerp from a stale pre-car position back to the player
                state.RemotePlayer.TargetPosition = avatar.transform.position;
                state.RemotePlayer.IsSeated       = false;
            }

            if (state.UsingLocal && state.LocalVehicle != null)
            {
                var rb = state.LocalVehicle.vehicleRigidbody;
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.WakeUp();
                }
            }

            if (state.GhostCar != null)
                GameObject.Destroy(state.GhostCar);
        }

        // ── Generic ghost car ─────────────────────────────────────────────────

        private static GameObject BuildGhostCar(Vehicle_Type type, Vector3 pos, Quaternion rot)
        {
            var root = new GameObject($"[MP_GhostCar] {type}");
            root.transform.position = pos;
            root.transform.rotation = rot;

            // body: (width, height, length)  wheels: radius, axleHalfWidth, frontZ, rearZ
            // All measurements approximate but distinguishable at a glance.
            switch (type)
            {
                case Vehicle_Type.MainVehicle:
                case Vehicle_Type.ScrapyardKonig:
                    // Konig — boxy 80s sedan, medium size
                    BuildBody(root, new Vector3(1.75f, 1.35f, 4.4f), new Color(0.55f, 0.18f, 0.12f));
                    BuildRoof(root, new Vector3(1.55f, 0.60f, 2.3f), new Vector3(0f, 1.58f, -0.15f), new Color(0.45f, 0.14f, 0.10f));
                    BuildWheels(root, radius: 0.32f, axleW: 0.97f, frontZ: 1.45f, rearZ: -1.45f);
                    break;

                case Vehicle_Type.OlTruck:
                    // Old pickup truck — tall cab, long bed, bigger wheels
                    BuildBody(root, new Vector3(1.95f, 1.55f, 5.2f), new Color(0.35f, 0.28f, 0.18f));
                    BuildRoof(root, new Vector3(1.75f, 0.70f, 2.0f), new Vector3(0f, 1.85f, 0.5f),  new Color(0.28f, 0.22f, 0.14f));
                    BuildWheels(root, radius: 0.40f, axleW: 1.05f, frontZ: 1.7f, rearZ: -1.5f);
                    break;

                case Vehicle_Type.Buggy:
                    // Small open buggy — no roof, wide stance
                    BuildBody(root, new Vector3(1.6f, 0.65f, 2.4f), new Color(0.55f, 0.42f, 0.08f));
                    BuildWheels(root, radius: 0.38f, axleW: 1.0f, frontZ: 0.85f, rearZ: -0.85f);
                    break;

                case Vehicle_Type.Racecar:
                    // Low, wide, long
                    BuildBody(root, new Vector3(1.9f, 0.75f, 4.6f), new Color(0.8f, 0.08f, 0.08f));
                    BuildRoof(root, new Vector3(1.3f, 0.45f, 1.5f), new Vector3(0f, 1.0f, 0.1f),   new Color(0.6f, 0.06f, 0.06f));
                    BuildWheels(root, radius: 0.30f, axleW: 1.05f, frontZ: 1.6f, rearZ: -1.6f);
                    break;

                case Vehicle_Type.SmollATV:
                    // Tiny quad — short and stubby
                    BuildBody(root, new Vector3(1.1f, 0.55f, 1.6f), new Color(0.15f, 0.35f, 0.15f));
                    BuildWheels(root, radius: 0.30f, axleW: 0.65f, frontZ: 0.6f, rearZ: -0.6f);
                    break;

                case Vehicle_Type.FishingBoat:
                    // Long flat hull — no roof, no wheels (it's a boat)
                    BuildBody(root, new Vector3(2.0f, 0.7f, 5.5f), new Color(0.15f, 0.25f, 0.55f));
                    break;

                default:
                    // Generic fallback for trailers, LeMissile, SmallCruiser, etc.
                    BuildBody(root, new Vector3(1.8f, 1.2f, 4.0f), new Color(0.25f, 0.25f, 0.28f));
                    BuildRoof(root, new Vector3(1.6f, 0.55f, 2.0f), new Vector3(0f, 1.50f, -0.1f), new Color(0.2f, 0.2f, 0.22f));
                    BuildWheels(root, radius: 0.33f, axleW: 0.95f, frontZ: 1.3f, rearZ: -1.3f);
                    break;
            }

            return root;
        }

        private static void BuildBody(GameObject root, Vector3 scale, Color color)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject.Destroy(body.GetComponent<BoxCollider>());
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, scale.y * 0.5f, 0f);
            body.transform.localScale    = scale;
            ApplyColor(body, color);
        }

        private static void BuildRoof(GameObject root, Vector3 scale, Vector3 localPos, Color color)
        {
            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObject.Destroy(roof.GetComponent<BoxCollider>());
            roof.transform.SetParent(root.transform, false);
            roof.transform.localPosition = localPos;
            roof.transform.localScale    = scale;
            ApplyColor(roof, color);
        }

        private static void BuildWheels(GameObject root, float radius, float axleW, float frontZ, float rearZ)
        {
            SpawnWheel(root.transform, new Vector3( axleW, radius, frontZ), radius);
            SpawnWheel(root.transform, new Vector3(-axleW, radius, frontZ), radius);
            SpawnWheel(root.transform, new Vector3( axleW, radius, rearZ),  radius);
            SpawnWheel(root.transform, new Vector3(-axleW, radius, rearZ),  radius);
        }

        private static void SpawnWheel(Transform parent, Vector3 localPos, float radius)
        {
            var w = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            GameObject.Destroy(w.GetComponent<CapsuleCollider>());
            w.transform.SetParent(parent, false);
            w.transform.localPosition = localPos;
            w.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            w.transform.localScale    = new Vector3(radius * 2f, 0.12f, radius * 2f);
            ApplyColor(w, new Color(0.08f, 0.08f, 0.08f));
        }

        private static void ApplyColor(GameObject go, Color color)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            var mat = new Material(Shader.Find("Standard"));
            mat.color = color;
            r.material = mat;
        }

        // ── Vehicle lookup ────────────────────────────────────────────────────

        private static NWH.Vehicle FindLocalVehicle(Vehicle_Type type)
        {
            if (type == Vehicle_Type.Useless   ||
                type == Vehicle_Type.PitBike   ||
                type == Vehicle_Type.AI_Vehicle)
                return null;

            var vehicles = FusionModdingAPI.Module.Vehicle.Vehicles;
            if (vehicles == null) return null;

            foreach (var v in vehicles)
                if (v != null && v.vehicle_type == type) return v;

            return null;
        }
    }

    // ── RemoteDriverState ─────────────────────────────────────────────────────

    public class RemoteDriverState
    {
        public int          PlayerId     { get; }
        public int          CarTypeInt   { get; }
        public NWH.Vehicle  LocalVehicle { get; }
        public GameObject   GhostCar     { get; }
        public bool         UsingLocal   { get; }
        public RemotePlayer RemotePlayer { get; }

        public Vector3    TargetPosition;
        public Quaternion TargetRotation = Quaternion.identity;
        public float      LastPacketTime;

        private const float POS_LERP  = 12f;
        private const float ROT_LERP  = 12f;
        private const float SNAP_DIST = 10f;

        public RemoteDriverState(int playerId, int carTypeInt,
            NWH.Vehicle localVehicle, GameObject ghostCar, bool usingLocal,
            RemotePlayer remotePlayer, Vector3 startPos, Quaternion startRot)
        {
            PlayerId       = playerId;
            CarTypeInt     = carTypeInt;
            LocalVehicle   = localVehicle;
            GhostCar       = ghostCar;
            UsingLocal     = usingLocal;
            RemotePlayer   = remotePlayer;
            TargetPosition = startPos;
            TargetRotation = startRot;
            LastPacketTime = Time.realtimeSinceStartup;
        }

        public void Tick()
        {
            var t = UsingLocal
                ? LocalVehicle?.vehicleTransform
                : GhostCar?.transform;

            if (t == null) return;

            if (Vector3.Distance(t.position, TargetPosition) > SNAP_DIST)
            {
                t.position = TargetPosition;
                t.rotation = TargetRotation;
                if (UsingLocal && LocalVehicle?.vehicleRigidbody != null)
                    LocalVehicle.vehicleRigidbody.velocity = Vector3.zero;
                return;
            }

            t.position = Vector3.Lerp(t.position, TargetPosition, Time.deltaTime * POS_LERP);
            t.rotation = Quaternion.Slerp(t.rotation, TargetRotation, Time.deltaTime * ROT_LERP);
        }
    }
}