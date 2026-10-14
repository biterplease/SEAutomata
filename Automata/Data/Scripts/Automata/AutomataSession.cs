using ProtoBuf;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using Sandbox.ModAPI.Interfaces;
using VRage.ObjectBuilders;
using System;
using System.Collections.Generic;
using System.IO;
using VRage;
using VRage.Game.Components;
using VRage.Utils;
using VRageMath;
using VRage.Game;

using Automata.Config;
using Automata.Util.Logging;
using Automata.VirtualNetwork;
using Automata.Logistics;
using Automata.Drone;
using Automata.Construction;
using Automata.Util;
using Sandbox.Game.Entities;

namespace Automata
{

    /// <summary>Item groups, in the order item lists show them.</summary>
    public enum ItemCategory : byte
    {
        Component = 0,
        Tool = 1,
        PlayerWeapon = 2,
        PlayerAmmo = 3,
        BlockAmmo = 4,
        Ingot = 5,
        Ore = 6,
        Other = 7,
    }

    /// <summary>A physical item definition (despite the name: every item, not only components).</summary>
    public struct ComponentData
    {
        public MyDefinitionId DefinitionId;
        public string DisplayName;
        public MyStringHash Slug;
        public float Mass;
        public float Volume;
        /// <summary>Inventory key (<see cref="Inventory.ItemKeys"/>): "SteelPlate", "Ingot/Iron", ...</summary>
        public MyStringHash Key;
        public ItemCategory Category;
        /// <summary>Listed to players (public, a known category).</summary>
        public bool Listed;
    }
    public struct BlockData
    {
        public MyDefinitionId DefinitionId;
        public string DisplayName;
        public MyCubeSize CubeSize;

    }

    /// <summary>
    /// Ore item definitions (physical items of type <see cref="MyObjectBuilder_Ore"/>).
    /// </summary>
    public struct OreDefinitionData
    {
        public MyDefinitionId DefinitionId;
        public string DisplayName;
        public MyStringHash Slug;
        public float Mass;
        public float Volume;
    }

    /// <summary>A block drawing debug overlays from the session's Draw() (clients): drone areas / routes, construction computer areas.</summary>
    public interface IDebugDrawable
    {
        /// <summary>Draws; false to stop being called.</summary>
        bool DrawDebug();
        void StopDebugDraw();
    }

    public enum ShareWith : byte
    {
        NoOne = 0,
        Friends = 1,
        Faction = 2,
        Neutrals = 4,
        Enemies = 8,
    }
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class AutomataSession : MySessionComponentBase
    {
        #region UI strings
        private static readonly MyStringId StrUpdateError = MyStringId.GetOrCompute("Update error: {0}");
        #endregion

        public const string VERSION = "v0.0.1";
        public const string MOD_NAME = "Automata";
        public static readonly Guid MOD_GUID = new Guid("1CFDA990-FD26-4950-A127-7BBC99FF1397");
        public static AutomataSession Instance;
        private static ServerConfig _serverConfig;

        // Shared message queue for all AI components
        public static MessageQueue _messageQueue;
        public MessageQueue MessageQueue => _messageQueue;

        // Mod-wide settings and management
        private bool isInitialized = false;
        private long _lastUpdateFrame = 0;
        private int _updateInterval; // DON'T initialize here - will be set in Init()
        private int updateCounter = 0;
        #region Timing
        private const int UPDATE_INTERVAL = 60;
        #endregion

        // _messageQueue
        private int messageQueueCleanupIntervalTicks = 600;
        private int lastMessageQueueCleanupFrame = 0;

        /// <summary>
        /// All components: steel plate, metal grid, etc.
        /// For use in logistic computer list filters.
        /// </summary>
        public static readonly Dictionary<MyStringHash, ComponentData> componentLookup = new Dictionary<MyStringHash, ComponentData>(MyStringHash.Comparer);
        public Dictionary<MyStringHash, ComponentData> ComponentLookup => componentLookup;
        public static readonly Dictionary<MyStringHash, BlockData> blockLookup = new Dictionary<MyStringHash, BlockData>(MyStringHash.Comparer);
        public Dictionary<MyStringHash, BlockData> BlockLookup => blockLookup;

        /// <summary>
        /// All ore definitions (Iron Ore, Silicon Ore, etc.) for mining surveyor filters and mass lookups.
        /// </summary>
        private static bool itemsIndexed;
        public static readonly Dictionary<MyStringHash, OreDefinitionData> oreLookup = new Dictionary<MyStringHash, OreDefinitionData>(MyStringHash.Comparer);
        public Dictionary<MyStringHash, OreDefinitionData> OreLookup => oreLookup;

        /// <summary>
        /// Listed items, sorted: components, tools, player weapons, player weapon ammo, block ammo, ingots, ores
        /// (display name within each). Logistics computer item lists.
        /// </summary>
        public static readonly List<ComponentData> itemCatalog = new List<ComponentData>();
        public List<ComponentData> ItemCatalog => itemCatalog;
        private static readonly Dictionary<string, MyStringHash> itemNames = new Dictionary<string, MyStringHash>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// All blocks: steel plate, metal grid, etc.
        /// For use in scheduler grind and weld list filters.
        /// </summary>
        public Dictionary<long, string> AllBlocks;

        /// <summary>
        /// Owner/faction-filtered connector and beacon lists, cached per drone. See <see cref="AnchorDirectory"/>.
        /// </summary>
        public readonly AnchorDirectory Anchors = new AnchorDirectory();

        // Drones that asked to draw debug geometry (observation area). Draw() runs on clients only.
        private readonly HashSet<IDebugDrawable> drawRequests = new HashSet<IDebugDrawable>();
        private readonly List<IDebugDrawable> drawSnapshot = new List<IDebugDrawable>();

        public void RequestDraw(IDebugDrawable drone, bool enabled)
        {
            if (drone == null) return;
            if (enabled) drawRequests.Add(drone);
            else drawRequests.Remove(drone);
        }

        public override void Draw()
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;
            try
            {
                if (isInitialized) Pointer.ScoutPointer.Draw();   // designator knobs and lights
            }
            catch (Exception ex)
            {
                Log.Error("Pointer: draw error: {0}", ex);
            }
            if (drawRequests.Count == 0) return;
            try
            {
                drawSnapshot.Clear();
                drawSnapshot.AddRange(drawRequests);   // a drone may unregister itself while drawing
                for (int i = 0; i < drawSnapshot.Count; i++)
                {
                    if (!drawSnapshot[i].DrawDebug())
                        drawRequests.Remove(drawSnapshot[i]);
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"Automata: Draw exception: {ex}");
                foreach (var drone in drawRequests)
                    drone.StopDebugDraw();
                drawRequests.Clear();
            }
        }

        /// <summary>
        /// Multiplayer packets (Digi's NetworkLib). Channel id must be unique among all mods in a world.
        /// </summary>
        public const ushort NETWORK_CHANNEL = 21843;
        public Digi.NetworkLib.Network Net { get; private set; }

        // Chat commands (/automata ...), clients only
        private readonly AutomataSessionCommandHandler commands = new AutomataSessionCommandHandler();

        public static ServerConfig GetConfig() => _serverConfig;
        public static MessageQueue GetMessageQueue() => _messageQueue;

        public override void LoadData()
        {
            Instance = this;
            Net = new Digi.NetworkLib.Network(NETWORK_CHANNEL, MOD_NAME);
            Util.NicknameControl.Register();
            if (!MyAPIGateway.Utilities.IsDedicated) MyAPIGateway.Utilities.MessageEntered += commands.MessageEntered;
            // Localization settings
            // LoadLangOverrides();
            // MyAPIGateway.Gui.GuiControlRemoved += GuiControlRemoved;
        }
        public AutomataSession()
        {
            try
            {
                MyLog.Default.WriteLineAndConsole("Automata: Instance constructor called");
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLineAndConsole($"Automata: Instance constructor failed: {ex}");
            }
        }

        private void Init()
        {
            try
            {
                Log.Initialize(MOD_NAME, 0, "Automata.log", typeof(AutomataSession));
                Log.Info("=== Automata Initializing ===");
                Pointer.ScoutPointer.Load(ModContext != null ? ModContext.ModPath : null);

                _serverConfig = ServerConfig.Instance;
                _serverConfig.LoadConfig();
                Log.Verbose("Server config loaded.");
                Pathfinding.PathfindingManager.Load(_serverConfig.Pathfinding);
                MessageQueue.Init(_serverConfig.MessageQueue);
                Log.Verbose("Message queue initialized.");
                _messageQueue = MessageQueue.Instance;
                // In-flight messages are saved by the blocks that sent them (their save data), not by the session
                Log.Info("Log level: {0}", _serverConfig.Logging.LogLevel.ToString());

                _updateInterval = _serverConfig.Session.UpdateInterval;

                Log.Info("Mod loaded successfully");
                Log.Info("Update interval: {0} ticks", _updateInterval);
                LogisticsComputerTerminalControls.DoOnce(ModContext);
                // DroneControllerTerminalControls.DoOnce(ModContext);

                QueueTerminalDumps();   // property ids to the SE log (see LogTerminalControls)


                isInitialized = true;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"Automata: Init exception: {ex}");
            }
        }


        // Terminal controls / actions of block types, written once to the SE log (Space Engineers' own log file): the
        // property ids we read or switch on blocks we don't own the code of (AI blocks: "ActivateBehavior"...). A
        // type's controls exist only once a block of that type was created, so an empty list is retried later.
        #region Timing
        private const int TERMINAL_DUMP_RETRY_TICKS = 600;
        #endregion
        private const int TERMINAL_DUMP_MAX_TRIES = 60;
        private sealed class TerminalDump { public string Title; public Func<int> Dump; public int Tries; }
        private readonly List<TerminalDump> terminalDumps = new List<TerminalDump>();
        private int lastTerminalDumpFrame;

        private void QueueTerminalDumps()
        {
            terminalDumps.Add(new TerminalDump { Title = "AI DEFENSIVE (IMyDefensiveCombatBlock)", Dump = () => LogTerminalControls<IMyDefensiveCombatBlock>("AI DEFENSIVE (IMyDefensiveCombatBlock)") });
            terminalDumps.Add(new TerminalDump { Title = "AI OFFENSIVE (IMyOffensiveCombatBlock)", Dump = () => LogTerminalControls<IMyOffensiveCombatBlock>("AI OFFENSIVE (IMyOffensiveCombatBlock)") });
            terminalDumps.Add(new TerminalDump { Title = "AI FLIGHT (IMyFlightMovementBlock)", Dump = () => LogTerminalControls<IMyFlightMovementBlock>("AI FLIGHT (IMyFlightMovementBlock)") });
            terminalDumps.Add(new TerminalDump { Title = "PROGRAMMABLE BLOCK", Dump = () => LogTerminalControls<IMyProgrammableBlock>("PROGRAMMABLE BLOCK") });
            terminalDumps.Add(new TerminalDump { Title = "REMOTE CONTROL", Dump = () => LogTerminalControls<IMyRemoteControl>("REMOTE CONTROL") });
        }

        private void RunTerminalDumps(int now)
        {
            if (terminalDumps.Count == 0 || now - lastTerminalDumpFrame < TERMINAL_DUMP_RETRY_TICKS)
                return;
            lastTerminalDumpFrame = now;
            for (int i = terminalDumps.Count - 1; i >= 0; i--)
            {
                var d = terminalDumps[i];
                int count = 0;
                try { count = d.Dump(); }
                catch (Exception ex) { MyLog.Default.WriteLine("Automata: terminal dump " + d.Title + " failed: " + ex.Message); }
                if (count > 0 || ++d.Tries >= TERMINAL_DUMP_MAX_TRIES)
                {
                    if (count == 0) MyLog.Default.WriteLine("Automata: [DEV] " + d.Title + ": no terminal controls (no such block was created this session)");
                    terminalDumps.RemoveAt(i);
                }
            }
        }

        // Returns how many controls there were (0: the type's controls aren't created yet; nothing written)
        private static int LogTerminalControls<T>(string title)
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<T>(out controls);
            if (controls == null || controls.Count == 0) return 0;
            MyLog.Default.WriteLine("Automata: [DEV] ===== " + title + " TERMINAL CONTROLS (" + controls.Count + ") =====");
            foreach (IMyTerminalControl c in controls)
            {
                var titled = c as IMyTerminalControlTitleTooltip;
                string name = titled != null ? MyTexts.GetString(titled.Title) : "N/A";
                var property = c as ITerminalProperty;
                string valueType = property != null ? property.TypeName : "N/A";
                MyLog.Default.WriteLine("Automata: [DEV] terminal property: id='" + c.Id + "'; type='" + c.GetType().Name + "'; valueType='" + valueType
                                        + "'; displayName='" + name + "'");
            }
            List<IMyTerminalAction> actions;
            MyAPIGateway.TerminalControls.GetActions<T>(out actions);
            if (actions != null)
                foreach (IMyTerminalAction a in actions)
                    MyLog.Default.WriteLine("Automata: [DEV] toolbar action: id='" + a.Id + "'; displayName='" + a.Name + "'");
            MyLog.Default.WriteLine("Automata: [DEV] ===== end of " + title + " =====");
            return controls.Count;
        }

        // Clients, every frame (also paused): scout pointer input
        public override void HandleInput()
        {
            try
            {
                if (isInitialized) Pointer.ScoutPointer.HandleInput();
            }
            catch (Exception ex)
            {
                Log.Error("Pointer: input error: {0}", ex);
            }
        }

        public override void UpdateBeforeSimulation()
        {
            try
            {
                if (!isInitialized)
                {
                    if (MyAPIGateway.Session == null)
                        return;
                    Log.Info("Initializing Automata Session");
                    Init();
                    return;
                }

                // Once (it ran every tick when one of the tables stayed empty: 86k "Items indexed" lines in a 25 min log)
                if (!itemsIndexed || componentLookup.Count == 0)
                {
                    itemsIndexed = true;
                    LoadComponentsAndBlocks();
                }
                Pathfinding.PathfindingManager.Instance?.Update();   // look-ahead ray budget
                Pointer.ScoutPointer.Update();                       // pointers' magazines kept full (no vanilla reload)
                if (MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer) Pointer.ScoutPointer.UpdateServer();
                RunTerminalDumps(MyAPIGateway.Session.GameplayFrameCounter);
                if (MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer) Util.PerfStats.Update(MyAPIGateway.Session.GameplayFrameCounter);

                var currentFrame = MyAPIGateway.Session.GameplayFrameCounter;
                if ((currentFrame - lastMessageQueueCleanupFrame) > messageQueueCleanupIntervalTicks)
                {
                    lastMessageQueueCleanupFrame = currentFrame;
                    _messageQueue.TryPerformCleanup();
                }
                if (_messageQueue != null && (MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer))
                    _messageQueue.Relays.Update(currentFrame);   // every REBUILD_TICKS
                if (currentFrame - _lastUpdateFrame < _updateInterval)
                {
                    return;
                }

                _lastUpdateFrame = currentFrame;


                // Optional: Log active AI block counts
                updateCounter++;
            }
            catch (Exception ex)
            {
                MyAPIGateway.Utilities.ShowMessage(MOD_NAME, Loc.F(StrUpdateError, ex.Message));
                MyLog.Default.WriteLine($"Automata: UpdateBeforeSimulation exception: {ex}");
            }
        }


        public override void SaveData()
        {
            try
            {
                if (MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer) Pathfinding.PathfindingManager.Instance?.SaveData();
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"Automata: SaveData exception: {ex}");
            }
        }

        protected override void UnloadData()
        {
            try
            {
                Pointer.ScoutPointer.Unload();
                Util.NicknameControl.Unregister();
                if (MyAPIGateway.Utilities != null) MyAPIGateway.Utilities.MessageEntered -= commands.MessageEntered;
                itemsIndexed = false;   // statics outlive the session (next world in the same game process)
                Pathfinding.PathfindingManager.Unload();
                Construction.BuildPlanner.ClearFrames();
                // Reset message queue singleton
                if (_messageQueue != null) _messageQueue.Reset();

                Anchors.Clear();
                drawRequests.Clear();
                Util.LcdFeedServer.Clear();
                Util.LcdFeedClient.Clear();
                Drone.DroneControllerBlock.ClearDroneRegistry();
                Net?.Dispose();
                Net = null;

                // Remove localization texts
                // MyAPIGateway.Gui.GuiControlRemoved -= GuiControlRemoved;

                Instance = null;

                if (Log.IsInitialized)
                {
                    Log.Info("Mod unloaded");
                    Log.Close();
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"Automata: UnloadData exception: {ex}");

                var writer = MyAPIGateway.Utilities.WriteFileInLocalStorage("shutdown_error.log", typeof(AutomataSession));
                if (writer != null)
                {
                    writer.Write(ex.ToString());
                    writer.Flush();
                    writer.Close();
                }
            }
        }

        private void LoadComponentsAndBlocks()
        {
            // 1. Ensure a clean slate
            componentLookup.Clear();
            blockLookup.Clear();
            oreLookup.Clear();
            itemCatalog.Clear();
            itemNames.Clear();
            ambiguousNames.Clear();

            // Ammo fired by hand-held weapons (the rest is block ammo)
            var playerAmmo = new HashSet<MyDefinitionId>();
            var physicalItems = MyDefinitionManager.Static.GetPhysicalItemDefinitions();
            foreach (var physItem in physicalItems)
            {
                var weaponItem = physItem as MyWeaponItemDefinition;
                if (weaponItem == null) continue;
                MyWeaponDefinition weapon;
                if (!MyDefinitionManager.Static.TryGetWeaponDefinition(weaponItem.WeaponDefinitionId, out weapon) || weapon.AmmoMagazinesId == null) continue;
                for (int i = 0; i < weapon.AmmoMagazinesId.Length; i++) playerAmmo.Add(weapon.AmmoMagazinesId[i]);
            }

            // 2. Populate dynamically from the Definition Manager
            foreach (var physItem in physicalItems)
            {
                if (physItem == null) continue;

                var key = Inventory.ItemKeys.Of(physItem.Id);
                var category = Categorize(physItem, playerAmmo);
                var data = new ComponentData
                {
                    DisplayName = physItem.DisplayNameText,
                    DefinitionId = physItem.Id,
                    Slug = MyStringHash.GetOrCompute(physItem.Id.SubtypeName),
                    Mass = physItem.Mass,
                    Volume = physItem.Volume,
                    Key = key,
                    Category = category,
                    Listed = physItem.Public && category != ItemCategory.Other,
                };
                componentLookup[key] = data;
                if (data.Listed) itemCatalog.Add(data);
                AddItemName(key.String, key);
                AddItemName(physItem.Id.SubtypeName, key);
                if (!string.IsNullOrEmpty(data.DisplayName)) AddItemName(data.DisplayName.Replace(" ", ""), key);
                if (physItem.Id.TypeId == typeof(MyObjectBuilder_Ore))
                {
                    MyStringHash slug = MyStringHash.GetOrCompute(physItem.Id.SubtypeName);
                    oreLookup[slug] = new OreDefinitionData
                    {
                        DisplayName = physItem.DisplayNameText,
                        DefinitionId = physItem.Id,
                        Slug = slug,
                        Mass = physItem.Mass,
                        Volume = physItem.Volume,
                    };
                }
            }
            var blockDefs = MyDefinitionManager.Static.GetAllDefinitions<MyCubeBlockDefinition>();
            foreach (var blockDef in blockDefs)
            {
                // blockDef.Id.SubtypeName is your slug here as well
                // blockDef.Size indicates if it's 1x1x1, 3x3x3, etc.
                // blockDef.CubeSize indicates Large or Small grid
                blockLookup[MyStringHash.GetOrCompute(blockDef.Id.SubtypeName)] = new BlockData{
                    DisplayName = blockDef.DisplayNameText,
                    DefinitionId = blockDef.Id,
                    CubeSize = blockDef.CubeSize
                };
            }
            itemCatalog.Sort((a, b) =>
            {
                int c = a.Category.CompareTo(b.Category);
                return c != 0 ? c : string.Compare(a.DisplayName ?? a.Key.String, b.DisplayName ?? b.Key.String, StringComparison.OrdinalIgnoreCase);
            });
            Log.Info("Items indexed: {0} ({1} listed), {2} ores, {3} blocks", componentLookup.Count, itemCatalog.Count, oreLookup.Count, blockLookup.Count);
        }

        private static ItemCategory Categorize(MyPhysicalItemDefinition item, HashSet<MyDefinitionId> playerAmmo)
        {
            var type = item.Id.TypeId;
            if (type == typeof(MyObjectBuilder_Component)) return ItemCategory.Component;
            if (type == typeof(MyObjectBuilder_Ingot)) return ItemCategory.Ingot;
            if (type == typeof(MyObjectBuilder_Ore)) return ItemCategory.Ore;
            if (type == typeof(MyObjectBuilder_AmmoMagazine)) return playerAmmo.Contains(item.Id) ? ItemCategory.PlayerAmmo : ItemCategory.BlockAmmo;
            // The industrial target designator is a tool, though held as a rifle (a weapon item definition: Data/PhysicalItems.sbc)
            if (type == typeof(MyObjectBuilder_PhysicalGunObject) && item.Id.SubtypeName == "AutomataIndustrialTargetDesignatorItem") return ItemCategory.Tool;
            if (type == typeof(MyObjectBuilder_PhysicalGunObject)) return item is MyWeaponItemDefinition ? ItemCategory.PlayerWeapon : ItemCategory.Tool;
            return ItemCategory.Other;
        }

        // Names an item is found by (key, subtype, display name without spaces); a name two items share is ambiguous
        private static void AddItemName(string name, MyStringHash key)
        {
            if (string.IsNullOrEmpty(name) || ambiguousNames.Contains(name)) return;
            MyStringHash existing;
            if (itemNames.TryGetValue(name, out existing))
            {
                if (existing == key) return;
                itemNames.Remove(name);
                ambiguousNames.Add(name);
                return;
            }
            itemNames[name] = key;
        }

        /// <summary>
        /// An item from what a player typed: its key ("SteelPlate", "Ingot/Iron"), subtype or display name, any case,
        /// spaces ignored. A name two items share ("Iron": ore and ingot) needs the key. False before definitions load.
        /// </summary>
        /// <summary>
        /// Items of one object builder type ("MyObjectBuilder_Ore", "MyObjectBuilder_SeedItem"...): (key, display name),
        /// sorted by name. Terminal lists (a drone's ignored ores / crops).
        /// </summary>
        public static void ListItemsOfType(string typeId, List<KeyValuePair<string, string>> result)
        {
            result.Clear();
            foreach (var d in componentLookup.Values)
            {
                if (d.DefinitionId.TypeId.ToString() != typeId) continue;
                result.Add(new KeyValuePair<string, string>(d.Key.String, string.IsNullOrEmpty(d.DisplayName) ? d.DefinitionId.SubtypeName : d.DisplayName));
            }
            result.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Display name of an item key ("Ore/Iron" -> "Iron Ore"), or the key itself.</summary>
        public static string ItemDisplayName(string key)
        {
            ComponentData d;
            if (componentLookup.TryGetValue(VRage.Utils.MyStringHash.GetOrCompute(key), out d) && !string.IsNullOrEmpty(d.DisplayName)) return d.DisplayName;
            return key;
        }

        public static bool TryResolveItem(string text, out ComponentData item)
        {
            item = default(ComponentData);
            if (string.IsNullOrWhiteSpace(text)) return false;
            string name = text.Trim();
            MyStringHash key;
            if (!itemNames.TryGetValue(name, out key) && !itemNames.TryGetValue(name.Replace(" ", ""), out key)) return false;
            return componentLookup.TryGetValue(key, out item);
        }

        // void LoadLangOverrides()
        // {
        //     string folder = Path.Combine(ModContext.ModPathData, "Localization");
        //     MyTexts.LoadTexts(folder, cultureName: "override", subcultureName: null);
        // }

        // void GuiControlRemoved(object screen)
        // {
        //     if (screen == null)
        //         return;

        //     // detect when options menu is closed in case player changes language
        //     if (screen.ToString().EndsWith("ScreenOptionsSpace"))
        //     {
        //         LoadLangOverrides();
        //     }
        // }
    }
}