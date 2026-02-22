// NetworkClient.cs — TCP connection, framing, packet dispatch.
// All public APIs are consistent with the new CarSyncManager and WorldStateManager.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace MultiplayerMod
{
    public class NetworkClient
    {
        private TcpClient     _tcp;
        private NetworkStream _stream;
        private Thread        _receiveThread;
        private readonly object _sendLock = new object();

        public bool   IsConnected   => _tcp != null && _tcp.Connected;
        public int    LocalPlayerId { get; private set; } = -1;

        // ── Debug stats ───────────────────────────────────────────────────────
        public int   PacketsSent     { get; private set; }
        public int   PacketsReceived { get; private set; }
        public int   BytesSent       { get; private set; }
        public int   BytesReceived   { get; private set; }
        public float PingMs          { get; private set; } = -1f;
        public string StatusLine     { get; private set; } = "Disconnected";

        private readonly Queue<string> _log = new Queue<string>();
        private const int LOG_MAX = 14;
        public IEnumerable<string> Log => _log;

        // ── Threading ─────────────────────────────────────────────────────────
        private readonly ConcurrentQueue<byte[]> _incoming = new ConcurrentQueue<byte[]>();

        // ── Sub-systems ───────────────────────────────────────────────────────
        private readonly PlayerManager     _pm;
        private readonly CarSyncManager    _cars;
        private readonly WorldStateManager _world;

        // ── Send timers ───────────────────────────────────────────────────────
        private float _moveTimer;
        private float _carTimer;
        private float _pingTimer;
        private const float MOVE_HZ    = 0.05f; // 20 Hz
        private const float CAR_HZ     = 0.05f; // 20 Hz
        private const float PING_EVERY = 3f;
        private long _pingSentAt;

        public NetworkClient(PlayerManager pm, CarSyncManager cars, WorldStateManager world)
        {
            _pm    = pm;
            _cars  = cars;
            _world = world;
        }

        // ── Connection ────────────────────────────────────────────────────────
        public void Connect(string ip, int port, string name)
        {
            try
            {
                StatusLine = $"Connecting to {ip}:{port}…";
                _tcp = new TcpClient();
                _tcp.Connect(ip, port);
                _stream = _tcp.GetStream();
                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                _receiveThread.Start();
                SendRaw(PacketWriter.WriteHandshake(name));
                AddLog($"Handshake → '{name}'");
                StatusLine = $"Connected ({ip}:{port}) — awaiting role…";
            }
            catch (Exception ex)
            {
                StatusLine = $"Connect failed: {ex.Message}";
                MultiplayerPlugin.Log.LogError($"[Net] Connect: {ex.Message}");
                _tcp = null;
            }
        }

        public void Disconnect()
        {
            try { _tcp?.Close(); } catch { }
            _tcp          = null;
            LocalPlayerId = -1;
            StatusLine    = "Disconnected";
            PingMs        = -1f;
            _pm.ClearAll();
            _cars.ClearAll();
            AddLog("Disconnected");
        }

        // ── Main-thread update ────────────────────────────────────────────────
        public void Update()
        {
            // Drain incoming queue — must happen before send timers so identity
            // packets (AssignId, RoleAssign) are processed before we try to send
            // player-move or vehicle packets that require LocalPlayerId.
            while (_incoming.TryDequeue(out var pkt))
                Dispatch(pkt);

            if (!IsConnected || LocalPlayerId < 0) return;

            // On-foot position broadcast
            _moveTimer += Time.deltaTime;
            if (_moveTimer >= MOVE_HZ)
            {
                _moveTimer = 0f;
                SendMove();
            }

            // Vehicle state broadcast (host only)
            _carTimer += Time.deltaTime;
            if (_carTimer >= CAR_HZ)
            {
                _carTimer = 0f;
                SendVehicles();
            }

            // Ping
            _pingTimer += Time.deltaTime;
            if (_pingTimer >= PING_EVERY)
            {
                _pingTimer  = 0f;
                _pingSentAt = Stopwatch.GetTimestamp();
                SendRaw(PacketWriter.WritePing(_pingSentAt));
            }
        }

        // ── Public helpers ────────────────────────────────────────────────────

        // WorldStateManager uses this to send SleepRequest without accessing _stream.
        // Accepts an already-framed packet (Frame() already called).
        public void SendRawPublic(byte[] framed) => WriteFramed(framed);

        // ── Senders ───────────────────────────────────────────────────────────
        private void SendMove()
        {
            if (!LocalPlayerTracker.IsReady) return;
            // When driving, our position is carried by the VehicleState packet.
            // Still send a PlayerMove so the guest sees us somewhere reasonable
            // if they miss vehicle packets — use car position when in car.
            var pos = LocalPlayerTracker.Position;
            if (pos == Vector3.zero) return;
            float yaw = LocalPlayerTracker.Rotation.y;
            SendRaw(PacketWriter.WritePlayerMove(LocalPlayerId, pos.x, pos.y, pos.z, yaw));
        }

        private void SendVehicles()
        {
            if (MultiplayerPlugin.Instance.Role != PlayerRole.Host) return;
            var pkts = _cars.BuildHostBroadcasts();
            foreach (var p in pkts) SendRaw(p);
        }

        private void SendRaw(byte[] payload) => WriteFramed(PacketWriter.Frame(payload));

        private void WriteFramed(byte[] framed)
        {
            if (!IsConnected) return;
            try
            {
                lock (_sendLock) _stream.Write(framed, 0, framed.Length);
                PacketsSent++;
                BytesSent += framed.Length;
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Log.LogWarning($"[Net] Send: {ex.Message}");
            }
        }

        // ── Receive thread ────────────────────────────────────────────────────
        private void ReceiveLoop()
        {
            var lenBuf = new byte[4];
            try
            {
                while (_tcp != null && _tcp.Connected)
                {
                    ReadExact(_stream, lenBuf, 4);
                    int len = BitConverter.ToInt32(lenBuf, 0);
                    if (len <= 0 || len > 1024 * 1024) break; // sanity
                    var data = new byte[len];
                    ReadExact(_stream, data, len);
                    PacketsReceived++;
                    BytesReceived += 4 + len;
                    _incoming.Enqueue(data);
                }
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Log.LogWarning($"[Net] Receive ended: {ex.Message}");
                StatusLine = "Disconnected (connection lost)";
            }
        }

        // ── Packet dispatch (main thread only) ────────────────────────────────
        private void Dispatch(byte[] data)
        {
            try { DispatchInner(data); }
            catch (Exception ex)
            {
                MultiplayerPlugin.Log.LogError(
                    $"[Net] Dispatch error on type=0x{data[0]:X2}: {ex}");
            }
        }

        private void DispatchInner(byte[] data)
        {
            switch (PacketReader.PeekType(data))
            {
                // ─── Identity ──────────────────────────────────────────────
                case PacketType.AssignId:
                {
                    LocalPlayerId = PacketReader.ReadAssignId(data);
                    AddLog($"✓ ID #{LocalPlayerId}");
                    break;
                }

                case PacketType.RoleAssign:
                {
                    var role = PacketReader.ReadRoleAssign(data);
                    MultiplayerPlugin.Instance.SetRole(role);
                    StatusLine = $"Connected — {role} (ID #{LocalPlayerId})";
                    AddLog($"✓ Role: {role}");
                    break;
                }

                // ─── Players ───────────────────────────────────────────────
                case PacketType.PlayerJoin:
                {
                    var (id, name, role) = PacketReader.ReadPlayerJoin(data);
                    if (id == LocalPlayerId) break; // our own join echo
                    _pm.AddRemotePlayer(id, name);
                    AddLog($"+ {name} #{id} ({role})");
                    break;
                }

                case PacketType.PlayerLeave:
                {
                    int id   = PacketReader.ReadPlayerLeave(data);
                    string n = _pm.GetName(id);
                    _pm.RemoveRemotePlayer(id);
                    _cars.OnPlayerLeft(id);
                    AddLog($"- {n} #{id}");
                    break;
                }

                case PacketType.PlayerMove:
                {
                    var (id, pos, yaw) = PacketReader.ReadPlayerMove(data);
                    if (id == LocalPlayerId) break;
                    _pm.UpdateRemotePlayer(id, pos, new Vector3(0f, yaw, 0f));
                    break;
                }

                // ─── Vehicles ─────────────────────────────────────────────
                case PacketType.VehicleState:
                {
                    // Only guests apply vehicle state — host is the authority
                    if (MultiplayerPlugin.Instance.Role == PlayerRole.Host) break;
                    var s = PacketReader.ReadVehicleState(data);
                    _cars.OnVehicleState(s);
                    break;
                }

                // ─── Time ─────────────────────────────────────────────────
                case PacketType.TimeSync:
                {
                    var (h, m, d) = PacketReader.ReadTimeSync(data);
                    _world.ApplyTimeSync(h, m, d);
                    AddLog($"⏱ Time → {h:D2}:{m:D2} day={d}");
                    break;
                }

                // ─── Ping / Pong ───────────────────────────────────────────
                case PacketType.Ping:
                {
                    long ts = PacketReader.ReadPingPong(data);
                    SendRaw(PacketWriter.WritePong(ts));
                    break;
                }

                case PacketType.Pong:
                {
                    long ts = PacketReader.ReadPingPong(data);
                    long freq = Stopwatch.Frequency;
                    PingMs = (float)(ts - _pingSentAt) / freq * 1000f;
                    // ts echoes our own timestamp, so use _pingSentAt
                    PingMs = (float)(Stopwatch.GetTimestamp() - _pingSentAt)
                             / Stopwatch.Frequency * 1000f;
                    break;
                }

                default:
                    MultiplayerPlugin.Log.LogWarning(
                        $"[Net] Unknown packet 0x{data[0]:X2} len={data.Length}");
                    break;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void AddLog(string line)
        {
            var entry = $"[{DateTime.Now:HH:mm:ss}] {line}";
            _log.Enqueue(entry);
            while (_log.Count > LOG_MAX) _log.Dequeue();
            MultiplayerPlugin.Log.LogInfo($"[Net] {line}");
        }

        private static void ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n == 0) throw new EndOfStreamException("Connection closed");
                off += n;
            }
        }
    }
}