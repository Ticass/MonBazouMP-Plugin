// packets.cs — ALL packet read/write. Single source of truth.
// No other file should manually construct byte arrays.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MultiplayerMod
{
    public enum PacketType : byte
    {
        Handshake   = 0,
        PlayerJoin  = 1,
        PlayerLeave = 2,
        PlayerMove  = 3,
        AssignId    = 4,
        Ping        = 5,
        Pong        = 6,
        RoleAssign  = 7,
        VehicleState = 10,
        TimeSync     = 20,
        SleepRequest = 21,
    }

    public enum PlayerRole : byte { Host = 0, Guest = 1 }

    // =========================================================================
    // Writer
    // =========================================================================
    public static class PacketWriter
    {
        // Wrap a payload with a 4-byte little-endian length prefix for framing.
        public static byte[] Frame(byte[] payload)
        {
            var out_ = new byte[4 + payload.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, out_, 0, 4);
            Buffer.BlockCopy(payload, 0, out_, 4, payload.Length);
            return out_;
        }

        public static byte[] WriteHandshake(string name) =>
            Make(PacketType.Handshake, w => w.Write(name));

        public static byte[] WriteAssignId(int id) =>
            Make(PacketType.AssignId, w => w.Write(id));

        public static byte[] WriteRoleAssign(PlayerRole role) =>
            Make(PacketType.RoleAssign, w => w.Write((byte)role));

        public static byte[] WritePlayerJoin(int id, string name, PlayerRole role) =>
            Make(PacketType.PlayerJoin, w => { w.Write(id); w.Write(name); w.Write((byte)role); });

        public static byte[] WritePlayerLeave(int id) =>
            Make(PacketType.PlayerLeave, w => w.Write(id));

        // On-foot: position + Y-euler only (ghosts only need yaw).
        public static byte[] WritePlayerMove(int id, float px, float py, float pz, float yaw) =>
            Make(PacketType.PlayerMove, w =>
            {
                w.Write(id);
                w.Write(px); w.Write(py); w.Write(pz);
                w.Write(yaw);
            });

        // Vehicle: position + quaternion rotation + linear vel + angular vel + speed + occupants.
        // We send full quaternion (not euler) to avoid gimbal lock on flipped cars.
        // We send angular velocity so guests can dead-reckon rotation too.
        public static byte[] WriteVehicleState(
            int vt,
            Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel,
            float speed, int driverId, int passengerId) =>
            Make(PacketType.VehicleState, w =>
            {
                w.Write(vt);
                w.Write(pos.x);    w.Write(pos.y);    w.Write(pos.z);
                w.Write(rot.x);    w.Write(rot.y);    w.Write(rot.z);    w.Write(rot.w);
                w.Write(vel.x);    w.Write(vel.y);    w.Write(vel.z);
                w.Write(angVel.x); w.Write(angVel.y); w.Write(angVel.z);
                w.Write(speed);
                w.Write(driverId);
                w.Write(passengerId);
            });

        public static byte[] WriteTimeSync(int hour, int minute, int day) =>
            Make(PacketType.TimeSync, w => { w.Write(hour); w.Write(minute); w.Write(day); });

        public static byte[] WriteSleepRequest() =>
            Make(PacketType.SleepRequest, _ => { });

        public static byte[] WritePing(long ts) =>
            Make(PacketType.Ping, w => w.Write(ts));

        public static byte[] WritePong(long ts) =>
            Make(PacketType.Pong, w => w.Write(ts));

        // ── helper ───────────────────────────────────────────────────────────
        private static byte[] Make(PacketType type, Action<BinaryWriter> fill)
        {
            using var ms = new MemoryStream(64);
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)type);
            fill(bw);
            return ms.ToArray();
        }
    }

    // =========================================================================
    // Reader
    // =========================================================================
    public static class PacketReader
    {
        public static PacketType PeekType(byte[] d) => (PacketType)d[0];

        public static string ReadHandshake(byte[] d) =>
            Read(d, r => r.ReadString());

        public static int ReadAssignId(byte[] d) =>
            Read(d, r => r.ReadInt32());

        public static PlayerRole ReadRoleAssign(byte[] d) =>
            Read(d, r => (PlayerRole)r.ReadByte());

        public static (int id, string name, PlayerRole role) ReadPlayerJoin(byte[] d) =>
            Read(d, r => (r.ReadInt32(), r.ReadString(), (PlayerRole)r.ReadByte()));

        public static int ReadPlayerLeave(byte[] d) =>
            Read(d, r => r.ReadInt32());

        public static (int id, Vector3 pos, float yaw) ReadPlayerMove(byte[] d) =>
            Read(d, r => (
                r.ReadInt32(),
                new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                r.ReadSingle()
            ));

        public struct VehicleState
        {
            public int       vt;
            public Vector3   pos;
            public Quaternion rot;
            public Vector3   vel;
            public Vector3   angVel;
            public float     speed;
            public int       driverId;
            public int       passengerId;
        }

        public static VehicleState ReadVehicleState(byte[] d) =>
            Read(d, r =>
            {
                var s = new VehicleState();
                s.vt         = r.ReadInt32();
                s.pos        = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                s.rot        = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                s.vel        = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                s.angVel     = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                s.speed      = r.ReadSingle();
                s.driverId   = r.ReadInt32();
                s.passengerId = r.ReadInt32();
                return s;
            });

        public static (int hour, int minute, int day) ReadTimeSync(byte[] d) =>
            Read(d, r => (r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));

        public static long ReadPingPong(byte[] d) =>
            Read(d, r => r.ReadInt64());

        // ── helper ───────────────────────────────────────────────────────────
        private static T Read<T>(byte[] d, Func<BinaryReader, T> parse)
        {
            using var ms = new MemoryStream(d);
            using var br = new BinaryReader(ms);
            br.ReadByte(); // consume type byte
            return parse(br);
        }
    }
}