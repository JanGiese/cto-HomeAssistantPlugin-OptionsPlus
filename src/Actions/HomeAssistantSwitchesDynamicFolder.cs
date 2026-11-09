namespace Loupedeck.HomeAssistantPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    using Loupedeck;
    using Loupedeck.HomeAssistantPlugin.Models;
    using Loupedeck.HomeAssistantPlugin.Services;

    public class HomeAssistantSwitchesDynamicFolder : PluginDynamicFolder
    {
        private record SwitchItem(
            String EntityId,
            String FriendlyName,
            String State,
            String DeviceId,
            String DeviceName,
            String Manufacturer,
            String Model
        );

        private readonly IconService _icons;

        // Command constants
        private const String CmdStatus = "status";
        private const String CmdRetry = "retry";
        private const String CmdBack = "back";
        private const String CmdArea = "area";

        private CancellationTokenSource _cts = new();

        private readonly Dictionary<String, SwitchItem> _switchesByEntity = new();

        // Navigation levels
        private enum ViewLevel { Root, Area, Device }
        private ViewLevel _level = ViewLevel.Root;

        private String? _currentAreaId = null; // when in Area view
        private String? _currentEntityId = null; // when in Device view

        // Area data
        private readonly Dictionary<String, String> _areaIdToName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<String, String> _entityToAreaId =
            new(StringComparer.OrdinalIgnoreCase);

        // Synthetic "no area" bucket
        private const String UnassignedAreaId = "!unassigned";

        // Action parameter prefixes
        private const String PfxDevice = "device:"; // device:<entity_id>
        private const String PfxActOn = "act:on:"; // act:on:<entity_id>
        private const String PfxActOff = "act:off:"; // act:off:<entity_id>

        // ====================================================================
        // CONSTANTS - All magic numbers extracted to named constants
        // ====================================================================

        // --- UI Display Constants ---
        private const Int32 UiPaddingSmall = 8;                // Small padding for UI elements
        private const Int32 UiPaddingMedium = 10;              // Medium padding for UI elements
        private const Int32 FontSizeSmall = 22;                // Small font size for UI text
        private const Int32 FontSizeMedium = 56;               // Medium font size for icons

        // --- Status Colors ---
        private const Int32 StatusOkRed = 0;                   // Online status red component
        private const Int32 StatusOkGreen = 160;               // Online status green component
        private const Int32 StatusOkBlue = 60;                 // Online status blue component
        private const Int32 StatusErrorRed = 200;              // Error status red component
        private const Int32 StatusErrorGreen = 30;             // Error status green component
        private const Int32 StatusErrorBlue = 30;              // Error status blue component

        // --- Network and Timing Constants ---
        private const Int32 AuthTimeoutSeconds = 60;           // Authentication timeout in seconds
        private static readonly TimeSpan EchoSuppressWindow = TimeSpan.FromSeconds(3); // Echo suppression window

        private readonly HaEventListener _events = new();
        private CancellationTokenSource _eventsCts = new();

        private ISwitchControlService? _switchSvc;
        private IHaClient? _ha; // adapter over HaWebSocketClient

        // --- Echo suppression: ignore HA frames shortly after we sent a command ---
        private readonly Dictionary<String, DateTime> _lastCmdAt =
            new(StringComparer.OrdinalIgnoreCase);

        // Service dependencies
        private IHomeAssistantDataService? _dataService;
        private IHomeAssistantDataParser? _dataParser;
        private ISwitchStateManager? _switchStateManager;
        private IRegistryService? _registryService;

        public HomeAssistantSwitchesDynamicFolder()
        {
            PluginLog.Info("[SwitchesDynamicFolder] Constructor START");

            this.DisplayName = "All Switch Controls";
            this.GroupName = "Switches";

            this._icons = new IconService(new Dictionary<String, String>
            {
                { IconId.Back,        "back_icon.svg" },
                { IconId.SwitchOn,    "switch_on_icon.svg" },
                { IconId.SwitchOff,   "switch_off_icon.svg" },
                { IconId.Retry,       "reload_icon.svg" },
                { IconId.Issue,       "issue_status_icon.svg" },
                { IconId.Online,      "online_status_icon.png" },
                { IconId.Area,        "area_icon.svg" },
                { IconId.Switch,      "switch_icon.svg" },
            });

            PluginLog.Info($"[SwitchesDynamicFolder] Constructor - this.Plugin is null: {this.Plugin == null}");
            PluginLog.Info("[SwitchesDynamicFolder] Constructor completed - dependency initialization deferred to OnLoad()");
        }

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType _) =>
            PluginDynamicFolderNavigation.None;

        public override String GetButtonDisplayName(PluginImageSize imageSize) => "All Switches";

        public override BitmapImage GetButtonImage(PluginImageSize imageSize)
        {
            try
            {
                // Use switch_icon as the primary icon for switches
                return PluginResources.ReadImage("multiple_switches_icon.svg");
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[GetButtonImage] Failed to load switch_icon.svg");
                // Return a fallback image if the primary icon fails to load
                try
                {
                    return PluginResources.ReadImage("switch_off_icon.svg");
                }
                catch (Exception fallbackEx)
                {
                    PluginLog.Error(fallbackEx, "[GetButtonImage] Failed to load fallback switch_off_icon.svg");
                    throw; // Let the framework handle the final failure
                }
            }
        }

        public override IEnumerable<String> GetButtonPressActionNames(DeviceType _)
        {
            // Always show Back + Status
            yield return this.CreateCommandName(CmdBack);
            yield return this.CreateCommandName(CmdStatus);

            if (this._level == ViewLevel.Device && !String.IsNullOrEmpty(this._currentEntityId))
            {
                yield return this.CreateCommandName($"{PfxActOn}{this._currentEntityId}");
                yield return this.CreateCommandName($"{PfxActOff}{this._currentEntityId}");
                yield break;
            }

            if (this._level == ViewLevel.Area && !String.IsNullOrEmpty(this._currentAreaId))
            {
                // Switches for current area
                foreach (var kv in this._switchesByEntity)
                {
                    if (this._entityToAreaId.TryGetValue(kv.Key, out var aid) && String.Equals(aid, this._currentAreaId, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return this.CreateCommandName($"{PfxDevice}{kv.Key}");
                    }
                }
                yield break;
            }

            // ROOT: list areas that actually have switches
            var areaIds = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
            foreach (var eid in this._switchesByEntity.Keys)
            {
                if (this._entityToAreaId.TryGetValue(eid, out var aid))
                {
                    areaIds.Add(aid);
                }
            }

            // Order by area name
            var ordered = areaIds
                .Select(aid => (aid, name: this._areaIdToName.TryGetValue(aid, out var n) ? n : aid))
                .OrderBy(t => t.name, StringComparer.CurrentCultureIgnoreCase);

            foreach (var (aid, _) in ordered)
            {
                yield return this.CreateCommandName($"{CmdArea}{aid}");
            }

            yield return this.CreateCommandName(CmdRetry);
        }

        public override String GetCommandDisplayName(String actionParameter, PluginImageSize _)
        {
            if (actionParameter == CmdBack)
            {
                return "Back";
            }

            if (actionParameter == CmdStatus)
            {
                return String.Empty; // no caption under status
            }

            if (actionParameter == CmdRetry)
            {
                return "Retry";
            }

            if (actionParameter.StartsWith(PfxDevice, StringComparison.OrdinalIgnoreCase))
            {
                var entityId = actionParameter.Substring(PfxDevice.Length);
                return entityId;
            }
            if (actionParameter.StartsWith(PfxActOn, StringComparison.OrdinalIgnoreCase))
            {
                return "On";
            }
            if (actionParameter.StartsWith(CmdArea, StringComparison.OrdinalIgnoreCase))
            {
                var areaId = actionParameter.Substring(CmdArea.Length);
                return this._areaIdToName.TryGetValue(areaId, out var name) ? name : areaId;
            }

            return actionParameter.StartsWith(PfxActOff, StringComparison.OrdinalIgnoreCase) ? "Off" : actionParameter;
        }

        // Paint the tile: green when OK, red on error
        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            if (actionParameter == CmdBack)
            {
                return this._icons.Get(IconId.Back);
            }
            if (actionParameter == CmdRetry)
            {
                return this._icons.Get(IconId.Retry);
            }

            // STATUS
            if (actionParameter == CmdStatus)
            {
                var ok = HealthBus.State == HealthState.Ok;
                using (var bb = new BitmapBuilder(imageSize))
                {
                    var okImg = this._icons.Get(IconId.Online);
                    var issueImg = this._icons.Get(IconId.Issue);
                    TilePainter.Background(bb, ok ? okImg : issueImg, ok ? new BitmapColor(StatusOkRed, StatusOkGreen, StatusOkBlue) : new BitmapColor(StatusErrorRed, StatusErrorGreen, StatusErrorBlue));
                    bb.DrawText(ok ? "ONLINE" : "ISSUE", fontSize: FontSizeSmall, color: new BitmapColor(255, 255, 255));
                    return bb.ToImage();
                }
            }

            // DEVICE tiles (switch icons)
            if (actionParameter.StartsWith(PfxDevice, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.Switch);
            }

            if (actionParameter.StartsWith(CmdArea, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.Area);
            }

            // ACTION tiles
            if (actionParameter.StartsWith(PfxActOn, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.SwitchOn);
            }
            if (actionParameter.StartsWith(PfxActOff, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.SwitchOff);
            }

            // Fallback for any unhandled cases - return a default icon
            return this._icons.Get(IconId.Switch);
        }

        public override void RunCommand(String actionParameter)
        {
            PluginLog.Debug(() => $"RunCommand: {actionParameter}");

            try
            {
                // Use switch expression to dispatch to focused command handlers
                var handled = actionParameter switch
                {
                    CmdBack => this.HandleBackCommand(),
                    CmdRetry => this.HandleRetryCommand(),
                    CmdStatus => this.HandleStatusCommand(),
                    var cmd when cmd.StartsWith(CmdArea, StringComparison.OrdinalIgnoreCase) => this.HandleAreaCommand(cmd),
                    var cmd when cmd.StartsWith(PfxDevice, StringComparison.OrdinalIgnoreCase) => this.HandleDeviceCommand(cmd),
                    var cmd when cmd.StartsWith(PfxActOn, StringComparison.OrdinalIgnoreCase) => this.HandleSwitchOnCommand(cmd),
                    var cmd when cmd.StartsWith(PfxActOff, StringComparison.OrdinalIgnoreCase) => this.HandleSwitchOffCommand(cmd),
                    _ => false
                };

                if (!handled)
                {
                    PluginLog.Warning($"Unhandled command: {actionParameter}");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"[RunCommand] Exception for {actionParameter}");
                HealthBus.Error("Command error");
            }
        }

        private Boolean HandleBackCommand()
        {
            if (this._level == ViewLevel.Device)
            {
                this._currentEntityId = null;
                this._level = ViewLevel.Area;
                this.ButtonActionNamesChanged();
            }
            else if (this._level == ViewLevel.Area)
            {
                this._currentAreaId = null;
                this._level = ViewLevel.Root;
                this.ButtonActionNamesChanged();
            }
            else // Root
            {
                this.Close();
            }
            return true;
        }

        private Boolean HandleRetryCommand()
        {
            this.AuthenticateSync();
            return true;
        }

        private Boolean HandleStatusCommand()
        {
            var ok = HealthBus.State == HealthState.Ok;
            this.Plugin.OnPluginStatusChanged(ok ? PluginStatus.Normal : PluginStatus.Error,
                ok ? "Home Assistant is connected." : HealthBus.LastMessage);
            return true;
        }

        private Boolean HandleAreaCommand(String actionParameter)
        {
            var areaId = actionParameter.Substring(CmdArea.Length);
            if (this._areaIdToName.ContainsKey(areaId) || String.Equals(areaId, UnassignedAreaId, StringComparison.OrdinalIgnoreCase))
            {
                this._currentAreaId = areaId;
                this._level = ViewLevel.Area;
                this._currentEntityId = null;
                this.ButtonActionNamesChanged();
                PluginLog.Debug(() => $"ENTER area view: {areaId}");
                return true;
            }
            return false;
        }

        private Boolean HandleDeviceCommand(String actionParameter)
        {
            PluginLog.Info($"Entering Device view");
            var entityId = actionParameter.Substring(PfxDevice.Length);
            if (!this._switchesByEntity.ContainsKey(entityId))
            {
                return false;
            }

            this._level = ViewLevel.Device;
            this._currentEntityId = entityId;

            this.ButtonActionNamesChanged();       // swap to device actions

            PluginLog.Debug(() => $"ENTER device view: {entityId}  level={this._level}");
            return true;
        }

        private Boolean HandleSwitchOnCommand(String actionParameter)
        {
            var entityId = actionParameter.Substring(PfxActOn.Length);

            // Optimistic: mark ON immediately using SwitchStateManager (UI becomes responsive)
            this._switchStateManager?.UpdateSwitchState(entityId, true);
            PluginLog.Debug(() => $"[TurnOn] Updated SwitchStateManager for {entityId}: ON=true");

            this.CommandImageChanged(this.CreateCommandName($"{PfxDevice}{entityId}"));

            this.MarkCommandSent(entityId);
            _ = this._switchSvc?.TurnOnAsync(entityId);
            return true;
        }

        private Boolean HandleSwitchOffCommand(String actionParameter)
        {
            var entityId = actionParameter.Substring(PfxActOff.Length);

            // Optimistic: mark OFF using SwitchStateManager
            this._switchStateManager?.UpdateSwitchState(entityId, false);
            PluginLog.Debug(() => $"[TurnOff] Updated SwitchStateManager for {entityId}: ON=false");

            this.CommandImageChanged(this.CreateCommandName($"{PfxDevice}{entityId}"));

            this._switchSvc?.TurnOffAsync(entityId);
            this.MarkCommandSent(entityId);
            return true;
        }

        // 🔧 return bools here:
        public override Boolean Load()
        {
            PluginLog.Info("[SwitchesDynamicFolder] Load() START");
            PluginLog.Debug(() => $"[SwitchesDynamicFolder] Folder.Name = {this.Name}, CommandName = {this.CommandName}");

            try
            {
                // Initialize dependencies now that Plugin is available
                PluginLog.Debug(() => $"[SwitchesDynamicFolder] Load() - this.Plugin is null: {this.Plugin == null}");

                if (this.Plugin is HomeAssistantPlugin haPlugin)
                {
                    PluginLog.Debug(() => $"[SwitchesDynamicFolder] Load() - this.Plugin type: {this.Plugin.GetType().Name}");

                    // Initialize dependency injection - use the shared HaClient from Plugin
                    PluginLog.Info("[SwitchesDynamicFolder] Load() - Initializing dependencies");

                    this._ha = new HaClientAdapter(haPlugin.HaClient);
                    this._dataService = new HomeAssistantDataService(this._ha);
                    this._dataParser = new HomeAssistantDataParser(new CapabilityService());

                    // Use the singleton SwitchStateManager from the main plugin to preserve state across folder exits/entries
                    this._switchStateManager = haPlugin.SwitchStateManager;
                    var existingCount = this._switchStateManager.GetTrackedEntityIds().Count();
                    PluginLog.Info(() => $"[SwitchesDynamicFolder] Using singleton SwitchStateManager with {existingCount} existing tracked entities");

                    this._registryService = new RegistryService();

                    // Initialize switch control service
                    this._switchSvc = new SwitchControlService(this._ha);

                    PluginLog.Info("[SwitchesDynamicFolder] Load() - All dependencies initialized successfully");
                }
                else
                {
                    PluginLog.Error(() => $"[SwitchesDynamicFolder] Load() - Plugin is not HomeAssistantPlugin, actual type: {this.Plugin?.GetType()?.Name ?? "null"}");
                    return false;
                }

                HealthBus.HealthChanged += this.OnHealthChanged;

                PluginLog.Info("[SwitchesDynamicFolder] Load() completed successfully");
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "[SwitchesDynamicFolder] Load() failed with exception");
                return false;
            }
        }

        public override Boolean Unload()
        {
            PluginLog.Info("SwitchesDynamicFolder.Unload()");

            if (this._switchStateManager != null)
            {
                var trackedCount = this._switchStateManager.GetTrackedEntityIds().Count();
                PluginLog.Info(() => $"[SwitchesDynamicFolder] Unloading - SwitchStateManager retains {trackedCount} tracked entities (singleton preserved)");
            }

            this._switchSvc?.Dispose();
            this._eventsCts?.Cancel();
            this._events.SafeCloseAsync();

            HealthBus.HealthChanged -= this.OnHealthChanged;
            return true;
        }

        public override Boolean Activate()
        {
            PluginLog.Info("SwitchesDynamicFolder.Activate() -> authenticate");
            var ret = this.AuthenticateSync();
            return ret;
        }

        public override Boolean Deactivate()
        {
            PluginLog.Info("SwitchesDynamicFolder.Deactivate() -> close WS");

            if (this._switchStateManager != null)
            {
                var trackedCount = this._switchStateManager.GetTrackedEntityIds().Count();
                PluginLog.Info(() => $"[SwitchesDynamicFolder] Deactivating - SwitchStateManager retains {trackedCount} tracked entities (singleton preserved)");
            }

            this._cts?.Cancel();
            this._ha?.SafeCloseAsync().GetAwaiter().GetResult();
            this._eventsCts?.Cancel();
            _ = this._events.SafeCloseAsync();
            return true;
        }

        private void OnHealthChanged(Object? sender, EventArgs e)
        {
            try
            {
                this.ButtonActionNamesChanged();
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Failed to refresh status tile");
            }
        }

        // now returns bool so Activate() can bubble success up
        private Boolean AuthenticateSync()
        {
            this._cts?.Cancel();
            this._cts = new CancellationTokenSource();

            if (!this.Plugin.TryGetPluginSetting(HomeAssistantPlugin.SettingBaseUrl, out var baseUrl) ||
                String.IsNullOrWhiteSpace(baseUrl))
            {
                PluginLog.Warning("Missing ha.baseUrl setting");
                HealthBus.Error("Missing Base URL");
                this.Plugin.OnPluginStatusChanged(PluginStatus.Error, "Set Home Assistant Base URL in plugin settings.");
                return false;
            }

            if (!this.Plugin.TryGetPluginSetting(HomeAssistantPlugin.SettingToken, out var token) ||
                String.IsNullOrWhiteSpace(token))
            {
                PluginLog.Warning("Missing ha.token setting");
                HealthBus.Error("Missing Token");
                this.Plugin.OnPluginStatusChanged(PluginStatus.Error, "Set Home Assistant Long-Lived Token in plugin settings.");
                return false;
            }

            try
            {
                var (ok, msg) = this._ha != null
                    ? this._ha.ConnectAndAuthenticateAsync(baseUrl, token, TimeSpan.FromSeconds(AuthTimeoutSeconds), this._cts.Token)
                        .GetAwaiter().GetResult()
                    : (false, "HaClient not initialized");

                if (ok)
                {
                    HealthBus.Ok("Auth OK");
                    this.Plugin.OnPluginStatusChanged(PluginStatus.Normal, "Connected to Home Assistant.");

                    try
                    {
                        this._eventsCts?.Cancel();
                        this._eventsCts = new CancellationTokenSource();
                        
                        // Subscribe to switch state changes
                        this._events.SwitchStateChanged -= this.OnHaSwitchStateChanged;
                        this._events.SwitchStateChanged += this.OnHaSwitchStateChanged;
                        
                        PluginLog.Verbose("[WS] connecting event stream…");
                        _ = this._events.ConnectAndSubscribeAsync(baseUrl, token, this._eventsCts.Token); // fire-and-forget
                        PluginLog.Info("[events] subscribed to switch state_changed");
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warning(ex, "[events] subscribe failed");
                    }

                    // Fetch and log the current switches
                    var okFetch = this.FetchSwitchesAndServices();
                    if (!okFetch)
                    {
                        PluginLog.Warning("FetchSwitchesAndServices encountered issues (see logs).");
                    }

                    this._ha?.EnsureConnectedAsync(TimeSpan.FromSeconds(AuthTimeoutSeconds), this._cts.Token).GetAwaiter().GetResult();

                    this.ButtonActionNamesChanged();
                    return true;
                }

                HealthBus.Error(msg ?? "Auth failed");
                this.Plugin.OnPluginStatusChanged(PluginStatus.Error, msg);
                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "AuthenticateSync failed");
                HealthBus.Error("Auth error");
                this.Plugin.OnPluginStatusChanged(PluginStatus.Error, "Auth error. See plugin logs.");
                return false;
            }
        }

        private Boolean FetchSwitchesAndServices()
        {
            try
            {
                if (this._dataService == null || this._dataParser == null || this._registryService == null || this._switchStateManager == null)
                {
                    PluginLog.Error("[SwitchesDynamicFolder] FetchSwitchesAndServices: Required services are not initialized");
                    return false;
                }

                // Use the self-contained InitOrUpdateAsync method for switch state initialization
                var (switchSuccess, switchError) = this._switchStateManager.InitOrUpdateAsync(this._dataService, this._dataParser, this._cts.Token).GetAwaiter().GetResult();
                if (!switchSuccess)
                {
                    PluginLog.Error($"[SwitchesDynamicFolder] Switch state initialization failed: {switchError}");
                    HealthBus.Error("Switch init failed");
                    return false;
                }

                // Fetch services data (unique to DynamicFolder - not handled by InitOrUpdateAsync)
                var (okServices, servicesJson, errServices) = this._dataService.FetchServicesAsync(this._cts.Token).GetAwaiter().GetResult();
                if (!okServices)
                {
                    PluginLog.Error(() => $"Failed to fetch services: {errServices}");
                    return false;
                }

                // Validate servicesJson is not null even if fetch succeeded
                if (String.IsNullOrEmpty(servicesJson))
                {
                    PluginLog.Error("FetchServicesAsync succeeded but returned null or empty JSON data");
                    HealthBus.Error("Invalid services data");
                    return false;
                }

                // We need to re-fetch some data that InitOrUpdateAsync already fetched, but we need it for additional processing
                var (okStates, statesJson, errStates) = this._dataService.FetchStatesAsync(this._cts.Token).GetAwaiter().GetResult();
                var (okEnt, entJson, errEnt) = this._dataService.FetchEntityRegistryAsync(this._cts.Token).GetAwaiter().GetResult();
                var (okDev, devJson, errDev) = this._dataService.FetchDeviceRegistryAsync(this._cts.Token).GetAwaiter().GetResult();
                var (okArea, areaJson, errArea) = this._dataService.FetchAreaRegistryAsync(this._cts.Token).GetAwaiter().GetResult();

                if (!okStates || String.IsNullOrEmpty(statesJson))
                {
                    PluginLog.Error($"[SwitchesDynamicFolder] Failed to re-fetch states for additional processing: {errStates}");
                    // Don't fail here since switch initialization succeeded - just log warning
                    PluginLog.Warning("[SwitchesDynamicFolder] Continuing without additional state processing");
                }
                else
                {
                    // Validate required JSON data using the parser (unique to DynamicFolder)
                    if (!this._dataParser.ValidateJsonData(statesJson, servicesJson))
                    {
                        PluginLog.Warning("[SwitchesDynamicFolder] JSON data validation failed - continuing without validation");
                    }

                    // Parse registry data and update registry service (unique to DynamicFolder)
                    var registryData = this._dataParser.ParseRegistries(devJson, entJson, areaJson);
                    this._registryService.UpdateRegistries(registryData);

                    // Parse switch states for internal cache updates (unique to DynamicFolder)
                    var switches = this._dataParser.ParseSwitchStates(statesJson, registryData);

                    // Update internal caches for compatibility with existing code (unique to DynamicFolder)
                    this.UpdateInternalCachesFromServices(switches, registryData);
                }

                // Process services using the parser (unique to DynamicFolder)
                this._dataParser.ProcessServices(servicesJson);

                HealthBus.Ok("Fetched switches/services");
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "FetchSwitchesAndServices failed");
                HealthBus.Error("Fetch failed");
                return false;
            }
        }

        /// <summary>
        /// Updates internal caches from services - only maintains non-state data for UI purposes
        /// State management is now handled entirely by SwitchStateManager
        /// </summary>
        private void UpdateInternalCachesFromServices(List<SwitchData> switches, ParsedRegistryData registryData)
        {
            // Clear existing UI data (state is managed by SwitchStateManager)
            this._switchesByEntity.Clear();
            this._entityToAreaId.Clear();
            this._areaIdToName.Clear();

            PluginLog.Debug(() => $"[UpdateInternalCachesFromServices] Clearing UI caches, SwitchStateManager handles all state data");

            // Update from registry data
            foreach (var (areaId, areaName) in registryData.AreaIdToName)
            {
                this._areaIdToName[areaId] = areaName;
            }

            // Update from switch data - only UI-related data, not state
            foreach (var switchEntity in switches)
            {
                var si = new SwitchItem(switchEntity.EntityId, switchEntity.FriendlyName, switchEntity.State,
                                       switchEntity.DeviceId ?? "", switchEntity.DeviceName, switchEntity.Manufacturer, switchEntity.Model);
                this._switchesByEntity[switchEntity.EntityId] = si;
                this._entityToAreaId[switchEntity.EntityId] = switchEntity.AreaId;
            }

            PluginLog.Info(() => $"[UpdateInternalCachesFromServices] Updated {switches.Count} switches, {registryData.AreaIdToName.Count} areas - state managed by SwitchStateManager");
        }

        private void OnHaSwitchStateChanged(String entityId, Boolean isOn)
        {
            // Only switches
            if (!entityId.StartsWith("switch.", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var priorIsOn = this._switchStateManager?.IsSwitchOn(entityId) ?? false;
            var wasIgnored = this.ShouldIgnoreFrame(entityId, "switch_state");

            PluginLog.Verbose(() => $"[OnHaSwitchStateChanged] {entityId}: receivedIsOn={isOn}, priorIsOn={priorIsOn}, wasIgnored={wasIgnored}");

            if (wasIgnored)
            {
                return;
            }

            // Update switch state using SwitchStateManager
            this._switchStateManager?.UpdateSwitchState(entityId, isOn);
            PluginLog.Verbose(() => $"[OnHaSwitchStateChanged] {entityId}: Updated to {(isOn ? "ON" : "OFF")} state");

            // Also repaint the device tile icon if visible in the current view
            this.CommandImageChanged(this.CreateCommandName($"{PfxDevice}{entityId}"));
        }

        private void MarkCommandSent(String entityId) => this._lastCmdAt[entityId] = DateTime.UtcNow;

        private Boolean ShouldIgnoreFrame(String entityId, String? reasonForLog = null)
        {
            if (this._lastCmdAt.TryGetValue(entityId, out var t))
            {
                var elapsed = DateTime.UtcNow - t;
                if (elapsed <= EchoSuppressWindow)
                {
                    PluginLog.Verbose(() => $"[echo] SUPPRESSED frame for {entityId} ({reasonForLog}) - elapsed={elapsed.TotalSeconds:F1}s of {EchoSuppressWindow.TotalSeconds}s window");
                    return true;
                }
                // past the window → forget it
                this._lastCmdAt.Remove(entityId);
                PluginLog.Verbose(() => $"[echo] Window expired for {entityId}, removing from suppression list");
            }
            return false;
        }
    }
}