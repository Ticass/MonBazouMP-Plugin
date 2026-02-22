using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerMod
{
    public class PlayerManager
    {
        private readonly Dictionary<int, RemotePlayer> _remotePlayers = new Dictionary<int, RemotePlayer>();

        public void AddRemotePlayer(int id, string name)
        {
            if (_remotePlayers.ContainsKey(id))
            {
                MultiplayerPlugin.Log.LogWarning($"[MP] AddRemotePlayer called twice for id={id}, ignoring");
                return;
            }

            var go = BuildGhostObject(name);
            // Place underground until the first real position arrives so it
            // doesn't briefly pop in at world origin, but keep it ACTIVE so
            // SetActive(true) in UpdateRemotePlayer is never the gating factor.
            go.transform.position = new Vector3(0f, -1000f, 0f);
            go.SetActive(true);
            var rp = new RemotePlayer(id, name, go);
            _remotePlayers[id] = rp;
            MultiplayerPlugin.Log.LogInfo($"[MP] Ghost created for '{name}' (id={id})");
        }

        public void RemoveRemotePlayer(int id)
        {
            if (_remotePlayers.TryGetValue(id, out var remote))
            {
                GameObject.Destroy(remote.RootObject);
                _remotePlayers.Remove(id);
                MultiplayerPlugin.Log.LogInfo($"[MP] Ghost destroyed for id={id}");
            }
        }

        public void UpdateRemotePlayer(int id, Vector3 position, Vector3 eulerAngles)
        {
            if (!_remotePlayers.TryGetValue(id, out var remote))
            {
                // Player moved before we got their join — create a ghost on the fly
                MultiplayerPlugin.Log.LogWarning($"[MP] Got move for unknown id={id}, auto-spawning ghost");
                AddRemotePlayer(id, $"Player#{id}");
                if (!_remotePlayers.TryGetValue(id, out remote)) return;
            }

            // First valid position: hard-snap so the ghost doesn't lerp up from -1000
            if (!remote.HasReceivedPosition)
            {
                remote.RootObject.transform.position = position;
                remote.HasReceivedPosition = true;
                MultiplayerPlugin.Log.LogInfo($"[MP] Ghost #{id} first position: {position}");
            }

            remote.TargetPosition = position;
            remote.TargetRotation = Quaternion.Euler(eulerAngles);
        }

        public string GetName(int id) =>
            _remotePlayers.TryGetValue(id, out var p) ? p.Name : $"Player#{id}";

        public RemotePlayer GetPlayer(int id) =>
            _remotePlayers.TryGetValue(id, out var p) ? p : null;

        public int Count => _remotePlayers.Count;

        public IEnumerable<RemotePlayer> All => _remotePlayers.Values;

        public void Update()
        {
            foreach (var kv in _remotePlayers)
                kv.Value.Tick();
        }

        public void ClearAll()
        {
            foreach (var kv in _remotePlayers)
                if (kv.Value.RootObject != null)
                    GameObject.Destroy(kv.Value.RootObject);
            _remotePlayers.Clear();
        }

        // ── Ghost builder ─────────────────────────────────────────────────────

        private static GameObject BuildGhostObject(string playerName)
        {
            var root = new GameObject($"[MP] {playerName}");

            // Body
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);
            body.transform.localScale    = new Vector3(0.5f, 0.9f, 0.5f);
            GameObject.Destroy(body.GetComponent<CapsuleCollider>());
            ApplyColor(body, new Color(0.2f, 0.6f, 1f, 1f));

            // Head
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0f, 2.15f, 0f);
            head.transform.localScale    = Vector3.one * 0.45f;
            GameObject.Destroy(head.GetComponent<SphereCollider>());
            ApplyColor(head, new Color(0.9f, 0.75f, 0.6f, 1f));

            // Nametag
            var tag = new GameObject("Nametag");
            tag.transform.SetParent(root.transform, false);
            tag.transform.localPosition = new Vector3(0f, 2.8f, 0f);
            var tm       = tag.AddComponent<TextMesh>();
            tm.text          = playerName;
            tm.fontSize      = 48;
            tm.characterSize = 0.06f;
            tm.anchor        = TextAnchor.LowerCenter;
            tm.alignment     = TextAlignment.Center;
            tm.color         = Color.white;
            tag.AddComponent<NametagBillboard>();

            return root;
        }

        // Use a fully opaque material — transparent materials sometimes don't
        // render correctly if the shader isn't set up exactly right
        private static void ApplyColor(GameObject go, Color color)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            var mat = new Material(Shader.Find("Standard"));
            mat.color = color;
            r.material = mat;
        }
    }

    // ── RemotePlayer data ─────────────────────────────────────────────────────

    public class RemotePlayer
    {
        public int        Id                  { get; }
        public string     Name                { get; }
        public GameObject RootObject          { get; }
        public bool       HasReceivedPosition { get; set; }

        // When true the ghost is parented inside a car — Tick() must NOT move it
        // because the car transform already carries it. Moving it in world space
        // while parented causes the rubber-band-to-origin bug.
        public bool IsSeated { get; set; }

        public Vector3    TargetPosition;
        public Quaternion TargetRotation = Quaternion.identity;

        private const float LERP_POS = 10f;
        private const float LERP_ROT = 10f;

        public RemotePlayer(int id, string name, GameObject go)
        {
            Id = id; Name = name; RootObject = go;
        }

        public void Tick()
        {
            if (RootObject == null || !HasReceivedPosition) return;

            // If seated inside a car the parent transform moves us — don't fight it.
            if (IsSeated) return;

            var t = RootObject.transform;
            t.position = Vector3.Lerp(t.position, TargetPosition, Time.deltaTime * LERP_POS);
            t.rotation = Quaternion.Slerp(t.rotation, TargetRotation, Time.deltaTime * LERP_ROT);
        }
    }

    // ── Billboard nametag ─────────────────────────────────────────────────────

    public class NametagBillboard : MonoBehaviour
    {
        private void LateUpdate()
        {
            if (Camera.main == null) return;
            transform.forward = Camera.main.transform.forward;
        }
    }
}