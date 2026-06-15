using System;
using System.Collections.Generic;
using System.Reflection;
using BabyStepsNetworking.Client;
using BabyStepsNetworking.Extensions;
using BabyStepsNetworking.Transport;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks.Networking
{
    // Bridges to BabyStepsMultiplayerClient's NetworkManager purely via reflection (so
    // BabyBlocks has no hard dependency on that mod and keeps working fine when it isn't
    // installed), then talks to its NetworkClient through a real BabyStepsNetworking
    // mod channel (BabyBlocks references BabyStepsNetworking directly).
    //
    // Every connected client that has BabyBlocks periodically broadcasts a 1-byte "hello"
    // packet over a mod channel. The multiplayer server auto-relays any unrecognized
    // mod-range opcode to every other client (tagged with the sender's player UUID), so
    // this works on any ordinary server with zero server-side or BabyBlocks-side setup
    // beyond the client mods being installed.
    internal static class ModNetworking
    {
        private const byte HelloMarker = 0xB5; // arbitrary "I have Baby Blocks" marker byte
        private const byte PropPlacedMarker = 0xB6; // a peer dropped a prop from the palette
        private const byte FreecamUpdateMarker = 0xB7; // a peer entered/moved/left fly-cam
        private const float AnnounceIntervalSeconds = 5f;
        private const float FreecamSendIntervalSeconds = 0.15f;

        // Most freecam updates are sent Unreliable (cheap, high-frequency). Every
        // FreecamReliableIntervalSeconds, one is sent ReliableOrdered instead, so a peer
        // who joins mid-session (and missed earlier Unreliable broadcasts) is guaranteed
        // to get a marker-creating packet within that window without needing the sender
        // to toggle fly-cam.
        private const float FreecamReliableIntervalSeconds = 1.5f;
        private static float _nextFreecamReliableTime;

        // Player UUIDs (as used by BabyStepsMultiplayerClient's NetworkManager) of peers
        // who have announced that they also have Baby Blocks installed.
        public static readonly HashSet<byte> PeersWithBabyBlocks = new();

        private static bool _reflectionDone;
        private static bool _reflectionOk;

        private static FieldInfo _networkManagerField; // BabyStepsMultiplayerClient.Core.networkManager (static)
        private static PropertyInfo _isConnectedProp;   // NetworkManager.IsConnected
        private static FieldInfo _networkClientField;   // NetworkManager._networkClient

        // Local player appearance, used to stamp our suit color/nickname on freecam
        // broadcasts. Resolved separately from the connection reflection above so a
        // failure here doesn't disable the rest of ModNetworking.
        private static FieldInfo _playerConfigField;   // BabyStepsMultiplayerClient.ModSettings.player (static)
        private static FieldInfo _suitColorEntryField; // PlayerConfig.SuitColor
        private static FieldInfo _nicknameEntryField;  // PlayerConfig.Nickname
        private static PropertyInfo _suitColorValueProp; // MelonPreferences_Entry<Color>.Value
        private static PropertyInfo _nicknameValueProp;  // MelonPreferences_Entry<string>.Value

        private static IModChannel _channel;
        private static float _nextAnnounceTime;
        private static float _nextFreecamSendTime;
        private static bool _wasFlyCamActive;

        public static void Update()
        {
            EnsureReflection();
            if (!_reflectionOk) return;

            object networkManager;
            try
            {
                networkManager = _networkManagerField.GetValue(null);
            }
            catch { return; }

            bool connected = false;
            if (networkManager != null)
            {
                try { connected = (bool)_isConnectedProp.GetValue(networkManager); }
                catch { connected = false; }
            }

            if (connected)
            {
                if (_channel == null)
                    TryCreateChannel(networkManager);

                if (_channel != null && Time.unscaledTime >= _nextAnnounceTime)
                {
                    Announce();
                    _nextAnnounceTime = Time.unscaledTime + AnnounceIntervalSeconds;
                }

                if (_channel != null)
                {
                    bool flyActive = FlyCamController.FlyCamActive;
                    if (flyActive && Time.unscaledTime >= _nextFreecamSendTime)
                    {
                        bool reliable = Time.unscaledTime >= _nextFreecamReliableTime;
                        SendFreecamUpdate(true, reliable);
                        _nextFreecamSendTime = Time.unscaledTime + FreecamSendIntervalSeconds;
                        if (reliable)
                            _nextFreecamReliableTime = Time.unscaledTime + FreecamReliableIntervalSeconds;
                    }
                    else if (!flyActive && _wasFlyCamActive)
                    {
                        SendFreecamUpdate(false);
                    }
                    _wasFlyCamActive = flyActive;
                }
            }
            else if (_channel != null)
            {
                TeardownChannel();
            }

            RemoteFreecamManager.Update();
        }

        private static void EnsureReflection()
        {
            if (_reflectionDone) return;
            _reflectionDone = true;
            try
            {
                Assembly mpAsm = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "BabyStepsMultiplayerClient")
                    {
                        mpAsm = asm;
                        break;
                    }
                }
                if (mpAsm == null) return;

                var coreType = mpAsm.GetType("BabyStepsMultiplayerClient.Core");
                _networkManagerField = coreType?.GetField("networkManager",
                    BindingFlags.Public | BindingFlags.Static);
                var networkManagerType = _networkManagerField?.FieldType;
                if (networkManagerType == null) return;

                _isConnectedProp = networkManagerType.GetProperty("IsConnected",
                    BindingFlags.Public | BindingFlags.Instance);
                _networkClientField = networkManagerType.GetField("_networkClient",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (_isConnectedProp == null || _networkClientField == null) return;

                _reflectionOk = true;
                BBLog.Msg("[BabyBlocks][ModNetworking] Reflection bridge to BabyStepsMultiplayerClient established");

                // Best-effort: resolve ModSettings.player.SuitColor/Nickname so freecam
                // broadcasts can include our own appearance. Failure here is non-fatal.
                try
                {
                    var modSettingsType = mpAsm.GetType("BabyStepsMultiplayerClient.ModSettings");
                    _playerConfigField = modSettingsType?.GetField("player",
                        BindingFlags.Public | BindingFlags.Static);
                    var playerConfigType = _playerConfigField?.FieldType;

                    _suitColorEntryField = playerConfigType?.GetField("SuitColor",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    _nicknameEntryField = playerConfigType?.GetField("Nickname",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                    _suitColorValueProp = _suitColorEntryField?.FieldType.GetProperty("Value",
                        BindingFlags.Public | BindingFlags.Instance);
                    _nicknameValueProp = _nicknameEntryField?.FieldType.GetProperty("Value",
                        BindingFlags.Public | BindingFlags.Instance);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[BabyBlocks][ModNetworking] Appearance reflection setup failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] Reflection setup failed: {ex.Message}");
            }
        }

        // Returns the local player's suit color and nickname via reflection into
        // BabyStepsMultiplayerClient.ModSettings.player. Falls back to white/empty if
        // the multiplayer mod isn't installed or its fields couldn't be resolved.
        private static bool TryGetLocalAppearance(out Color suitColor, out string displayName)
        {
            suitColor = Color.white;
            displayName = string.Empty;
            if (_playerConfigField == null) return false;

            try
            {
                var playerConfig = _playerConfigField.GetValue(null);
                if (playerConfig == null) return false;

                if (_suitColorEntryField != null && _suitColorValueProp != null)
                {
                    var entry = _suitColorEntryField.GetValue(playerConfig);
                    if (entry != null) suitColor = (Color)_suitColorValueProp.GetValue(entry);
                }
                if (_nicknameEntryField != null && _nicknameValueProp != null)
                {
                    var entry = _nicknameEntryField.GetValue(playerConfig);
                    if (entry != null) displayName = (string)_nicknameValueProp.GetValue(entry) ?? string.Empty;
                }
                return true;
            }
            catch { return false; }
        }

        private static void TryCreateChannel(object networkManager)
        {
            try
            {
                var networkClient = _networkClientField.GetValue(networkManager) as NetworkClient;
                if (networkClient == null) return;

                var channel = networkClient.CreateModChannel("BabyBlocks");
                if (channel == null) return;

                channel.DataReceived += OnDataReceived;

                _channel = channel;
                PeersWithBabyBlocks.Clear();
                _nextAnnounceTime = 0f; // announce immediately on the next Update
                BBLog.Msg("[BabyBlocks][ModNetworking] Mod channel established");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] Failed to create mod channel: {ex.Message}");
            }
        }

        private static void TeardownChannel()
        {
            try
            {
                if (_channel != null)
                    _channel.DataReceived -= OnDataReceived;
            }
            catch { /* best-effort */ }

            _channel = null;
            PeersWithBabyBlocks.Clear();
            RemoteFreecamManager.ClearAll();
        }

        private static void Announce()
        {
            try
            {
                _channel.Send(new byte[] { HelloMarker }, PacketDelivery.ReliableOrdered);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] Announce failed: {ex.Message}");
            }
        }

        // Broadcasts that a prop was just placed via the level editor's drag-and-drop
        // palette, so peers can spawn the same prop at the same transform as a basic
        // sync test. Payload layout:
        //   [marker][idLen:ushort LE][idBytes UTF8][pos.xyz][rot.xyzw][scale.xyz] (floats LE)
        public static void SendPropPlaced(string propId, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (_channel == null) return;
            try
            {
                var idBytes = System.Text.Encoding.UTF8.GetBytes(propId);
                var payload = new byte[1 + 2 + idBytes.Length + 4 * (3 + 4 + 3)];
                int o = 0;
                payload[o++] = PropPlacedMarker;
                payload[o++] = (byte)(idBytes.Length & 0xFF);
                payload[o++] = (byte)((idBytes.Length >> 8) & 0xFF);
                Buffer.BlockCopy(idBytes, 0, payload, o, idBytes.Length);
                o += idBytes.Length;

                WriteFloat(payload, ref o, position.x);
                WriteFloat(payload, ref o, position.y);
                WriteFloat(payload, ref o, position.z);
                WriteFloat(payload, ref o, rotation.x);
                WriteFloat(payload, ref o, rotation.y);
                WriteFloat(payload, ref o, rotation.z);
                WriteFloat(payload, ref o, rotation.w);
                WriteFloat(payload, ref o, scale.x);
                WriteFloat(payload, ref o, scale.y);
                WriteFloat(payload, ref o, scale.z);

                _channel.Send(payload, PacketDelivery.ReliableOrdered);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendPropPlaced failed: {ex.Message}");
            }
        }

        // Broadcasts the local player's fly-cam state to peers. While active, this is sent
        // at FreecamSendIntervalSeconds over an unreliable channel along with our suit color
        // and nickname; on exiting fly-cam a single reliable "inactive" packet is sent so
        // peers can remove our marker. Payload layout:
        //   [marker][active:byte][pos.xyz floats LE][color.rgb bytes][nameLen:ushort LE][nameBytes UTF8]
        private static void SendFreecamUpdate(bool active, bool reliable = false)
        {
            if (_channel == null) return;
            try
            {
                Vector3 pos = Vector3.zero;
                Color color = Color.white;
                string name = string.Empty;

                if (active)
                {
                    var player = PlayerMovement.me;
                    if (player == null || player.flyCam == null) return;
                    pos = player.flyCam.transform.position;
                    TryGetLocalAppearance(out color, out name);
                }

                var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
                var payload = new byte[1 + 1 + 4 * 3 + 3 + 2 + nameBytes.Length];
                int o = 0;
                payload[o++] = FreecamUpdateMarker;
                payload[o++] = (byte)(active ? 1 : 0);
                WriteFloat(payload, ref o, pos.x);
                WriteFloat(payload, ref o, pos.y);
                WriteFloat(payload, ref o, pos.z);
                payload[o++] = (byte)Mathf.Clamp(Mathf.RoundToInt(color.r * 255f), 0, 255);
                payload[o++] = (byte)Mathf.Clamp(Mathf.RoundToInt(color.g * 255f), 0, 255);
                payload[o++] = (byte)Mathf.Clamp(Mathf.RoundToInt(color.b * 255f), 0, 255);
                payload[o++] = (byte)(nameBytes.Length & 0xFF);
                payload[o++] = (byte)((nameBytes.Length >> 8) & 0xFF);
                Buffer.BlockCopy(nameBytes, 0, payload, o, nameBytes.Length);

                _channel.Send(payload, (!active || reliable) ? PacketDelivery.ReliableOrdered : PacketDelivery.Unreliable);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendFreecamUpdate failed: {ex.Message}");
            }
        }

        private static void WriteFloat(byte[] buf, ref int offset, float value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buf, offset, 4);
            offset += 4;
        }

        private static float ReadFloat(byte[] buf, ref int offset)
        {
            float value = BitConverter.ToSingle(buf, offset);
            offset += 4;
            return value;
        }

        private static void OnDataReceived(byte senderUuid, byte[] payload)
        {
            if (payload == null || payload.Length < 1) return;

            switch (payload[0])
            {
                case HelloMarker:
                    if (PeersWithBabyBlocks.Add(senderUuid))
                        MelonLogger.Msg($"[BabyBlocks] Player {senderUuid} also has Baby Blocks installed");
                    break;

                case PropPlacedMarker:
                    HandlePropPlaced(senderUuid, payload);
                    break;

                case FreecamUpdateMarker:
                    HandleFreecamUpdate(senderUuid, payload);
                    break;
            }
        }

        private static void HandleFreecamUpdate(byte senderUuid, byte[] payload)
        {
            try
            {
                int o = 1;
                bool active = payload[o++] != 0;
                if (!active)
                {
                    RemoteFreecamManager.Remove(senderUuid);
                    return;
                }

                var pos = new Vector3(ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o));
                var color = new Color(payload[o++] / 255f, payload[o++] / 255f, payload[o++] / 255f);
                int nameLen = payload[o] | (payload[o + 1] << 8);
                o += 2;
                string name = System.Text.Encoding.UTF8.GetString(payload, o, nameLen);

                RemoteFreecamManager.UpdateFreecam(senderUuid, pos, color, name);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandleFreecamUpdate failed: {ex.Message}");
            }
        }

        private static void HandlePropPlaced(byte senderUuid, byte[] payload)
        {
            try
            {
                int o = 1;
                int idLen = payload[o] | (payload[o + 1] << 8);
                o += 2;
                string propId = System.Text.Encoding.UTF8.GetString(payload, o, idLen);
                o += idLen;

                var position = new Vector3(ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o));
                var rotation  = new Quaternion(ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o));
                var scale     = new Vector3(ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o));

                var info = PropLibrary.FindById(propId);
                if (info == null)
                {
                    MelonLogger.Warning($"[BabyBlocks][ModNetworking] Player {senderUuid} placed unknown prop '{propId}'");
                    return;
                }

                LevelEditor.EnsureManager();
                PropLibrary.LoadPropData(info);

                var obj = LevelEditorManager.Instance.SpawnFromPropInfo(info, position);
                if (obj == null) return;

                obj.transform.SetPositionAndRotation(position, rotation);
                obj.transform.localScale = scale;

                MelonLogger.Msg($"[BabyBlocks] Player {senderUuid} placed prop '{propId}'");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandlePropPlaced failed: {ex.Message}");
            }
        }
    }
}
