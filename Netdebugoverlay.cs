using UnityEngine;

namespace MultiplayerMod
{
    /// <summary>
    /// Press F9 to toggle. Shows live connection stats, ping, packet counters,
    /// and a scrolling event log of network activity.
    /// </summary>
    public class NetDebugOverlay : MonoBehaviour
    {
        private bool _visible = false;

        private GUIStyle _boxStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _logStyle;
        private bool _stylesInit = false;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
                _visible = !_visible;
        }

        private void OnGUI()
        {
            if (!_visible) return;

            InitStyles();

            var net     = MultiplayerPlugin.Instance?.Network;
            var players = MultiplayerPlugin.Instance?.Players;
            if (net == null) return;

            float w = 340f, h = 320f;
            float x = Screen.width - w - 10f;
            float y = 10f;

            GUI.Box(new Rect(x - 6, y - 6, w + 12, h + 12), GUIContent.none, _boxStyle);

            GUILayout.BeginArea(new Rect(x, y, w, h));

            // ── Title ──────────────────────────────────────────────────────────
            GUILayout.Label("◈ MonBazou Multiplayer", _titleStyle);
            GUILayout.Space(4);

            // ── Connection status ──────────────────────────────────────────────
            var statusColor = net.IsConnected ? "#00ff88" : "#ff4444";
            GUILayout.Label($"<color={statusColor}>●</color> {net.StatusLine}", _labelStyle);
            GUILayout.Space(4);

            if (net.IsConnected)
            {
                // ── Identity ───────────────────────────────────────────────────
                GUILayout.Label($"<color=#aaaaaa>Local ID:</color>  #{net.LocalPlayerId}", _labelStyle);

                // ── Ping ───────────────────────────────────────────────────────
                string pingStr = net.PingMs < 0
                    ? "<color=#888888>measuring...</color>"
                    : PingColored(net.PingMs);
                GUILayout.Label($"<color=#aaaaaa>Ping:</color>      {pingStr}", _labelStyle);

                GUILayout.Space(4);

                // ── Traffic ────────────────────────────────────────────────────
                GUILayout.Label($"<color=#aaaaaa>Sent:</color>      {net.PacketsSent} pkts  /  {FormatBytes(net.BytesSent)}", _labelStyle);
                GUILayout.Label($"<color=#aaaaaa>Recv:</color>      {net.PacketsReceived} pkts  /  {FormatBytes(net.BytesReceived)}", _labelStyle);

                GUILayout.Space(4);

                // ── Players ────────────────────────────────────────────────────
                GUILayout.Label($"<color=#aaaaaa>Players:</color>   {(players?.Count ?? 0) + 1} online", _labelStyle);
                if (players != null)
                {
                    foreach (var p in players.All)
                        GUILayout.Label($"  <color=#66ccff>#{p.Id}</color> {p.Name}", _labelStyle);
                }

                GUILayout.Space(6);
            }

            // ── Event log ──────────────────────────────────────────────────────
            GUILayout.Label("<color=#ffcc44>── Event Log ──────────────</color>", _labelStyle);
            if (net != null)
            {
                foreach (var line in net.Log)
                    GUILayout.Label(line, _logStyle);
            }

            GUILayout.EndArea();
        }

        private static string PingColored(float ms)
        {
            string color = ms < 60 ? "#00ff88" : ms < 120 ? "#ffcc00" : "#ff4444";
            return $"<color={color}>{ms:0} ms</color>";
        }

        private static string FormatBytes(int bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:0.0} KB";
            return $"{bytes / (1024f * 1024f):0.0} MB";
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = MakeTex(1, 1, new Color(0.05f, 0.05f, 0.1f, 0.88f));

            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.fontSize  = 13;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.normal.textColor = Color.white;
            _titleStyle.richText = true;

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 11;
            _labelStyle.normal.textColor = Color.white;
            _labelStyle.richText = true;

            _logStyle = new GUIStyle(GUI.skin.label);
            _logStyle.fontSize = 10;
            _logStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            _logStyle.richText = true;
        }

        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var tex = new Texture2D(w, h);
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = col;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}