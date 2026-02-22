using System;
using System.IO;

namespace MultiplayerMod
{
    public enum PacketType : byte
    {
        Handshake    = 0,
        PlayerJoin   = 1,
        PlayerLeave  = 2,
        PlayerMove   = 3,
        AssignId     = 4,
        Ping         = 5,
        Pong         = 6,
        CarUpdate    = 10,
        SetTime      = 20,
        SleepRequest = 21,
        TimeSync     = 22,
    }

    public static class PacketWriter
    {
        public static byte[] WriteHandshake(string playerName)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.Handshake);
            bw.Write(playerName);
            return ms.ToArray();
        }

        public static byte[] WritePlayerMove(int playerId,
            float px, float py, float pz, float rx, float ry, float rz)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.PlayerMove);
            bw.Write(playerId);
            bw.Write(px); bw.Write(py); bw.Write(pz);
            bw.Write(rx); bw.Write(ry); bw.Write(rz);
            return ms.ToArray();
        }

        /// <param name="carType">Vehicle_Type cast to int — stable enum ID</param>
        public static byte[] WriteCarUpdate(int playerId, int carType,
            float px, float py, float pz,
            float rx, float ry, float rz,
            float speed)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.CarUpdate);
            bw.Write(playerId);
            bw.Write(carType);
            bw.Write(px); bw.Write(py); bw.Write(pz);
            bw.Write(rx); bw.Write(ry); bw.Write(rz);
            bw.Write(speed);
            return ms.ToArray();
        }

        public static byte[] WriteSleepRequest(float targetHour)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.SleepRequest);
            bw.Write(targetHour);
            return ms.ToArray();
        }

        public static byte[] WriteTimeSync(float hour)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.TimeSync);
            bw.Write(hour);
            return ms.ToArray();
        }

        public static byte[] WritePing(long timestamp)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.Ping);
            bw.Write(timestamp);
            return ms.ToArray();
        }

        public static byte[] Frame(byte[] payload)
        {
            var frame = new byte[4 + payload.Length];
            BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
            payload.CopyTo(frame, 4);
            return frame;
        }
    }

    public static class PacketReader
    {
        public static PacketType PeekType(byte[] data) => (PacketType)data[0];

        public static string ReadHandshake(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            br.ReadByte();
            return br.ReadString();
        }

        public static (int id, float px, float py, float pz,
                        float rx, float ry, float rz) ReadPlayerMove(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            br.ReadByte();
            int id = br.ReadInt32();
            return (id,
                br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
                br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        }

        public static (int playerId, int carType,
                        float px, float py, float pz,
                        float rx, float ry, float rz,
                        float speed) ReadCarUpdate(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            br.ReadByte();
            int pid   = br.ReadInt32();
            int ctype = br.ReadInt32();
            float px = br.ReadSingle(), py = br.ReadSingle(), pz = br.ReadSingle();
            float rx = br.ReadSingle(), ry = br.ReadSingle(), rz = br.ReadSingle();
            float sp = br.ReadSingle();
            return (pid, ctype, px, py, pz, rx, ry, rz, sp);
        }

        public static float ReadSetTime(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            br.ReadByte();
            return br.ReadSingle();
        }

        public static float ReadSleepRequest(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            br.ReadByte();
            return br.ReadSingle();
        }

        public static long ReadPingPong(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            br.ReadByte();
            return br.ReadInt64();
        }

        public static byte[] WriteAssignId(int id)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.AssignId);
            bw.Write(id);
            return ms.ToArray();
        }

        public static int ReadAssignId(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            br.ReadByte();
            return br.ReadInt32();
        }

        public static byte[] WritePlayerJoin(int id, string name)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.PlayerJoin);
            bw.Write(id);
            bw.Write(name);
            return ms.ToArray();
        }

        public static (int id, string name) ReadPlayerJoin(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            br.ReadByte();
            return (br.ReadInt32(), br.ReadString());
        }

        public static byte[] WritePlayerLeave(int id)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.PlayerLeave);
            bw.Write(id);
            return ms.ToArray();
        }

        public static int ReadPlayerLeave(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            br.ReadByte();
            return br.ReadInt32();
        }

        public static byte[] WritePong(long timestamp)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.Pong);
            bw.Write(timestamp);
            return ms.ToArray();
        }

        public static byte[] WriteSetTime(float hour)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.SetTime);
            bw.Write(hour);
            return ms.ToArray();
        }

        public static byte[] WriteTimeSync(float hour)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)PacketType.TimeSync);
            bw.Write(hour);
            return ms.ToArray();
        }
    }
}