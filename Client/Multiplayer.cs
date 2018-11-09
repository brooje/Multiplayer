﻿extern alias zip;

using Harmony;
using Ionic.Zlib;
using LiteNetLib;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.Serialization;
using UnityEngine;
using Verse;
using Verse.Profile;
using Verse.Sound;
using Verse.Steam;
using zip::Ionic.Zip;

namespace Multiplayer.Client
{
    [StaticConstructorOnStartup]
    public static class Multiplayer
    {
        public static readonly int ProtocolVersion = 1;

        public static MultiplayerSession session;
        public static MultiplayerGame game;

        public static IConnection Client => session?.client;
        public static MultiplayerServer LocalServer => session?.localServer;
        public static ChatWindow Chat => session?.chat;
        public static PacketLogWindow PacketLog => session?.packetLog;
        public static bool IsReplay => session?.replay ?? false;

        public static string username;
        public static HarmonyInstance harmony => MultiplayerModInstance.harmony;

        public static bool reloading;
        public static bool simulating;

        public static FactionDef FactionDef = FactionDef.Named("MultiplayerColony");
        public static FactionDef DummyFactionDef = FactionDef.Named("MultiplayerDummy");
        public static IncidentDef AuroraIncident = IncidentDef.Named("Aurora");

        public static IdBlock GlobalIdBlock => game.worldComp.globalIdBlock;
        public static Faction DummyFaction => game.dummyFaction;
        public static MultiplayerWorldComp WorldComp => game.worldComp;

        public static List<ulong> mapSeeds = new List<ulong>();

        // Null during loading
        public static Faction RealPlayerFaction
        {
            get => Client != null ? game.RealPlayerFaction : Faction.OfPlayer;
            set => game.RealPlayerFaction = value;
        }

        public static bool ExecutingCmds => MultiplayerWorldComp.executingCmdWorld || MapAsyncTimeComp.executingCmdMap != null;
        public static bool Ticking => MultiplayerWorldComp.tickingWorld || MapAsyncTimeComp.tickingMap != null || ConstantTicker.ticking;

        public static bool dontSync;
        public static bool ShouldSync => Client != null && !Ticking && !ExecutingCmds && !reloading && Current.ProgramState == ProgramState.Playing && LongEventHandler.currentEvent == null && !dontSync;

        public static string ReplaysDir => GenFilePaths.FolderUnderSaveData("MpReplays");

        public static Callback<P2PSessionRequest_t> sessionReqCallback;
        public static Callback<P2PSessionConnectFail_t> p2pFail;
        public static Callback<FriendRichPresenceUpdate_t> friendRchpUpdate;
        public static Callback<GameRichPresenceJoinRequested_t> gameJoinReq;
        public static Callback<PersonaStateChange_t> personaChange;
        public static AppId_t RimWorldAppId;

        public const string SteamConnectStart = " -mpserver=";

        static Multiplayer()
        {
            if (GenCommandLine.CommandLineArgPassed("profiler"))
                SimpleProfiler.CheckAvailable();

            MpLog.info = str => Log.Message($"{username} {Current.Game?.CurrentMap?.AsyncTime()?.mapTicks.ToString() ?? ""} {str}");
            MpLog.error = str => Log.Error(str);

            GenCommandLine.TryGetCommandLineArg("username", out username);
            if (username == null)
                username = SteamUtility.SteamPersonaName;
            if (username == "???")
                username = "Player" + Rand.Range(0, 9999);

            SimpleProfiler.Init(username);

            if (SteamManager.Initialized)
                InitSteam();

            Log.Message($"Player's username: {username}");
            Log.Message($"Processor: {SystemInfo.processorType}");

            PlantWindSwayPatch.Init();

            var gameobject = new GameObject();
            gameobject.AddComponent<OnMainThread>();
            UnityEngine.Object.DontDestroyOnLoad(gameobject);

            MpConnectionState.SetImplementation(ConnectionStateEnum.ClientSteam, typeof(ClientSteamState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ClientJoining, typeof(ClientJoiningState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ClientPlaying, typeof(ClientPlayingState));

            harmony.DoAllMpPatches();

            SyncHandlers.Init();
            Sync.RegisterAllSyncMethods();

            DoPatches();

            Log.messageQueue.maxMessages = 1000;

            if (GenCommandLine.CommandLineArgPassed("dev"))
            {
                Current.Game = new Game();
                Current.Game.InitData = new GameInitData
                {
                    gameToLoad = "mappo"
                };

                LongEventHandler.QueueLongEvent(null, "Play", "LoadingLongEvent", true, null);
            }
            else if (GenCommandLine.TryGetCommandLineArg("connect", out string ip))
            {
                if (String.IsNullOrEmpty(ip))
                    ip = "127.0.0.1";

                LongEventHandler.QueueLongEvent(() =>
                {
                    IPAddress.TryParse(ip, out IPAddress addr);
                    ClientUtil.TryConnect(addr, MultiplayerServer.DefaultPort);
                }, "Connecting", false, null);
            }
        }

        private static void InitSteam()
        {
            RimWorldAppId = SteamUtils.GetAppID();

            sessionReqCallback = Callback<P2PSessionRequest_t>.Create(req =>
            {
                if (session != null && session.allowSteam && !session.pendingSteam.Contains(req.m_steamIDRemote))
                {
                    session.pendingSteam.Add(req.m_steamIDRemote);
                    SteamFriends.RequestUserInformation(req.m_steamIDRemote, true);
                }
            });

            friendRchpUpdate = Callback<FriendRichPresenceUpdate_t>.Create(update =>
            {
            });

            gameJoinReq = Callback<GameRichPresenceJoinRequested_t>.Create(req =>
            {
            });

            personaChange = Callback<PersonaStateChange_t>.Create(change =>
            {
            });

            p2pFail = Callback<P2PSessionConnectFail_t>.Create(fail =>
            {
                if (session == null) return;

                CSteamID remoteId = fail.m_steamIDRemote;
                EP2PSessionError error = (EP2PSessionError)fail.m_eP2PSessionError;

                if (Client is SteamConnection clientConn && clientConn.remoteId == remoteId)
                {
                    session.disconnectNetReason = error == EP2PSessionError.k_EP2PSessionErrorTimeout ? "Connection timed out" : "Connection error";
                    ConnectionStatusListeners.All.Do(a => a.Disconnected());
                    OnMainThread.StopMultiplayer();
                }

                if (LocalServer == null) return;

                LocalServer.Enqueue(() =>
                {
                    ServerPlayer player = LocalServer.FindPlayer(p => p.conn is SteamConnection conn && conn.remoteId == remoteId);
                    if (player != null)
                        LocalServer.OnDisconnected(player.conn);
                });
            });
        }

        public static XmlDocument SaveAndReload()
        {
            /*if (serverThread != null)
            {
                Map map = Find.Maps[0];
                List<Region> regions = new List<Region>(map.regionGrid.AllRegions);
                foreach (Region region in regions)
                {
                    region.id = map.cellIndices.CellToIndex(region.AnyCell);
                    region.cachedCellCount = -1;
                    region.precalculatedHashCode = Gen.HashCombineInt(region.id, 1295813358);
                    region.closedIndex = new uint[RegionTraverser.NumWorkers];

                    if (region.Room != null)
                    {
                        region.Room.ID = region.id;
                        region.Room.Group.ID = region.id;
                    }
                }

                StringBuilder builder = new StringBuilder();
                SimpleProfiler.DumpMemory(Find.Maps[0].spawnedThings, builder);
                File.WriteAllText("memory_save", builder.ToString());
            }*/

            XmlDocument SaveGame()
            {
                //SaveCompression.doSaveCompression = true;

                ScribeUtil.StartWritingToDoc();

                Scribe.EnterNode("savegame");
                ScribeMetaHeaderUtility.WriteMetaHeader();
                Scribe.EnterNode("game");
                int currentMapIndex = Current.Game.currentMapIndex;
                Scribe_Values.Look(ref currentMapIndex, "currentMapIndex", -1);
                Current.Game.ExposeSmallComponents();
                World world = Current.Game.World;
                Scribe_Deep.Look(ref world, "world");
                List<Map> maps = Find.Maps;
                Scribe_Collections.Look(ref maps, "maps", LookMode.Deep);
                Find.CameraDriver.Expose();
                Scribe.ExitNode();

                SaveCompression.doSaveCompression = false;

                return ScribeUtil.FinishWritingToDoc();
            }

            reloading = true;

            WorldGrid worldGridSaved = Find.WorldGrid;
            WorldRenderer worldRendererSaved = Find.World.renderer;
            var tweenedPos = new Dictionary<int, Vector3>();
            var drawers = new Dictionary<int, MapDrawer>();
            int localFactionId = RealPlayerFaction.loadID;

            //RealPlayerFaction = DummyFaction;

            foreach (Map map in Find.Maps)
            {
                drawers[map.uniqueID] = map.mapDrawer;
                //RebuildRegionsAndRoomsPatch.copyFrom[map.uniqueID] = map.regionGrid;

                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                    tweenedPos[p.thingIDNumber] = p.drawer.tweener.tweenedPos;
            }

            Stopwatch watch = Stopwatch.StartNew();
            XmlDocument gameDoc = SaveGame();
            Log.Message("Saving took " + watch.ElapsedMilliseconds);

            MapDrawerRegenPatch.copyFrom = drawers;
            WorldGridCtorPatch.copyFrom = worldGridSaved;
            WorldRendererCtorPatch.copyFrom = worldRendererSaved;

            LoadInMainThread(gameDoc);

            RealPlayerFaction = Find.FactionManager.GetById(localFactionId);

            foreach (Map m in Find.Maps)
            {
                foreach (Pawn p in m.mapPawns.AllPawnsSpawned)
                {
                    if (tweenedPos.TryGetValue(p.thingIDNumber, out Vector3 v))
                    {
                        p.drawer.tweener.tweenedPos = v;
                        p.drawer.tweener.lastDrawFrame = Time.frameCount;
                    }
                }
            }

            SaveCompression.doSaveCompression = false;
            reloading = false;

            /*if (serverThread != null)
            {
                Map map = Find.Maps[0];
                List<Region> regions = new List<Region>(map.regionGrid.AllRegions);
                foreach (Region region in regions)
                {
                    region.id = map.cellIndices.CellToIndex(region.AnyCell);
                    region.cachedCellCount = -1;
                    region.precalculatedHashCode = Gen.HashCombineInt(region.id, 1295813358);
                    region.closedIndex = new uint[RegionTraverser.NumWorkers];

                    if (region.Room != null)
                    {
                        region.Room.ID = region.id;
                        region.Room.Group.ID = region.id;
                    }
                }

                TickList[] tickLists = new[] {
                    Find.Maps[0].AsyncTime().tickListLong,
                    Find.Maps[0].AsyncTime().tickListRare,
                    Find.Maps[0].AsyncTime().tickListNormal
                };

                foreach (TickList list in tickLists)
                {
                    for (int i = 0; i < list.thingsToRegister.Count; i++)
                    {
                        list.BucketOf(list.thingsToRegister[i]).Add(list.thingsToRegister[i]);
                    }
                    list.thingsToRegister.Clear();
                    for (int j = 0; j < list.thingsToDeregister.Count; j++)
                    {
                        list.BucketOf(list.thingsToDeregister[j]).Remove(list.thingsToDeregister[j]);
                    }
                    list.thingsToDeregister.Clear();
                }

                StringBuilder builder = new StringBuilder();
                SimpleProfiler.DumpMemory(Find.Maps[0].spawnedThings, builder);
                File.WriteAllText("memory_reload", builder.ToString());
            }*/

            return gameDoc;
        }

        public static void LoadInMainThread(XmlDocument gameDoc)
        {
            var watch = Stopwatch.StartNew();
            MemoryUtility.ClearAllMapsAndWorld();

            LoadPatch.gameToLoad = gameDoc;

            CancelRootPlayStartLongEvents.cancel = true;
            Find.Root.Start();
            CancelRootPlayStartLongEvents.cancel = false;

            SavedGameLoaderNow.LoadGameFromSaveFileNow(null);

            Log.Message("Loading took " + watch.ElapsedMilliseconds);
        }

        public static void CacheAndSendGameData(XmlDocument doc)
        {
            XmlNode gameNode = doc.DocumentElement["game"];
            XmlNode mapsNode = gameNode["maps"];

            var mapsData = new Dictionary<int, byte[]>();

            foreach (XmlNode mapNode in mapsNode)
            {
                int id = int.Parse(mapNode["uniqueID"].InnerText);
                byte[] mapData = ScribeUtil.XmlToByteArray(mapNode);
                OnMainThread.cachedMapData[id] = mapData;
                mapsData[id] = mapData;
            }

            gameNode["currentMapIndex"].RemoveFromParent();
            mapsNode.RemoveAll();

            byte[] gameData = ScribeUtil.XmlToByteArray(doc);
            OnMainThread.cachedAtTime = TickPatch.Timer;
            OnMainThread.cachedGameData = gameData;

            ThreadPool.QueueUserWorkItem(c =>
            {
                foreach (var mapData in mapsData)
                {
                    byte[] compressedMaps = GZipStream.CompressBuffer(mapData.Value);
                    Client.SendFragmented(Packets.Client_AutosavedData, ByteWriter.GetBytes(0, compressedMaps, mapData.Key));
                }

                byte[] compressedGame = GZipStream.CompressBuffer(gameData);
                Client.SendFragmented(Packets.Client_AutosavedData, ByteWriter.GetBytes(1, compressedGame));
            });
        }

        private static void DoPatches()
        {
            harmony.PatchAll();

            // General designation handling
            {
                var designatorMethods = new[] { "DesignateSingleCell", "DesignateMultiCell", "DesignateThing" };

                foreach (Type t in typeof(Designator).AllSubtypesAndSelf())
                {
                    foreach (string m in designatorMethods)
                    {
                        MethodInfo method = t.GetMethod(m, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                        if (method == null) continue;

                        MethodInfo prefix = AccessTools.Method(typeof(DesignatorPatches), m);
                        harmony.Patch(method, new HarmonyMethod(prefix), null, null);
                    }
                }
            }

            // Remove side effects from methods which are non-deterministic during ticking (e.g. camera dependent motes and sound effects)
            {
                var randPatchPrefix = new HarmonyMethod(typeof(RandPatches).GetMethod("Prefix"));
                var randPatchPostfix = new HarmonyMethod(typeof(RandPatches).GetMethod("Postfix"));

                var subSustainerCtor = typeof(SubSustainer).GetConstructor(new[] { typeof(Sustainer), typeof(SubSoundDef) });
                var sampleCtor = typeof(Sample).GetConstructor(new[] { typeof(SubSoundDef) });
                var subSoundPlay = typeof(SubSoundDef).GetMethod("TryPlay");
                var effecterTick = typeof(Effecter).GetMethod("EffectTick");
                var effecterTrigger = typeof(Effecter).GetMethod("Trigger");
                var randomBoltMesh = typeof(LightningBoltMeshPool).GetProperty("RandomBoltMesh").GetGetMethod();

                var effectMethods = new MethodBase[] { subSustainerCtor, sampleCtor, subSoundPlay, effecterTick, effecterTrigger, randomBoltMesh };
                var moteMethods = typeof(MoteMaker).GetMethods(BindingFlags.Static | BindingFlags.Public);

                foreach (MethodBase m in effectMethods.Concat(moteMethods))
                    harmony.Patch(m, randPatchPrefix, randPatchPostfix);
            }

            // Set ThingContext and FactionContext (for pawns and buildings) in common Thing methods
            {
                var thingMethodPrefix = new HarmonyMethod(typeof(PatchThingMethods).GetMethod("Prefix"));
                var thingMethodPostfix = new HarmonyMethod(typeof(PatchThingMethods).GetMethod("Postfix"));
                var thingMethods = new[] { "Tick", "TickRare", "TickLong", "SpawnSetup", "TakeDamage", "Kill" };

                foreach (Type t in typeof(Thing).AllSubtypesAndSelf())
                {
                    foreach (string m in thingMethods)
                    {
                        MethodInfo method = t.GetMethod(m, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                        if (method != null)
                            harmony.Patch(method, thingMethodPrefix, thingMethodPostfix);
                    }
                }
            }

            // Full precision floating point saving
            {
                var doubleSavePrefix = new HarmonyMethod(typeof(ValueSavePatch).GetMethod(nameof(ValueSavePatch.DoubleSave_Prefix)));
                var floatSavePrefix = new HarmonyMethod(typeof(ValueSavePatch).GetMethod(nameof(ValueSavePatch.FloatSave_Prefix)));
                var valueSaveMethod = typeof(Scribe_Values).GetMethod(nameof(Scribe_Values.Look));

                harmony.Patch(valueSaveMethod.MakeGenericMethod(typeof(double)), doubleSavePrefix, null);
                harmony.Patch(valueSaveMethod.MakeGenericMethod(typeof(float)), floatSavePrefix, null);
            }

            // Set the map time for GUI methods depending on it
            {
                var setMapTimePrefix = new HarmonyMethod(AccessTools.Method(typeof(SetMapTimeForUI), "Prefix"));
                var setMapTimePostfix = new HarmonyMethod(AccessTools.Method(typeof(SetMapTimeForUI), "Postfix"));

                var mapInterfaceMethods = new[]
                {
                    nameof(MapInterface.MapInterfaceOnGUI_BeforeMainTabs),
                    nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs),
                    nameof(MapInterface.HandleMapClicks),
                    nameof(MapInterface.HandleLowPriorityInput),
                    nameof(MapInterface.MapInterfaceUpdate)
                };

                foreach (string m in mapInterfaceMethods)
                    harmony.Patch(AccessTools.Method(typeof(MapInterface), m), setMapTimePrefix, setMapTimePostfix);

                var windowMethods = new[] { "DoWindowContents", "WindowUpdate" };

                foreach (string m in windowMethods)
                    harmony.Patch(typeof(MainTabWindow_Inspect).GetMethod(m), setMapTimePrefix, setMapTimePostfix);

                foreach (Type t in typeof(InspectTabBase).AllSubtypesAndSelf())
                {
                    MethodInfo method = t.GetMethod("FillTab", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (method != null && !method.IsAbstract)
                        harmony.Patch(method, setMapTimePrefix, setMapTimePostfix);
                }

                harmony.Patch(AccessTools.Method(typeof(SoundRoot), nameof(SoundRoot.Update)), setMapTimePrefix, setMapTimePostfix);
            }
        }

        public static void ExposeIdBlock(ref IdBlock block, string label)
        {
            if (Scribe.mode == LoadSaveMode.Saving && block != null)
            {
                string base64 = Convert.ToBase64String(block.Serialize());
                Scribe_Values.Look(ref base64, label);
            }

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                string base64 = null;
                Scribe_Values.Look(ref base64, label);

                if (base64 != null)
                    block = IdBlock.Deserialize(new ByteReader(Convert.FromBase64String(base64)));
                else
                    block = null;
            }
        }

        public static void HandleReceive(byte[] data)
        {
            try
            {
                Client.HandleReceive(data);
            }
            catch (Exception e)
            {
                Log.Error($"Exception handling packet by {Client}: {e}");
            }
        }

        public static void LoadReplay(string name, bool toEnd = false, Action after = null)
        {
            session = new MultiplayerSession();
            session.client = new ReplayConnection();
            session.client.State = ConnectionStateEnum.ClientPlaying;
            session.replay = true;

            var replay = Replay.ForLoading(name);
            replay.LoadInfo();
            replay.LoadCurrentData(0);
            // todo ensure everything is read correctly

            session.myFactionId = replay.info.playerFaction;
            session.replayTimerStart = replay.info.sections[0].start;

            int tickUntil = replay.info.sections[0].end;
            session.replayTimerEnd = tickUntil;
            TickPatch.tickUntil = tickUntil;

            TickPatch.skipTo = toEnd ? tickUntil : session.replayTimerStart;
            TickPatch.afterSkip = after;

            ClientJoiningState.ReloadGame(OnMainThread.cachedMapData.Keys.ToList());
        }
    }

    public class Replay
    {
        public readonly string fileName;
        public ReplayInfo info = new ReplayInfo();

        private Replay(string fileName)
        {
            this.fileName = fileName;
        }

        private ZipFile ZipFile => new ZipFile(Path.Combine(Multiplayer.ReplaysDir, $"{fileName}.zip"));

        public void WriteCurrentData()
        {
            string sectionId = info.sections.Count.ToString("D3");

            using (var zip = ZipFile)
            {
                foreach (var kv in OnMainThread.cachedMapData)
                    zip.AddEntry($"maps/{sectionId}_{kv.Key}_save", kv.Value);

                foreach (var kv in OnMainThread.cachedMapCmds)
                    if (kv.Key >= 0)
                        zip.AddEntry($"maps/{sectionId}_{kv.Key}_cmds", SerializeCmds(kv.Value));

                if (OnMainThread.cachedMapCmds.TryGetValue(ScheduledCommand.Global, out var worldCmds))
                    zip.AddEntry($"world/{sectionId}_cmds", SerializeCmds(worldCmds));

                zip.AddEntry($"world/{sectionId}_save", OnMainThread.cachedGameData);
                zip.Save();
            }

            info.sections.Add(new ReplaySection(OnMainThread.cachedAtTime, TickPatch.Timer));
            WriteInfo();
        }

        private byte[] SerializeCmds(List<ScheduledCommand> cmds)
        {
            ByteWriter writer = new ByteWriter();

            writer.WriteInt32(cmds.Count);
            foreach (var cmd in cmds)
                writer.WritePrefixedBytes(cmd.Serialize());

            return writer.GetArray();
        }

        private List<ScheduledCommand> DeserializeCmds(byte[] data)
        {
            var reader = new ByteReader(data);

            int count = reader.ReadInt32();
            var result = new List<ScheduledCommand>(count);
            for (int i = 0; i < count; i++)
                result.Add(ScheduledCommand.Deserialize(new ByteReader(reader.ReadPrefixedBytes())));

            return result;
        }

        public void WriteInfo()
        {
            using (var zip = ZipFile)
            {
                zip.UpdateEntry("info", DirectXmlSaver.XElementFromObject(info, typeof(ReplayInfo)).ToString());
                zip.Save();
            }
        }

        public void LoadInfo()
        {
            using (var zip = ZipFile)
            {
                var doc = ScribeUtil.LoadDocument(zip["info"].GetBytes());
                info = DirectXmlToObject.ObjectFromXml<ReplayInfo>(doc.DocumentElement, true);
            }
        }

        public void LoadCurrentData(int sectionId)
        {
            string sectionIdStr = sectionId.ToString("D3");

            using (var zip = ZipFile)
            {
                foreach (var mapCmds in zip.SelectEntries($"name = maps/{sectionIdStr}_*_cmds"))
                {
                    int mapId = int.Parse(mapCmds.FileName.Split('_')[1]);
                    OnMainThread.cachedMapCmds[mapId] = DeserializeCmds(mapCmds.GetBytes());
                }

                foreach (var mapSave in zip.SelectEntries($"name = maps/{sectionIdStr}_*_save"))
                {
                    int mapId = int.Parse(mapSave.FileName.Split('_')[1]);
                    OnMainThread.cachedMapData[mapId] = mapSave.GetBytes();
                }

                var worldCmds = zip[$"world/{sectionIdStr}_cmds"];
                if (worldCmds != null)
                    OnMainThread.cachedMapCmds[ScheduledCommand.Global] = DeserializeCmds(worldCmds.GetBytes());

                OnMainThread.cachedGameData = zip[$"world/{sectionIdStr}_save"].GetBytes();
            }
        }

        public static Replay ForLoading(string fileName)
        {
            return new Replay(fileName);
        }

        public static Replay ForLoading(FileInfo file)
        {
            return new Replay(Path.GetFileNameWithoutExtension(file.Name));
        }

        public static Replay ForSaving(string fileName)
        {
            var replay = new Replay(fileName);
            replay.info.name = $"{Multiplayer.username}'s {Find.World.info.name}";
            replay.info.playerFaction = Multiplayer.session.myFactionId;

            return replay;
        }
    }

    public class ReplayInfo
    {
        public string name;
        public int playerFaction;

        public List<ReplaySection> sections = new List<ReplaySection>();
        public List<ReplayCheckpoint> checkpoints = new List<ReplayCheckpoint>();
    }

    public class ReplaySection
    {
        public int start;
        public int end;

        public ReplaySection()
        {
        }

        public ReplaySection(int start, int end)
        {
            this.start = start;
            this.end = end;
        }
    }

    public class ReplayCheckpoint
    {
        public string name;
        public double time;
        public Color color;
    }

    public class MultiplayerSession
    {
        public IConnection client;
        public NetManager netClient;
        public ChatWindow chat = new ChatWindow();
        public PacketLogWindow packetLog = new PacketLogWindow();
        public int myFactionId;

        public bool replay;
        public int replayTimerStart = -1;
        public int replayTimerEnd = -1;

        public string disconnectNetReason;
        public string disconnectServerReason;

        public bool allowSteam;
        public List<CSteamID> pendingSteam = new List<CSteamID>();

        public MultiplayerServer localServer;
        public Thread serverThread;

        public void Stop()
        {
            if (client != null)
                client.State = ConnectionStateEnum.Disconnected;

            if (localServer != null)
            {
                localServer.running = false;
                serverThread.Join();
            }

            if (netClient != null)
                netClient.Stop();

            Log.Message("Multiplayer session stopped.");
        }
    }

    public class MultiplayerGame
    {
        public MultiplayerWorldComp worldComp;
        public SharedCrossRefs sharedCrossRefs = new SharedCrossRefs();

        public Faction dummyFaction;
        private Faction myFaction;

        public Faction RealPlayerFaction
        {
            get => myFaction;

            set
            {
                myFaction = value;
                Faction.OfPlayer.def = Multiplayer.FactionDef;
                value.def = FactionDefOf.PlayerColony;
                Find.FactionManager.ofPlayer = value;

                worldComp.SetFaction(value);

                foreach (Map m in Find.Maps)
                    m.MpComp().SetFaction(value);
            }
        }

        public MultiplayerGame()
        {
            Toils_Ingest.cardinals = GenAdj.CardinalDirections.ToList();
            Toils_Ingest.diagonals = GenAdj.DiagonalDirections.ToList();
            GenAdj.adjRandomOrderList = null;
            CellFinder.mapEdgeCells = null;
            CellFinder.mapSingleEdgeCells = new List<IntVec3>[4];

            TradeSession.trader = null;
            TradeSession.playerNegotiator = null;
            TradeSession.deal = null;
            TradeSession.giftMode = false;
        }
    }

    public class Container<T>
    {
        private readonly T _value;
        public T Value => _value;

        public Container(T value)
        {
            _value = value;
        }

        public static implicit operator Container<T>(T value)
        {
            return new Container<T>(value);
        }
    }

    public class Container<T, U>
    {
        private readonly T _first;
        private readonly U _second;

        public T First => _first;
        public U Second => _second;

        public Container(T first, U second)
        {
            _first = first;
            _second = second;
        }
    }

    public class MultiplayerModInstance : Mod
    {
        public static HarmonyInstance harmony = HarmonyInstance.Create("multiplayer");

        public MultiplayerModInstance(ModContentPack pack) : base(pack)
        {
            EarlyMarkNoInline();
        }

        public void EarlyMarkNoInline()
        {
            foreach (var type in MpUtil.AllModTypes())
            {
                MpPatchExtensions.DoMpPatches(null, type)?.ForEach(m => MpUtil.MarkNoInlining(m));

                var harmonyMethods = type.GetHarmonyMethods();
                if (harmonyMethods?.Count > 0)
                {
                    var original = new PatchProcessor(harmony, type, HarmonyMethod.Merge(harmonyMethods)).GetOriginalMethod();
                    if (original != null)
                        MpUtil.MarkNoInlining(original);
                }
            }
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
        }

        public override string SettingsCategory() => "Multiplayer";
    }

    public class OnMainThread : MonoBehaviour
    {
        public static ActionQueue queue = new ActionQueue();

        public static int cachedAtTime;
        public static byte[] cachedGameData;
        public static Dictionary<int, byte[]> cachedMapData = new Dictionary<int, byte[]>();

        // Global cmds are -1
        public static Dictionary<int, List<ScheduledCommand>> cachedMapCmds = new Dictionary<int, List<ScheduledCommand>>();

        public void Update()
        {
            Multiplayer.session?.netClient?.PollEvents();

            queue.RunQueue();

            if (Multiplayer.Client == null) return;

            if (SteamManager.Initialized)
                UpdateSteam();

            UpdateSync();
        }

        private Stopwatch lastSteamUpdate = Stopwatch.StartNew();

        private void UpdateSteam()
        {
            if (lastSteamUpdate.ElapsedMilliseconds > 1000)
            {
                if (Multiplayer.session.allowSteam)
                    SteamFriends.SetRichPresence("connect", Multiplayer.SteamConnectStart + "" + SteamUser.GetSteamID());
                else
                    SteamFriends.SetRichPresence("connect", null);

                lastSteamUpdate.Restart();
            }

            while (SteamNetworking.IsP2PPacketAvailable(out uint size))
            {
                byte[] data = new byte[size];
                SteamNetworking.ReadP2PPacket(data, size, out uint sizeRead, out CSteamID remote);
                HandleSteamPacket(remote, data);
            }
        }

        private void HandleSteamPacket(CSteamID remote, byte[] data)
        {
            if (Multiplayer.Client is SteamConnection localConn && localConn.remoteId == remote)
                Multiplayer.HandleReceive(data);

            if (Multiplayer.LocalServer == null) return;

            Multiplayer.LocalServer.Enqueue(() =>
            {
                ServerPlayer player = Multiplayer.LocalServer.FindPlayer(p => p.conn is SteamConnection conn && conn.remoteId == remote);

                if (player == null)
                {
                    IConnection conn = new SteamConnection(remote);
                    conn.State = ConnectionStateEnum.ServerSteam;
                    player = Multiplayer.LocalServer.OnConnected(conn);
                }

                player.HandleReceive(data);
            });
        }

        private void UpdateSync()
        {
            foreach (SyncField f in Sync.bufferedFields)
            {
                if (f.inGameLoop) continue;

                Sync.bufferedChanges[f].RemoveAll((k, data) =>
                {
                    if (CheckShouldRemove(f, k, data))
                        return true;

                    if (Utils.MillisNow - data.timestamp > 200)
                    {
                        f.DoSync(k.first, data.toSend, k.second);
                        data.sent = true;
                        data.timestamp = Utils.MillisNow;
                    }

                    return false;
                });
            }
        }

        public static bool CheckShouldRemove(SyncField field, Pair<object, object> target, BufferData data)
        {
            if (Equals(data.toSend, data.actualValue))
                return true;

            object currentValue = target.first.GetPropertyOrField(field.memberPath, target.second);

            if (!Equals(currentValue, data.actualValue))
            {
                if (data.sent)
                    return true;
                else
                    data.actualValue = currentValue;
            }

            return false;
        }

        public void OnApplicationQuit()
        {
            StopMultiplayer();
        }

        public static void StopMultiplayer()
        {
            if (Multiplayer.session != null)
            {
                Multiplayer.session.Stop();
                Multiplayer.session = null;
            }

            TickPatch.skipTo = -1;
            TickPatch.skipToTickUntil = false;
            TickPatch.afterSkip = null;

            Find.WindowStack?.WindowOfType<ServerBrowser>()?.Cleanup(true);

            foreach (var entry in Sync.bufferedChanges)
                entry.Value.Clear();

            ClearCaches();
        }

        public static void ClearCaches()
        {
            cachedAtTime = 0;
            cachedGameData = null;
            cachedMapData.Clear();
            cachedMapCmds.Clear();
        }

        public static void Enqueue(Action action)
        {
            queue.Enqueue(action);
        }

        public static void ScheduleCommand(ScheduledCommand cmd)
        {
            MpLog.Log($"Cmd: {cmd.type}, faction: {cmd.factionId}, map: {cmd.mapId}, ticks: {cmd.ticks}");
            cachedMapCmds.GetOrAddNew(cmd.mapId).Add(cmd);

            if (Current.ProgramState != ProgramState.Playing) return;

            if (cmd.mapId == ScheduledCommand.Global)
                Multiplayer.WorldComp.cmds.Enqueue(cmd);
            else
                cmd.GetMap()?.AsyncTime().cmds.Enqueue(cmd);
        }
    }

    public static class FactionContext
    {
        private static Stack<Faction> stack = new Stack<Faction>();

        public static Faction Push(Faction faction)
        {
            if (faction == null || (faction.def != Multiplayer.FactionDef && faction.def != FactionDefOf.PlayerColony))
            {
                stack.Push(null);
                return null;
            }

            stack.Push(Find.FactionManager.OfPlayer);
            Set(faction);
            return faction;
        }

        public static Faction Pop()
        {
            Faction f = stack.Pop();
            if (f != null)
                Set(f);
            return f;
        }

        private static void Set(Faction faction)
        {
            Find.FactionManager.OfPlayer.def = Multiplayer.FactionDef;
            faction.def = FactionDefOf.PlayerColony;
            Find.FactionManager.ofPlayer = faction;
        }
    }

    public class ReplayConnection : IConnection
    {
        public override void SendRaw(byte[] raw)
        {
        }

        public override void HandleReceive(byte[] rawData)
        {
        }

        public override void Close()
        {
        }
    }

}

