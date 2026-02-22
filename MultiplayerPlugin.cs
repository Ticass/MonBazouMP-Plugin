// MultiplayerPlugin.cs — BepInEx entry point, owns all sub-systems.
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace MultiplayerMod
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class MultiplayerPlugin : BaseUnityPlugin
    {
        public static MultiplayerPlugin Instance { get; private set; }
        public static ManualLogSource   Log      { get; private set; }

        // ── Config ────────────────────────────────────────────────────────────
        public static ConfigEntry<string> CfgServerIp;
        public static ConfigEntry<int>    CfgServerPort;
        public static ConfigEntry<string> CfgPlayerName;
        public static ConfigEntry<bool>   CfgAutoConnect;

        // ── Role — set by server on connect ──────────────────────────────────
        public PlayerRole Role { get; private set; } = PlayerRole.Guest;

        // ── Sub-systems ───────────────────────────────────────────────────────
        private NetworkClient     _net;
        private PlayerManager     _players;
        private CarSyncManager    _cars;
        private WorldStateManager _world;

        // ── Unity lifecycle ───────────────────────────────────────────────────
        private void Awake()
        {
            Instance = this;
            Log      = Logger;

            CfgServerIp    = Config.Bind("Network", "ServerIP",    "127.0.0.1", "Relay server IP");
            CfgServerPort  = Config.Bind("Network", "ServerPort",  7777,        "Relay server port");
            CfgPlayerName  = Config.Bind("Network", "PlayerName",  "Player",    "Your in-game name");
            CfgAutoConnect = Config.Bind("Network", "AutoConnect", false,       "Connect on game start");

            _players = new PlayerManager();
            _cars    = new CarSyncManager();
            _world   = new WorldStateManager();
            _net     = new NetworkClient(_players, _cars, _world);

            PlayerTrackerBootstrap.EnsureInjected();
            gameObject.AddComponent<NetDebugOverlay>();

            Log.LogInfo("MonBazou Multiplayer loaded — F8 = connect/disconnect");

            if (CfgAutoConnect.Value) DoConnect();
        }

        private void Update()
        {
            // Re-inject if scene reload destroyed our tracker
            PlayerTrackerBootstrap.EnsureInjected();

            _net?.Update();
            _players?.Update();
            _cars?.Update();
            _world?.Update();

            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (_net.IsConnected) DoDisconnect();
                else                  DoConnect();
            }
        }

        private void OnDestroy() => DoDisconnect();

        // ── Public methods ────────────────────────────────────────────────────
        public void SetRole(PlayerRole role) { Role = role; }

        public void DoConnect()
        {
            Role = PlayerRole.Guest; // safe default until server confirms
            _net.Connect(CfgServerIp.Value, CfgServerPort.Value, CfgPlayerName.Value);
        }

        public void DoDisconnect() => _net?.Disconnect();

        // Accessors used by sub-systems
        public NetworkClient     Network => _net;
        public PlayerManager     Players => _players;
        public CarSyncManager    Cars    => _cars;
        public WorldStateManager World   => _world;
    }
}