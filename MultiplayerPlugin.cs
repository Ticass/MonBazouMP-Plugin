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

        public static ConfigEntry<string> CfgServerIp;
        public static ConfigEntry<int>    CfgServerPort;
        public static ConfigEntry<string> CfgPlayerName;
        public static ConfigEntry<bool>   CfgAutoConnect;

        private NetworkClient    _networkClient;
        private PlayerManager    _playerManager;
        private CarSyncManager   _carSyncManager;
        private WorldStateManager _worldState;

        private void Awake()
        {
            Instance = this;
            Log      = Logger;

            CfgServerIp    = Config.Bind("Network", "ServerIP",    "127.0.0.1", "IP of the relay server");
            CfgServerPort  = Config.Bind("Network", "ServerPort",  7777,        "Port of the relay server");
            CfgPlayerName  = Config.Bind("Network", "PlayerName",  "Player",    "Your display name");
            CfgAutoConnect = Config.Bind("Network", "AutoConnect", false,       "Auto-connect on game start");

            _playerManager  = new PlayerManager();
            _carSyncManager = new CarSyncManager();
            _worldState     = new WorldStateManager();
            _networkClient  = new NetworkClient(_playerManager, _carSyncManager, _worldState);

            PlayerTrackerBootstrap.EnsureInjected();
            gameObject.AddComponent<NetDebugOverlay>();

            Log.LogInfo("MonBazou Multiplayer loaded! F8=connect  F9=debug overlay");

            if (CfgAutoConnect.Value) Connect();
        }

        private void Update()
        {
            // Re-inject the tracker if Unity destroyed it during a scene transition
            PlayerTrackerBootstrap.EnsureInjected();

            _networkClient?.Update();
            _playerManager?.Update();
            _carSyncManager?.Update();
            _worldState?.Update();

            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (_networkClient.IsConnected) Disconnect();
                else Connect();
            }
        }

        private void OnDestroy() => Disconnect();

        public void Connect()
        {
            _networkClient.Connect(
                CfgServerIp.Value,
                CfgServerPort.Value,
                CfgPlayerName.Value);
        }

        public void Disconnect() => _networkClient?.Disconnect();

        public NetworkClient    Network  => _networkClient;
        public PlayerManager    Players  => _playerManager;
        public CarSyncManager   Cars     => _carSyncManager;
        public WorldStateManager World   => _worldState;
    }
}