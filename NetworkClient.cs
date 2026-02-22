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

        public int    PacketsSent     { get; private set; }
        public int    PacketsReceived { get; private set; }
        public int    BytesSent       { get; private set; }
        public int    BytesReceived   { get; private set; }
        public float  PingMs          { get; private set; } = -1f;
        public string StatusLine      { get; private set; } = "Disconnected";

        private readonly Queue<string>           _log          = new Queue<string>();
        private const    int                     LOG_MAX       = 10;
        public  IEnumerable<string>              Log           => _log;

        private readonly ConcurrentQueue<byte[]> _incomingQueue = new ConcurrentQueue<byte[]>();
        private readonly PlayerManager           _playerManager;
        private readonly CarSyncManager          _carSyncManager;
        private readonly WorldStateManager       _worldState;

        private float _moveTimer;
        private float _carTimer;
        private float _pingTimer;
        private const float MOVE_INTERVAL = 0.1f;   // 10 Hz — reliable enough to debug with
        private const float CAR_INTERVAL  = 0.1f;
        private const float PING_INTERVAL = 3f;
        private long  _pingSentAt;

        public NetworkClient(PlayerManager pm, CarSyncManager csm, WorldStateManager ws)
        {
            _playerManager  = pm;
            _carSyncManager = csm;
            _worldState     = ws;
        }

        // ── Connection ────────────────────────────────────────────────────────

        public void Connect(string ip, int port, string playerName)
        {
            try
            {
                StatusLine = $"Connecting to {ip}:{port}...";
                _tcp = new TcpClient();
                _tcp.Connect(ip, port);
                _stream = _tcp.GetStream();

                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                _receiveThread.Start();

                // Handshake — server will reply with AssignId
                SendRaw(PacketWriter.WriteHandshake(playerName));
                AddLog($"Sent handshake as '{playerName}', waiting for ID...");
                StatusLine = $"Connected → {ip}:{port}  (waiting for ID)";
            }
            catch (Exception ex)
            {
                StatusLine = $"Failed: {ex.Message}";
                MultiplayerPlugin.Log.LogError($"[MP] Connection failed: {ex.Message}");
                _tcp = null;
            }
        }

        public void Disconnect()
        {
            try { _tcp?.Close(); } catch { }
            _tcp = null;
            LocalPlayerId = -1;
            StatusLine    = "Disconnected";
            PingMs        = -1f;
            _playerManager.ClearAll();
            _carSyncManager.ClearAll();
            AddLog("Disconnected");
        }

        // ── Per-frame update (main thread) ────────────────────────────────────

        public void Update()
        {
            // Always drain incoming queue first — so AssignId is processed
            // before we try to send any PlayerMove
            while (_incomingQueue.TryDequeue(out var data))
                HandlePacket(data);

            // Only send movement once we have a valid ID
            if (!IsConnected || LocalPlayerId < 0) return;

            _moveTimer += Time.deltaTime;
            if (_moveTimer >= MOVE_INTERVAL)
            {
                _moveTimer = 0f;
                SendPlayerMove();
            }

            if (LocalPlayerTracker.IsInCar)
            {
                _carTimer += Time.deltaTime;
                if (_carTimer >= CAR_INTERVAL)
                {
                    _carTimer = 0f;
                    SendCarUpdate();
                }
            }

            _pingTimer += Time.deltaTime;
            if (_pingTimer >= PING_INTERVAL)
            {
                _pingTimer  = 0f;
                _pingSentAt = Stopwatch.GetTimestamp();
                SendRaw(PacketWriter.WritePing(_pingSentAt));
            }
        }

        public void SendSleepRequest(float targetHour)
        {
            SendRaw(PacketWriter.WriteSleepRequest(targetHour));
            AddLog($"Sleep → skip to {targetHour:F1}h");
        }

        // ── Senders ───────────────────────────────────────────────────────────

        private void SendPlayerMove()
        {
            if (!LocalPlayerTracker.IsReady) return;

            var pos = LocalPlayerTracker.Position;
            var rot = LocalPlayerTracker.Rotation;

            // Sanity check — don't send 0,0,0 until tracker has real data
            if (pos == Vector3.zero) return;

            SendRaw(PacketWriter.WritePlayerMove(
                LocalPlayerId,
                pos.x, pos.y, pos.z,
                rot.x, rot.y, rot.z));
        }

        private void SendCarUpdate()
        {
            var pos = LocalPlayerTracker.CarPosition;
            var rot = LocalPlayerTracker.CarRotation;
            SendRaw(PacketWriter.WriteCarUpdate(
                LocalPlayerId,
                (int)LocalPlayerTracker.CurrentCarType,
                pos.x, pos.y, pos.z,
                rot.eulerAngles.x, rot.eulerAngles.y, rot.eulerAngles.z,
                LocalPlayerTracker.CarSpeed));
        }

        private void SendRaw(byte[] payload)
        {
            if (!IsConnected) return;
            try
            {
                var framed = PacketWriter.Frame(payload);
                lock (_sendLock) _stream.Write(framed, 0, framed.Length);
                PacketsSent++;
                BytesSent += framed.Length;
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Log.LogWarning($"[MP] Send error: {ex.Message}");
            }
        }

        // ── Receive loop (background thread) ─────────────────────────────────

        private void ReceiveLoop()
        {
            var lenBuf = new byte[4];
            try
            {
                while (_tcp != null && _tcp.Connected)
                {
                    ReadExact(_stream, lenBuf, 4);
                    int len = BitConverter.ToInt32(lenBuf, 0);
                    if (len <= 0 || len > 65536) break;
                    var data = new byte[len];
                    ReadExact(_stream, data, len);
                    PacketsReceived++;
                    BytesReceived += 4 + len;
                    _incomingQueue.Enqueue(data);
                }
            }
            catch (Exception ex)
            {
                MultiplayerPlugin.Log.LogWarning($"[MP] Receive ended: {ex.Message}");
                StatusLine = "Disconnected (connection lost)";
            }
        }

        // ── Packet dispatch (main thread only) ───────────────────────────────

        private void HandlePacket(byte[] data)
        {
            var type = PacketReader.PeekType(data);

            switch (type)
            {
                case PacketType.AssignId:
                {
                    LocalPlayerId = PacketReader.ReadAssignId(data);
                    StatusLine    = StatusLine.Replace("waiting for ID", $"ID #{LocalPlayerId}");
                    AddLog($"✓ Assigned local ID #{LocalPlayerId}");
                    MultiplayerPlugin.Log.LogInfo($"[MP] Local player ID = {LocalPlayerId}");
                    break;
                }

                case PacketType.PlayerJoin:
                {
                    var (joinId, joinName) = PacketReader.ReadPlayerJoin(data);
                    // Ignore if this is somehow our own ID echo
                    if (joinId == LocalPlayerId) break;
                    AddLog($"→ {joinName} (#{joinId}) joined");
                    MultiplayerPlugin.Log.LogInfo($"[MP] PlayerJoin id={joinId} name={joinName}");
                    _playerManager.AddRemotePlayer(joinId, joinName);
                    break;
                }

                case PacketType.PlayerLeave:
                {
                    var leaveId   = PacketReader.ReadPlayerLeave(data);
                    var leaveName = _playerManager.GetName(leaveId);
                    AddLog($"← {leaveName} (#{leaveId}) left");
                    MultiplayerPlugin.Log.LogInfo($"[MP] PlayerLeave id={leaveId}");
                    _playerManager.RemoveRemotePlayer(leaveId);
                    _carSyncManager.OnPlayerLeft(leaveId);
                    break;
                }

                case PacketType.PlayerMove:
                {
                    var (id, px, py, pz, rx, ry, rz) = PacketReader.ReadPlayerMove(data);
                    // Ignore our own echoed packets (server shouldn't send these back but be safe)
                    if (id == LocalPlayerId) break;
                    _playerManager.UpdateRemotePlayer(id,
                        new Vector3(px, py, pz),
                        new Vector3(rx, ry, rz));
                    break;
                }

                case PacketType.CarUpdate:
                {
                    var (pid, carType, cpx, cpy, cpz, crx, cry, crz, spd) =
                        PacketReader.ReadCarUpdate(data);
                    if (pid == LocalPlayerId) break;
                    _carSyncManager.OnCarUpdate(pid, carType,
                        new Vector3(cpx, cpy, cpz),
                        Quaternion.Euler(crx, cry, crz),
                        spd);
                    break;
                }

                case PacketType.SetTime:
                {
                    var h = PacketReader.ReadSetTime(data);
                    AddLog($"⏰ Time set → {h:F1}h");
                    _worldState.ApplyServerTime(h, instantSnap: true);
                    break;
                }

                case PacketType.TimeSync:
                {
                    var h = PacketReader.ReadSetTime(data);
                    // Use instantSnap:true — the server only sends this every 30s so
                    // there's no jitter risk, and we always want the correction applied.
                    _worldState.ApplyServerTime(h, instantSnap: true);
                    break;
                }

                case PacketType.Pong:
                {
                    var sentAt = PacketReader.ReadPingPong(data);
                    PingMs = (float)(Stopwatch.GetTimestamp() - sentAt)
                             / Stopwatch.Frequency * 1000f;
                    break;
                }

                default:
                    MultiplayerPlugin.Log.LogWarning(
                        $"[MP] Unknown packet type 0x{(byte)type:X2}");
                    break;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void AddLog(string line)
        {
            var entry = $"[{DateTime.Now:HH:mm:ss}] {line}";
            _log.Enqueue(entry);
            while (_log.Count > LOG_MAX) _log.Dequeue();
            MultiplayerPlugin.Log.LogInfo($"[MP] {line}");
        }

        private static void ReadExact(Stream s, byte[] buf, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = s.Read(buf, offset, count - offset);
                if (read == 0) throw new EndOfStreamException("Connection closed");
                offset += read;
            }
        }
    }
}