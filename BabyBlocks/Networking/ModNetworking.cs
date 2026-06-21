using System;
using System.Collections.Generic;
using System.Reflection;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks.Networking
{
    // Bridges to BabyStepsMultiplayerClient's NetworkManager and BabyStepsNetworking's channel
    // types purely via reflection, so BabyBlocks has no hard dependency on either and keeps
    // working fine when they aren't installed.
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
        private const byte PropTransformMarker = 0xB8; // a peer moved/rotated/scaled a networked prop
        private const byte PropSelectedMarker = 0xB9; // a peer selected/deselected a networked prop
        private const byte PropGhostUpdateMarker = 0xBA; // a peer is dragging a prop out of the palette
        private const byte PropGhostEndMarker = 0xBB; // a peer's in-progress palette placement ended without placing
        private const byte PropDeletedMarker = 0xBC; // a peer deleted a networked prop
        private const byte MaterialAppliedMarker = 0xBD; // a peer dragged a material construction onto a networked prop
        private const byte LevelClearedMarker = 0xBE; // a peer pressed the Clear Level button
        private const byte GroupSyncMarker = 0xBF; // a peer grouped/ungrouped a set of networked static props
        private const byte BaseMapStateMarker = 0xC0; // a peer toggled the Base Map on/off
        private const byte LevelTransferRequestMarker = 0xC1; // a peer is asking for the current level
        private const byte LevelTransferDataMarker = 0xC2; // level payload (BBB without baked data) in response
        private const float AnnounceIntervalSeconds = 5f;
        private const float FreecamSendIntervalSeconds = 0.15f;

        // Snapshot of the level taken immediately before TryCreateChannel clears it on
        // connect. Held so we can respond to a LevelTransferRequest from a peer that
        // connected at roughly the same time (and therefore sees our empty level).
        // Cleared once we receive level data from a peer (our snapshot is then stale).
        private static byte[] _pendingLevelSnapshot;

        // Maximum bytes of BBB payload per network packet. Conservative to stay well
        // under any UDP-derived MTU the relay server might impose.
        private const int LevelChunkSize = 8192;

        // Per-sender in-progress chunk assembly state, keyed by sender UUID.
        private static readonly Dictionary<byte, LevelChunkState> _levelChunkStates = new();

        // While we're receiving a level transfer (chunks in flight → level loading), live
        // prop-mutation events (place/delete/clear/material) are buffered so they aren't
        // silently discarded by LoadFromNetworkData's RemoveAll(). After loading finishes
        // the buffer is replayed so mid-connect edits appear correctly.
        private static bool _levelTransferPending;
        private static readonly List<(byte sender, byte[] payload)> _bufferedLivePackets = new();

        // Time the level-transfer request was sent. If no data arrives within this window
        // (e.g. no peers have a level) the pending flag is cleared automatically.
        private const float LevelTransferTimeoutSeconds = 10f;
        private static float _levelTransferRequestTime;

        private class LevelChunkState
        {
            public byte[][] Chunks;
            public int ReceivedCount;
            public int TotalLen;
        }

        // Networked props, keyed by a netId shared between both clients' copies of the
        // same logical prop. Lets HandlePropTransformUpdate/HandlePropSelected apply
        // updates in-place instead of spawning duplicates. Entries whose object has been
        // destroyed are pruned lazily on lookup.
        private static readonly Dictionary<ulong, LevelEditorObject> _networkedObjects = new();

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

        // BabyStepsNetworking channel (object so no hard type dependency)
        private static object   _channel;
        private static MethodInfo _createModChannelMethod;
        private static MethodInfo _channelSendMethod;
        private static EventInfo  _channelDataReceivedEvent;
        private static object     _packetDeliveryReliable;
        private static object     _packetDeliveryUnreliable;
        private static Delegate   _dataReceivedHandler;

        private static float _nextAnnounceTime;
        private static float _nextFreecamSendTime;
        private static bool _wasFlyCamActive;
        private static bool _channelCreateFailed;

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
                if (_channel == null && !_channelCreateFailed)
                    TryCreateChannel(networkManager);

                // If no peer responded to our level-transfer request within the timeout
                // (empty server, or no peer has BabyBlocks), clear the pending state so
                // the "Loading level..." overlay doesn't stay up forever.
                if (_levelTransferPending
                    && Time.unscaledTime - _levelTransferRequestTime > LevelTransferTimeoutSeconds)
                {
                    MelonLogger.Msg("[BabyBlocks][ModNetworking] Level transfer timed out — no response from peers");
                    _levelTransferPending = false;
                    _bufferedLivePackets.Clear();
                    FlyCamController.EndNetworkLevelTransfer();
                }

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
            RemotePropHighlightManager.Update();
            RemotePropGhostManager.Update();
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

                // Resolve BabyStepsNetworking channel types (optional — absent = no channel sync)
                try
                {
                    Assembly bsnAsm = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        if (asm.GetName().Name == "BabyStepsNetworking") { bsnAsm = asm; break; }

                    if (bsnAsm == null)
                    {
                        BBLog.Msg("[BabyBlocks][ModNetworking] BabyStepsNetworking not present — channel sync disabled");
                    }
                    else
                    {
                        var networkClientType    = bsnAsm.GetType("BabyStepsNetworking.Client.NetworkClient");
                        var modChannelType       = bsnAsm.GetType("BabyStepsNetworking.Extensions.IModChannel");
                        var packetDeliveryType   = bsnAsm.GetType("BabyStepsNetworking.Transport.PacketDelivery");

                        _createModChannelMethod    = networkClientType?.GetMethod("CreateModChannel", new[] { typeof(string) });
                        _channelSendMethod         = modChannelType?.GetMethod("Send");
                        _channelDataReceivedEvent  = modChannelType?.GetEvent("DataReceived");

                        if (packetDeliveryType != null)
                        {
                            _packetDeliveryReliable   = Enum.Parse(packetDeliveryType, "ReliableOrdered");
                            _packetDeliveryUnreliable = Enum.Parse(packetDeliveryType, "Unreliable");
                        }

                        if (_createModChannelMethod != null && _channelSendMethod != null && _channelDataReceivedEvent != null)
                            BBLog.Msg("[BabyBlocks][ModNetworking] BabyStepsNetworking channel types resolved");
                        else
                            MelonLogger.Warning("[BabyBlocks][ModNetworking] BabyStepsNetworking found but channel types incomplete — sync disabled");
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[BabyBlocks][ModNetworking] BabyStepsNetworking reflection failed: {ex.Message}");
                }

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
                if (_createModChannelMethod == null || _channelSendMethod == null || _channelDataReceivedEvent == null) return;

                var networkClient = _networkClientField.GetValue(networkManager);
                if (networkClient == null) return;

                var channel = _createModChannelMethod.Invoke(networkClient, new object[] { "BabyBlocks" });
                if (channel == null) return;

                // Ensure the level manager and prop library exist before we start
                // receiving packets.
                LevelEditor.EnsureManager();

                // Scan GPUI props eagerly so level-transfer data received immediately
                // on connect can resolve gpui:// prop IDs. Follow with InvalidateMaterialSources
                // for the same reason the lazy-scan sites do: prevents a synchronous
                // EnsureMaterialSources from caching placeholder materials during spawn.
                if (GpuiPropScanner.GpuiScannedNames.Count == 0)
                {
                    GpuiPropScanner.ScanGpuiProps();
                    MaterialCatalog.InvalidateMaterialSources();
                }

                // Snapshot the current level before clearing so we can respond to a
                // LevelTransferRequest from a peer who also just connected and sees our
                // empty slate. SerializeForNetwork() returns null if the scene is empty.
                _pendingLevelSnapshot = LevelSaveLoad.SerializeForNetwork();

                // Clear before subscribing so we can't receive packets for objects we're
                // about to destroy.
                LevelEditorManager.Instance?.RemoveAll();
                LevelEditor.ClearAllSelectionState();
                _networkedObjects.Clear();

                _dataReceivedHandler = Delegate.CreateDelegate(
                    _channelDataReceivedEvent.EventHandlerType, null,
                    typeof(ModNetworking).GetMethod("OnDataReceived", BindingFlags.NonPublic | BindingFlags.Static));
                _channelDataReceivedEvent.AddEventHandler(channel, _dataReceivedHandler);

                _channel = channel;
                PeersWithBabyBlocks.Clear();
                _nextAnnounceTime = 0f; // announce immediately on the next Update

                // Ask peers for their current level. Whoever has one will respond with
                // LevelTransferData so we can load it. Start buffering live edits so
                // props placed or deleted between now and when the transfer loads aren't lost.
                _levelTransferPending = true;
                _levelTransferRequestTime = Time.unscaledTime;
                _bufferedLivePackets.Clear();
                FlyCamController.BeginNetworkLevelTransfer();
                SendLevelTransferRequest();
                BBLog.Msg("[BabyBlocks][ModNetworking] Mod channel established");
            }
            catch (Exception ex)
            {
                _channelCreateFailed = true;
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] Failed to create mod channel: {ex.Message}");
            }
        }

        private static void TeardownChannel()
        {
            try
            {
                if (_channel != null && _channelDataReceivedEvent != null && _dataReceivedHandler != null)
                    _channelDataReceivedEvent.RemoveEventHandler(_channel, _dataReceivedHandler);
            }
            catch { /* best-effort */ }

            _dataReceivedHandler = null;
            _channel = null;
            _channelCreateFailed = false;
            _pendingLevelSnapshot = null;
            _levelChunkStates.Clear();
            _levelTransferPending = false;
            _bufferedLivePackets.Clear();
            FlyCamController.EndNetworkLevelTransfer();
            PeersWithBabyBlocks.Clear();
            _networkedObjects.Clear();
            RemoteFreecamManager.ClearAll();
            RemotePropHighlightManager.ClearAll();
            RemotePropGhostManager.ClearAll();
        }

        private static void Announce()
        {
            try
            {
                ChannelSend(new byte[] { HelloMarker }, reliable: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] Announce failed: {ex.Message}");
            }
        }

        // Generates a process-unique-enough id for tagging newly-placed networked props.
        // Both clients use the same id for their copy of a given prop, derived from a
        // random GUID — no negotiation needed since collisions are astronomically unlikely.
        private static ulong GenerateNetId()
        {
            var bytes = Guid.NewGuid().ToByteArray();
            ulong id = BitConverter.ToUInt64(bytes, 0);
            return id == 0 ? 1 : id;
        }

        // Assigns a fresh netId to a locally-placed object and registers it for future
        // transform/selection sync. Returns the assigned id (unchanged if already assigned).
        public static ulong RegisterNetworkedObject(LevelEditorObject obj)
        {
            if (obj == null) return 0;
            if (obj.netId != 0) return obj.netId;
            obj.netId = GenerateNetId();
            _networkedObjects[obj.netId] = obj;
            return obj.netId;
        }

        // Drops all current netId -> object mappings, e.g. before re-populating them from
        // a freshly loaded .bbb file (whose RemoveAll() just destroyed every previously
        // registered object anyway).
        public static void ClearNetworkedObjects() => _networkedObjects.Clear();

        // Registers an object spawned while loading a .bbb file under a netId both clients
        // derive the same way (its record index + 1), so it participates in
        // selection/transform/delete sync just like a live-placed prop, even though no
        // PropPlaced negotiation ever happened for it. GenerateNetId's GUID-derived ids
        // occupy the full 64-bit range, so collisions with these small sequential ids are
        // astronomically unlikely.
        public static void RegisterLoadedNetworkedObject(ulong netId, LevelEditorObject obj)
        {
            if (obj == null || netId == 0) return;
            obj.netId = netId;
            _networkedObjects[netId] = obj;
        }

        // Broadcasts that a prop was just placed via the level editor's drag-and-drop
        // palette, so peers can spawn the same prop at the same transform and track it
        // under the same netId for future transform/selection sync. Payload layout:
        //   [marker][netId:ulong LE][idLen:ushort LE][idBytes UTF8][pos.xyz][rot.xyzw][scale.xyz] (floats LE)
        public static void SendPropPlaced(ulong netId, string propId, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (_channel == null) return;
            try
            {
                var idBytes = System.Text.Encoding.UTF8.GetBytes(propId);
                var payload = new byte[1 + 8 + 2 + idBytes.Length + 4 * (3 + 4 + 3)];
                int o = 0;
                payload[o++] = PropPlacedMarker;
                WriteULong(payload, ref o, netId);
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

                ChannelSend(payload, reliable: true);
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

                ChannelSend(payload, !active || reliable);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendFreecamUpdate failed: {ex.Message}");
            }
        }

        private static void ChannelSend(byte[] payload, bool reliable)
        {
            if (_channel == null || _channelSendMethod == null) return;
            try
            {
                _channelSendMethod.Invoke(_channel, new object[] { payload, reliable ? _packetDeliveryReliable : _packetDeliveryUnreliable });
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] Send failed: {ex.Message}");
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

        private static void WriteULong(byte[] buf, ref int offset, ulong value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buf, offset, 8);
            offset += 8;
        }

        private static ulong ReadULong(byte[] buf, ref int offset)
        {
            ulong value = BitConverter.ToUInt64(buf, offset);
            offset += 8;
            return value;
        }

        private static void OnDataReceived(byte senderUuid, byte[] payload)
        {
            if (payload == null || payload.Length < 1) return;

            // While a level transfer is in progress, buffer live prop mutations so they
            // aren't silently destroyed when LoadFromNetworkData calls RemoveAll().
            // LevelTransfer and freecam/ghost/selection packets are not buffered —
            // they're either idempotent or don't depend on object existence.
            if (_levelTransferPending)
            {
                byte marker = payload[0];
                if (marker == PropPlacedMarker   || marker == PropDeletedMarker ||
                    marker == LevelClearedMarker  || marker == MaterialAppliedMarker ||
                    marker == GroupSyncMarker)
                {
                    _bufferedLivePackets.Add((senderUuid, payload));
                    return;
                }
            }

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

                case PropTransformMarker:
                    HandlePropTransformUpdate(senderUuid, payload);
                    break;

                case PropSelectedMarker:
                    HandlePropSelected(senderUuid, payload);
                    break;

                case PropGhostUpdateMarker:
                    HandlePropGhostUpdate(senderUuid, payload);
                    break;

                case PropGhostEndMarker:
                    RemotePropGhostManager.NotifyDragEnded(senderUuid);
                    break;

                case PropDeletedMarker:
                    HandlePropDeleted(senderUuid, payload);
                    break;

                case MaterialAppliedMarker:
                    HandleMaterialApplied(senderUuid, payload);
                    break;

                case LevelClearedMarker:
                    HandleLevelCleared(senderUuid, payload);
                    break;

                case GroupSyncMarker:
                    HandleGroupSync(senderUuid, payload);
                    break;

                case BaseMapStateMarker:
                    HandleBaseMapState(senderUuid, payload);
                    break;

                case LevelTransferRequestMarker:
                    HandleLevelTransferRequest(senderUuid, payload);
                    break;

                case LevelTransferDataMarker:
                    HandleLevelTransferData(senderUuid, payload);
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
                ulong netId = ReadULong(payload, ref o);
                int idLen = payload[o] | (payload[o + 1] << 8);
                o += 2;
                string propId = System.Text.Encoding.UTF8.GetString(payload, o, idLen);
                o += idLen;

                var position = new Vector3(ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o));
                var rotation  = new Quaternion(ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o));
                var scale     = new Vector3(ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o));

                LevelEditor.EnsureManager();

                var info = PropLibrary.FindById(propId);
                if (info == null && propId.StartsWith("gpui://", StringComparison.OrdinalIgnoreCase)
                    && GpuiPropScanner.GpuiScannedNames.Count == 0)
                {
                    GpuiPropScanner.ScanGpuiProps();
                    info = PropLibrary.FindById(propId);
                    // Invalidate so the async scan re-verifies VerifiedSourceMaterials against
                    // fully-loaded assets. This also sets MaterialSourcesLoaded=true immediately
                    // (via the coroutine's first MoveNext), preventing EnsureMaterialSources from
                    // running synchronously during the SpawnFromPropInfo call below (which would
                    // load source prefabs in an early/incomplete state and cache broken materials).
                    MaterialCatalog.InvalidateMaterialSources();
                }
                if (info == null)
                {
                    MelonLogger.Warning($"[BabyBlocks][ModNetworking] Player {senderUuid} placed unknown prop '{propId}'");
                    return;
                }

                PropLibrary.LoadPropData(info);

                var obj = LevelEditorManager.Instance.SpawnFromPropInfo(info, position);
                if (obj == null) return;

                obj.transform.SetPositionAndRotation(position, rotation);
                obj.transform.localScale = scale;

                // SpawnFromPropInfo's InitializeLoopBase ran before the transform above was
                // applied (so its loopBaseRotation/Scale are stale, and chunk data was
                // computed from the pre-sync transform) - re-sync against the final transform
                // so this copy loops/chunks identically to the placing client's.
                LevelEditorManager.Instance.SyncLoopBase(obj);

                if (netId != 0)
                {
                    obj.netId = netId;
                    _networkedObjects[netId] = obj;
                }

                // The real prop has now appeared - remove the sender's in-progress ghost
                // preview so it isn't left behind alongside the placed prop, and briefly
                // suppress further ghost updates from this sender. PropPlaced is
                // ReliableOrdered while ghost updates are mostly Unreliable, so a few
                // late in-flight ghost packets can otherwise arrive afterwards and
                // resurrect a duplicate ghost next to the now-real prop.
                RemotePropGhostManager.NotifyDragEnded(senderUuid);

                MelonLogger.Msg($"[BabyBlocks] Player {senderUuid} placed prop '{propId}'");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandlePropPlaced failed: {ex.Message}");
            }
        }

        // Broadcasts a transform update for a networked prop (move/rotate/scale), e.g. during
        // an active drag. Sent Unreliable at high frequency with periodic ReliableOrdered
        // keyframes (mirroring SendFreecamUpdate), plus always-reliable on drag release.
        // Payload layout: [marker][netId:ulong LE][pos.xyz][rot.xyzw][scale.xyz] (floats LE)
        public static void SendPropTransform(ulong netId, Vector3 position, Quaternion rotation, Vector3 scale, bool reliable = false)
        {
            if (_channel == null || netId == 0) return;
            try
            {
                var payload = new byte[1 + 8 + 4 * (3 + 4 + 3)];
                int o = 0;
                payload[o++] = PropTransformMarker;
                WriteULong(payload, ref o, netId);
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

                ChannelSend(payload, reliable);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendPropTransform failed: {ex.Message}");
            }
        }

        private static void HandlePropTransformUpdate(byte senderUuid, byte[] payload)
        {
            try
            {
                int o = 1;
                ulong netId = ReadULong(payload, ref o);
                var position = new Vector3(ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o));
                var rotation  = new Quaternion(ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o));
                var scale     = new Vector3(ReadFloat(payload, ref o), ReadFloat(payload, ref o), ReadFloat(payload, ref o));

                if (!_networkedObjects.TryGetValue(netId, out var obj) || obj == null)
                {
                    _networkedObjects.Remove(netId);
                    return;
                }

                obj.transform.SetPositionAndRotation(position, rotation);
                obj.transform.localScale = scale;
                LevelEditorManager.Instance?.SyncLoopBase(obj);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandlePropTransformUpdate failed: {ex.Message}");
            }
        }

        // Broadcasts that a networked prop was deleted, so peers remove their copy too.
        // Payload layout: [marker][netId:ulong LE]
        public static void SendPropDeleted(ulong netId)
        {
            if (_channel == null || netId == 0) return;
            _networkedObjects.Remove(netId);
            try
            {
                var payload = new byte[1 + 8];
                int o = 0;
                payload[o++] = PropDeletedMarker;
                WriteULong(payload, ref o, netId);

                ChannelSend(payload, reliable: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendPropDeleted failed: {ex.Message}");
            }
        }

        private static void HandlePropDeleted(byte senderUuid, byte[] payload)
        {
            try
            {
                int o = 1;
                ulong netId = ReadULong(payload, ref o);

                if (!_networkedObjects.TryGetValue(netId, out var obj) || obj == null)
                {
                    _networkedObjects.Remove(netId);
                    return;
                }

                _networkedObjects.Remove(netId);
                LevelEditor.RemoveDeletedObject(obj);
                LevelEditorManager.Instance?.Remove(obj);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandlePropDeleted failed: {ex.Message}");
            }
        }

        // Broadcasts that a peer dragged a material construction onto a networked prop, so
        // peers re-skin their copy of that prop the same way. Only synced for props that
        // already have a netId (i.e. placed/received over the network this session) -
        // applying to a non-networked prop is a purely local edit. Payload layout:
        //   [marker][netId:ulong LE][materialConstructionId:int LE]
        public static void SendMaterialApplied(ulong netId, int materialConstructionId)
        {
            if (_channel == null || netId == 0) return;
            try
            {
                var payload = new byte[1 + 8 + 4];
                int o = 0;
                payload[o++] = MaterialAppliedMarker;
                WriteULong(payload, ref o, netId);
                Buffer.BlockCopy(BitConverter.GetBytes(materialConstructionId), 0, payload, o, 4);

                ChannelSend(payload, reliable: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendMaterialApplied failed: {ex.Message}");
            }
        }

        // Broadcasts that the local player pressed the Clear Level button, so peers wipe
        // their copy of the level too. Only sent for the manual button press - loading a
        // level, undo/redo, etc. are not broadcast. Payload layout: [marker]
        public static void SendLevelCleared()
        {
            _networkedObjects.Clear();
            if (_channel == null) return;
            try
            {
                ChannelSend(new byte[] { LevelClearedMarker }, reliable: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendLevelCleared failed: {ex.Message}");
            }
        }

        private static void HandleLevelCleared(byte senderUuid, byte[] payload)
        {
            try
            {
                _networkedObjects.Clear();
                LevelEditorManager.Instance?.RemoveAll();
                LevelEditor.ClearAllSelectionState();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandleLevelCleared failed: {ex.Message}");
            }
        }

        // Broadcasts that the local player grouped or ungrouped a selection of static
        // (logically-grouped, non-physics) networked props, so peers apply the same
        // groupId grouping/dissolution to their copies. Only props with a netId
        // (placed/received over the network this session) are sent. Up to 255 netIds.
        // Payload layout: [marker][group:byte][count:byte][netId1..netIdN:ulong LE]
        public static void SendGroupSync(List<ulong> netIds, bool group)
        {
            if (_channel == null || netIds == null) return;
            var ids = new List<ulong>();
            foreach (var id in netIds)
                if (id != 0) ids.Add(id);
            if (ids.Count == 0) return;

            try
            {
                int n = Math.Min(ids.Count, 255);
                var payload = new byte[1 + 1 + 1 + n * 8];
                int o = 0;
                payload[o++] = GroupSyncMarker;
                payload[o++] = (byte)(group ? 1 : 0);
                payload[o++] = (byte)n;
                for (int i = 0; i < n; i++)
                    WriteULong(payload, ref o, ids[i]);

                ChannelSend(payload, reliable: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendGroupSync failed: {ex.Message}");
            }
        }

        private static void HandleGroupSync(byte senderUuid, byte[] payload)
        {
            try
            {
                int o = 1;
                bool group = payload[o++] != 0;
                int n = payload[o++];

                var targets = new List<LevelEditorObject>(n);
                for (int i = 0; i < n; i++)
                {
                    ulong netId = ReadULong(payload, ref o);
                    if (_networkedObjects.TryGetValue(netId, out var obj) && obj != null)
                        targets.Add(obj);
                }
                if (targets.Count == 0) return;

                if (group)
                {
                    var existingGroups = new HashSet<int>();
                    foreach (var t in targets)
                        if (t.groupId > 0) existingGroups.Add(t.groupId);
                    foreach (var gid in existingGroups) GroupManager.DissolveGroup(gid);

                    int groupId = GroupManager.AllocateGroupId();
                    foreach (var obj in targets) obj.groupId = groupId;
                    GroupManager.EnsureStaticGroupRoot(groupId, targets);
                }
                else
                {
                    var groupsToClear = new HashSet<int>();
                    foreach (var t in targets)
                        if (t.groupId > 0) groupsToClear.Add(t.groupId);
                    foreach (var gid in groupsToClear) GroupManager.DissolveGroup(gid);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandleGroupSync failed: {ex.Message}");
            }
        }

        // Broadcasts that the local player toggled the Base Map on/off, so peers apply the
        // same toggle. Payload layout: [marker][enabled:byte]
        public static void SendBaseMapState(bool enabled)
        {
            if (_channel == null) return;
            try
            {
                ChannelSend(new byte[] { BaseMapStateMarker, (byte)(enabled ? 1 : 0) }, reliable: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendBaseMapState failed: {ex.Message}");
            }
        }

        private static void HandleBaseMapState(byte senderUuid, byte[] payload)
        {
            try
            {
                bool enabled = payload[1] != 0;
                if (BaseMapController.BaseMapEnabled == enabled) return;
                BaseMapController.SetBaseMapEnabled(enabled);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandleBaseMapState failed: {ex.Message}");
            }
        }

        // Broadcasts that this client wants to receive the current level from any peer
        // who has one. Sent once per connect (in TryCreateChannel).
        private static void SendLevelTransferRequest()
        {
            if (_channel == null) return;
            try
            {
                ChannelSend(new byte[] { LevelTransferRequestMarker }, reliable: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendLevelTransferRequest failed: {ex.Message}");
            }
        }

        // Broadcasts the current level (serialized BBB without baked data) to all peers,
        // split into LevelChunkSize-byte chunks. Peers reassemble and load once all chunks
        // arrive. Payload per chunk: [marker][totalLen:int32][totalChunks:uint16][chunkIndex:uint16][data]
        private static void SendLevelTransferData(byte[] data)
        {
            if (_channel == null || data == null || data.Length == 0) return;
            try
            {
                int totalChunks = (data.Length + LevelChunkSize - 1) / LevelChunkSize;
                for (int i = 0; i < totalChunks; i++)
                {
                    int offset   = i * LevelChunkSize;
                    int chunkLen = Math.Min(LevelChunkSize, data.Length - offset);
                    var payload  = new byte[1 + 4 + 2 + 2 + chunkLen];
                    int o = 0;
                    payload[o++] = LevelTransferDataMarker;
                    Buffer.BlockCopy(BitConverter.GetBytes(data.Length), 0, payload, o, 4); o += 4;
                    Buffer.BlockCopy(BitConverter.GetBytes((ushort)totalChunks), 0, payload, o, 2); o += 2;
                    Buffer.BlockCopy(BitConverter.GetBytes((ushort)i), 0, payload, o, 2); o += 2;
                    Buffer.BlockCopy(data, offset, payload, o, chunkLen);
                    ChannelSend(payload, reliable: true);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendLevelTransferData failed: {ex.Message}");
            }
        }

        // Responds to a peer's LevelTransferRequest with our current level. Uses the
        // pre-clear snapshot taken in TryCreateChannel if we have no objects yet (i.e.
        // our own request hasn't been answered), otherwise serializes the live scene.
        private static void HandleLevelTransferRequest(byte senderUuid, byte[] payload)
        {
            try
            {
                byte[] data = null;
                var mgr = LevelEditorManager.Instance;
                if (mgr != null && mgr.Objects.Count > 0)
                    data = LevelSaveLoad.SerializeForNetwork();
                data ??= _pendingLevelSnapshot;
                if (data == null) return;
                SendLevelTransferData(data);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandleLevelTransferRequest failed: {ex.Message}");
            }
        }

        // Receives one chunk of level data. Buffers chunks per sender and loads the level
        // once all chunks for a transfer have arrived.
        private static void HandleLevelTransferData(byte senderUuid, byte[] payload)
        {
            try
            {
                // Header: [marker(1)][totalLen(4)][totalChunks(2)][chunkIndex(2)][data]
                if (payload.Length < 9) return;
                int   totalLen    = BitConverter.ToInt32(payload, 1);
                int   totalChunks = BitConverter.ToUInt16(payload, 5);
                int   chunkIndex  = BitConverter.ToUInt16(payload, 7);
                int   chunkDataLen = payload.Length - 9;

                if (totalLen <= 0 || totalChunks <= 0 || chunkIndex >= totalChunks || chunkDataLen <= 0) return;

                // Start or reset the assembly buffer when a new transfer begins.
                if (!_levelChunkStates.TryGetValue(senderUuid, out var state)
                    || state.Chunks.Length != totalChunks)
                {
                    state = new LevelChunkState
                    {
                        Chunks        = new byte[totalChunks][],
                        ReceivedCount = 0,
                        TotalLen      = totalLen,
                    };
                    _levelChunkStates[senderUuid] = state;
                }

                if (state.Chunks[chunkIndex] == null)
                {
                    var chunk = new byte[chunkDataLen];
                    Buffer.BlockCopy(payload, 9, chunk, 0, chunkDataLen);
                    state.Chunks[chunkIndex] = chunk;
                    state.ReceivedCount++;
                }

                if (state.ReceivedCount < totalChunks) return;

                // All chunks received — reassemble and load.
                _levelChunkStates.Remove(senderUuid);
                var assembled = new byte[state.TotalLen];
                int pos = 0;
                foreach (var chunk in state.Chunks)
                {
                    Buffer.BlockCopy(chunk, 0, assembled, pos, chunk.Length);
                    pos += chunk.Length;
                }

                _pendingLevelSnapshot = null;
                LevelEditor.EnsureManager();
                var (ok, count, error) = LevelSaveLoad.LoadFromNetworkData(assembled);
                if (ok)
                    MelonLogger.Msg($"[BabyBlocks] Received level from player {senderUuid}: {count} object(s)");
                else
                    MelonLogger.Warning($"[BabyBlocks] Level transfer from player {senderUuid} failed: {error}");

                // Level is loaded — stop buffering, hide the overlay, and replay any
                // prop mutations that arrived while the transfer was in flight.
                _levelTransferPending = false;
                FlyCamController.EndNetworkLevelTransfer();
                if (_bufferedLivePackets.Count > 0)
                {
                    var buffered = new List<(byte sender, byte[] payload)>(_bufferedLivePackets);
                    _bufferedLivePackets.Clear();
                    MelonLogger.Msg($"[BabyBlocks] Replaying {buffered.Count} buffered live event(s) after level transfer");
                    foreach (var (sender, pkt) in buffered)
                        OnDataReceived(sender, pkt);
                }
                else
                {
                    _bufferedLivePackets.Clear();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandleLevelTransferData failed: {ex.Message}");
            }
        }

        // Serializes and broadcasts the current level to all connected peers. Call this
        // after a successful local file load so everyone loads the same level.
        public static void BroadcastLevelLoad()
        {
            if (_channel == null) return;
            var data = LevelSaveLoad.SerializeForNetwork();
            if (data == null) return;
            SendLevelTransferData(data);
            MelonLogger.Msg($"[BabyBlocks] Broadcast level load: {data.Length} bytes");
        }

        private static void HandleMaterialApplied(byte senderUuid, byte[] payload)
        {
            try
            {
                int o = 1;
                ulong netId = ReadULong(payload, ref o);
                int materialConstructionId = BitConverter.ToInt32(payload, o);

                if (!_networkedObjects.TryGetValue(netId, out var obj) || obj == null)
                {
                    _networkedObjects.Remove(netId);
                    return;
                }

                var entry = MaterialConstructionLibrary.FindById(materialConstructionId);
                if (entry == null) return;

                MaterialConstructionPanel.ApplyToInstance(obj, entry);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandleMaterialApplied failed: {ex.Message}");
            }
        }

        // Broadcasts the local player's current selection (of networked props) so peers can
        // show a highlight around each in our suit color. An empty list means "nothing
        // selected" / selection cleared. Up to 255 netIds are sent. Payload layout:
        //   [marker][count:byte][netId1..netIdN:ulong LE][color.rgb bytes (only if count > 0)]
        public static void SendPropSelected(List<ulong> netIds)
        {
            if (_channel == null) return;
            try
            {
                int n = netIds == null ? 0 : Math.Min(netIds.Count, 255);
                Color color = Color.white;
                if (n > 0) TryGetLocalAppearance(out color, out _);

                var payload = new byte[1 + 1 + n * 8 + (n > 0 ? 3 : 0)];
                int o = 0;
                payload[o++] = PropSelectedMarker;
                payload[o++] = (byte)n;
                for (int i = 0; i < n; i++)
                    WriteULong(payload, ref o, netIds[i]);
                if (n > 0)
                {
                    payload[o++] = (byte)Mathf.Clamp(Mathf.RoundToInt(color.r * 255f), 0, 255);
                    payload[o++] = (byte)Mathf.Clamp(Mathf.RoundToInt(color.g * 255f), 0, 255);
                    payload[o++] = (byte)Mathf.Clamp(Mathf.RoundToInt(color.b * 255f), 0, 255);
                }

                ChannelSend(payload, reliable: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendPropSelected failed: {ex.Message}");
            }
        }

        private static void HandlePropSelected(byte senderUuid, byte[] payload)
        {
            try
            {
                int o = 1;
                int n = payload[o++];

                var targets = new List<LevelEditorObject>(n);
                for (int i = 0; i < n; i++)
                {
                    ulong netId = ReadULong(payload, ref o);
                    if (_networkedObjects.TryGetValue(netId, out var obj) && obj != null)
                        targets.Add(obj);
                }

                if (targets.Count == 0)
                {
                    RemotePropHighlightManager.Remove(senderUuid);
                    return;
                }

                var color = new Color(payload[o++] / 255f, payload[o++] / 255f, payload[o++] / 255f);
                RemotePropHighlightManager.SetHighlights(senderUuid, targets, color);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandlePropSelected failed: {ex.Message}");
            }
        }

        // Broadcasts the live position/rotation/scale of a prop being dragged out of the
        // palette (before it's dropped), so peers can show a matching ghost preview. Sent at
        // the same cadence as SendPropTransform. Payload layout:
        //   [marker][idLen:ushort LE][idBytes UTF8][pos.xyz][rot.xyzw][scale.xyz] (floats LE)
        public static void SendPropGhostUpdate(string propId, Vector3 position, Quaternion rotation, Vector3 scale, bool reliable)
        {
            if (_channel == null) return;
            try
            {
                var idBytes = System.Text.Encoding.UTF8.GetBytes(propId);
                var payload = new byte[1 + 2 + idBytes.Length + 4 * (3 + 4 + 3)];
                int o = 0;
                payload[o++] = PropGhostUpdateMarker;
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

                ChannelSend(payload, reliable);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendPropGhostUpdate failed: {ex.Message}");
            }
        }

        // Sent when a peer's in-progress palette placement ends without placing a prop
        // (e.g. dropped over UI), so peers remove the ghost preview.
        public static void SendPropGhostEnd()
        {
            if (_channel == null) return;
            try
            {
                ChannelSend(new byte[] { PropGhostEndMarker }, reliable: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] SendPropGhostEnd failed: {ex.Message}");
            }
        }

        private static void HandlePropGhostUpdate(byte senderUuid, byte[] payload)
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
                if (info == null && propId.StartsWith("gpui://", StringComparison.OrdinalIgnoreCase)
                    && GpuiPropScanner.GpuiScannedNames.Count == 0)
                {
                    GpuiPropScanner.ScanGpuiProps();
                    info = PropLibrary.FindById(propId);
                    MaterialCatalog.InvalidateMaterialSources();
                }
                if (info == null) return;

                RemotePropGhostManager.UpdateGhost(senderUuid, info, position, rotation, scale);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BabyBlocks][ModNetworking] HandlePropGhostUpdate failed: {ex.Message}");
            }
        }
    }
}
