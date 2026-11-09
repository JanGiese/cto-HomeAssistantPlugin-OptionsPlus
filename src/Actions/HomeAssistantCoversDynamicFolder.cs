namespace Loupedeck.HomeAssistantPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    using Loupedeck;
    using Loupedeck.HomeAssistantPlugin.Models;
    using Loupedeck.HomeAssistantPlugin.Services;

    public class HomeAssistantCoversDynamicFolder : PluginDynamicFolder
    {
        private record CoverItem(
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

        private readonly Dictionary<String, CoverItem> _coversByEntity = new();

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
        private const String PfxActOpen = "act:open:"; // act:open:<entity_id>
        private const String PfxActClose = "act:close:"; // act:close:<entity_id>
        private const String PfxActStop = "act:stop:"; // act:stop:<entity_id>
        private const String PfxActOpenTilt = "act:open_tilt:"; // act:open_tilt:<entity_id>
        private const String PfxActCloseTilt = "act:close_tilt:"; // act:close_tilt:<entity_id>
        private const String PfxActStopTilt = "act:stop_tilt:"; // act:stop_tilt:<entity_id>

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

        private ICoverControlService? _coverSvc;
        private IHaClient? _ha; // adapter over HaWebSocketClient

        // --- Echo suppression: ignore HA frames shortly after we sent a command ---
        private readonly Dictionary<String, DateTime> _lastCmdAt =
            new(StringComparer.OrdinalIgnoreCase);

        // Service dependencies
        private IHomeAssistantDataService? _dataService;
        private IHomeAssistantDataParser? _dataParser;
        private ICoverStateManager? _coverStateManager;
        private IRegistryService? _registryService;

        public HomeAssistantCoversDynamicFolder()
        {
            PluginLog.Info("[CoversDynamicFolder] Constructor START");

            this.DisplayName = "All Cover Controls";
            this.GroupName = "Covers";

            this._icons = new IconService(new Dictionary<String, String>
            {
                { IconId.Back,        "back_icon.svg" },
                { IconId.CoverOpen,    "cover_open_icon.svg" },    
                { IconId.CoverClose,   "cover_close_icon.svg" },
                { IconId.CoverStop,    "cover_stop_icon.svg" }   ,
                { IconId.CoverTilt,    "cover_tilt_icon.svg" }   ,
                { IconId.Retry,       "reload_icon.svg" },
                { IconId.Issue,       "issue_status_icon.svg" },
                { IconId.Online,      "online_status_icon.png" },
                { IconId.Area,        "area_icon.svg" },
                { IconId.Cover,      "cover_icon.svg" },       // Placeholder for cover icon
            });

            PluginLog.Info($"[CoversDynamicFolder] Constructor - this.Plugin is null: {this.Plugin == null}");
            PluginLog.Info("[CoversDynamicFolder] Constructor completed - dependency initialization deferred to OnLoad()");
        }

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType _) =>
            PluginDynamicFolderNavigation.None;

        public override String GetButtonDisplayName(PluginImageSize imageSize) => "All Covers";

        public override BitmapImage GetButtonImage(PluginImageSize imageSize)
        {
            try
            {
                // Use switch icon as a placeholder for covers until a dedicated cover icon is available
                return PluginResources.ReadImage("multiple_covers_icon.svg");
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "[GetButtonImage] Failed to load switch_icon.svg");
                // Return a fallback image if the primary icon fails to load
                try
                {
                    return PluginResources.ReadImage("area_icon.svg");
                }
                catch (Exception fallbackEx)
                {
                    PluginLog.Error(fallbackEx, "[GetButtonImage] Failed to load fallback area_icon.svg");
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
                // Get cover data to check capabilities
                var coverData = this._coverStateManager?.GetCoverData(this._currentEntityId);
                if (coverData != null)
                {
                    // Determine which controls are supported based on capabilities
                    var hasBasicControls = coverData.Capabilities.OnOff;
                    var hasPositionControls = coverData.HasPositionControl;
                    var hasTiltControls = coverData.HasTiltControl;

                    // Regular controls = basic open/close OR position control
                    var hasRegularControls = hasBasicControls || hasPositionControls;

                    PluginLog.Debug(() => $"[GetButtonPressActionNames] Cover {this._currentEntityId}: " +
                        $"BasicControls={hasBasicControls}, PositionControls={hasPositionControls}, " +
                        $"TiltControls={hasTiltControls}, ShowingRegular={hasRegularControls}");

                    // Show regular controls only if supported
                    if (hasRegularControls)
                    {
                        yield return this.CreateCommandName($"{PfxActOpen}{this._currentEntityId}");
                        yield return this.CreateCommandName($"{PfxActClose}{this._currentEntityId}");
                        yield return this.CreateCommandName($"{PfxActStop}{this._currentEntityId}");
                    }

                    // Show tilt controls only if supported
                    if (hasTiltControls)
                    {
                        yield return this.CreateCommandName($"{PfxActOpenTilt}{this._currentEntityId}");
                        yield return this.CreateCommandName($"{PfxActCloseTilt}{this._currentEntityId}");
                        yield return this.CreateCommandName($"{PfxActStopTilt}{this._currentEntityId}");
                    }

                    // If no controls are supported, log warning and show basic fallback
                    if (!hasRegularControls && !hasTiltControls)
                    {
                        PluginLog.Warning($"[GetButtonPressActionNames] Cover {this._currentEntityId} has no supported controls " +
                            $"(OnOff={coverData.Capabilities.OnOff}, Position={hasPositionControls}, Tilt={hasTiltControls}), " +
                            $"showing basic fallback");
                        yield return this.CreateCommandName($"{PfxActOpen}{this._currentEntityId}");
                        yield return this.CreateCommandName($"{PfxActClose}{this._currentEntityId}");
                        yield return this.CreateCommandName($"{PfxActStop}{this._currentEntityId}");
                    }
                }
                else
                {
                    // Fallback if cover data not available
                    PluginLog.Warning($"[GetButtonPressActionNames] Cover data not available for {this._currentEntityId}, showing basic controls");
                    yield return this.CreateCommandName($"{PfxActOpen}{this._currentEntityId}");
                    yield return this.CreateCommandName($"{PfxActClose}{this._currentEntityId}");
                    yield return this.CreateCommandName($"{PfxActStop}{this._currentEntityId}");
                }
                yield break;
            }

            if (this._level == ViewLevel.Area && !String.IsNullOrEmpty(this._currentAreaId))
            {
                // Covers for current area
                foreach (var kv in this._coversByEntity)
                {
                    if (this._entityToAreaId.TryGetValue(kv.Key, out var aid) && String.Equals(aid, this._currentAreaId, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return this.CreateCommandName($"{PfxDevice}{kv.Key}");
                    }
                }
                yield break;
            }

            // ROOT: list areas that actually have covers
            var areaIds = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
            foreach (var eid in this._coversByEntity.Keys)
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
            
            if (actionParameter.StartsWith(PfxActOpen, StringComparison.OrdinalIgnoreCase))
            {
                return "Open Cover";
            }
            
            if (actionParameter.StartsWith(PfxActClose, StringComparison.OrdinalIgnoreCase))
            {
                return "Close Cover";
            }
            
            if (actionParameter.StartsWith(PfxActStop, StringComparison.OrdinalIgnoreCase))
            {
                return "Stop Cover";
            }
            
            if (actionParameter.StartsWith(PfxActOpenTilt, StringComparison.OrdinalIgnoreCase))
            {
                return "Open Tilt";
            }
            
            if (actionParameter.StartsWith(PfxActCloseTilt, StringComparison.OrdinalIgnoreCase))
            {
                return "Close Tilt";
            }
            
            if (actionParameter.StartsWith(PfxActStopTilt, StringComparison.OrdinalIgnoreCase))
            {
                return "Stop Tilt";
            }
            
            if (actionParameter.StartsWith(CmdArea, StringComparison.OrdinalIgnoreCase))
            {
                var areaId = actionParameter.Substring(CmdArea.Length);
                return this._areaIdToName.TryGetValue(areaId, out var name) ? name : areaId;
            }

            return actionParameter;
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

            // DEVICE tiles (cover icons)
            if (actionParameter.StartsWith(PfxDevice, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.Cover); // Using switch icon as placeholder for covers
            }

            if (actionParameter.StartsWith(CmdArea, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.Area);
            }

            // ACTION tiles
            if (actionParameter.StartsWith(PfxActOpen, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.CoverOpen); // Using switch on icon as placeholder for open
            }
            
            if (actionParameter.StartsWith(PfxActClose, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.CoverClose); // Using switch off icon as placeholder for close
            }
            
            if (actionParameter.StartsWith(PfxActStop, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.CoverStop); // Using switch icon as placeholder for stop
            }
            
            // TILT ACTION tiles
            if (actionParameter.StartsWith(PfxActOpenTilt, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.CoverTilt); // Using switch on icon as placeholder for tilt open
            }
            
            if (actionParameter.StartsWith(PfxActCloseTilt, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.CoverTilt); // Using switch off icon as placeholder for tilt close
            }
            
            if (actionParameter.StartsWith(PfxActStopTilt, StringComparison.OrdinalIgnoreCase))
            {
                return this._icons.Get(IconId.CoverStop); // Using switch icon as placeholder for tilt stop
            }

            // Fallback for any unhandled cases - return a default icon
            return this._icons.Get(IconId.Cover);
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
                    var cmd when cmd.StartsWith(PfxActOpen, StringComparison.OrdinalIgnoreCase) => this.HandleCoverOpenCommand(cmd),
                    var cmd when cmd.StartsWith(PfxActClose, StringComparison.OrdinalIgnoreCase) => this.HandleCoverCloseCommand(cmd),
                    var cmd when cmd.StartsWith(PfxActStop, StringComparison.OrdinalIgnoreCase) => this.HandleCoverStopCommand(cmd),
                    var cmd when cmd.StartsWith(PfxActOpenTilt, StringComparison.OrdinalIgnoreCase) => this.HandleCoverOpenTiltCommand(cmd),
                    var cmd when cmd.StartsWith(PfxActCloseTilt, StringComparison.OrdinalIgnoreCase) => this.HandleCoverCloseTiltCommand(cmd),
                    var cmd when cmd.StartsWith(PfxActStopTilt, StringComparison.OrdinalIgnoreCase) => this.HandleCoverStopTiltCommand(cmd),
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
            if (!this._coversByEntity.ContainsKey(entityId))
            {
                return false;
            }

            this._level = ViewLevel.Device;
            this._currentEntityId = entityId;

            this.ButtonActionNamesChanged();       // swap to device actions

            PluginLog.Debug(() => $"ENTER device view: {entityId}  level={this._level}");
            return true;
        }

        private Boolean HandleCoverOpenCommand(String actionParameter)
        {
            var entityId = actionParameter.Substring(PfxActOpen.Length);

            // Optimistic: mark OPEN immediately using CoverStateManager (UI becomes responsive)
            this._coverStateManager?.UpdateCoverState(entityId, "open");
            PluginLog.Debug(() => $"[OpenCover] Updated CoverStateManager for {entityId}: state=open");

            this.CommandImageChanged(this.CreateCommandName($"{PfxDevice}{entityId}"));

            this.MarkCommandSent(entityId);
            _ = this._coverSvc?.OpenCoverAsync(entityId);
            return true;
        }

        private Boolean HandleCoverCloseCommand(String actionParameter)
        {
            var entityId = actionParameter.Substring(PfxActClose.Length);

            // Optimistic: mark CLOSED using CoverStateManager
            this._coverStateManager?.UpdateCoverState(entityId, "closed");
            PluginLog.Debug(() => $"[CloseCover] Updated CoverStateManager for {entityId}: state=closed");

            this.CommandImageChanged(this.CreateCommandName($"{PfxDevice}{entityId}"));

            this._coverSvc?.CloseCoverAsync(entityId);
            this.MarkCommandSent(entityId);
            return true;
        }

        private Boolean HandleCoverStopCommand(String actionParameter)
        {
            var entityId = actionParameter.Substring(PfxActStop.Length);

            // Mark as stopped using CoverStateManager
            this._coverStateManager?.UpdateCoverState(entityId, "stopped");
            PluginLog.Debug(() => $"[StopCover] Updated CoverStateManager for {entityId}: state=stopped");

            this.CommandImageChanged(this.CreateCommandName($"{PfxDevice}{entityId}"));

            this._coverSvc?.StopCoverAsync(entityId);
            this.MarkCommandSent(entityId);
            return true;
        }

        private Boolean HandleCoverOpenTiltCommand(String actionParameter)
        {
            var entityId = actionParameter.Substring(PfxActOpenTilt.Length);

            PluginLog.Info($"[OpenTilt] Opening tilt for cover {entityId}");

            // Update tilt state optimistically - set to 100 (fully open)
            this._coverStateManager?.UpdateCoverTiltPosition(entityId, 100);
            PluginLog.Debug(() => $"[OpenTilt] Updated CoverStateManager for {entityId}: tiltPosition=100");

            this.CommandImageChanged(this.CreateCommandName($"{PfxDevice}{entityId}"));

            // Use SetCoverTiltPositionAsync with position 100 (fully open)
            _ = this._coverSvc?.SetCoverTiltPositionAsync(entityId, 100);
            this.MarkCommandSent(entityId);
            return true;
        }

        private Boolean HandleCoverCloseTiltCommand(String actionParameter)
        {
            var entityId = actionParameter.Substring(PfxActCloseTilt.Length);

            PluginLog.Info($"[CloseTilt] Closing tilt for cover {entityId}");

            // Update tilt state optimistically - set to 0 (fully closed)
            this._coverStateManager?.UpdateCoverTiltPosition(entityId, 0);
            PluginLog.Debug(() => $"[CloseTilt] Updated CoverStateManager for {entityId}: tiltPosition=0");

            this.CommandImageChanged(this.CreateCommandName($"{PfxDevice}{entityId}"));

            // Use SetCoverTiltPositionAsync with position 0 (fully closed)
            _ = this._coverSvc?.SetCoverTiltPositionAsync(entityId, 0);
            this.MarkCommandSent(entityId);
            return true;
        }

        private Boolean HandleCoverStopTiltCommand(String actionParameter)
        {
            var entityId = actionParameter.Substring(PfxActStopTilt.Length);

            PluginLog.Info($"[StopTilt] Stopping tilt for cover {entityId}");

            // For tilt stop, we don't change the position - just stop movement
            // The actual position will be updated when Home Assistant sends the new state
            this.CommandImageChanged(this.CreateCommandName($"{PfxDevice}{entityId}"));

            // Use the dedicated stop_cover_tilt service for stopping tilt movement
            _ = this._coverSvc?.StopCoverTiltAsync(entityId);
            this.MarkCommandSent(entityId);
            return true;
        }

        // 🔧 return bools here:
        public override Boolean Load()
        {
            PluginLog.Info("[CoversDynamicFolder] Load() START");
            PluginLog.Debug(() => $"[CoversDynamicFolder] Folder.Name = {this.Name}, CommandName = {this.CommandName}");

            try
            {
                // Initialize dependencies now that Plugin is available
                PluginLog.Debug(() => $"[CoversDynamicFolder] Load() - this.Plugin is null: {this.Plugin == null}");

                if (this.Plugin is HomeAssistantPlugin haPlugin)
                {
                    PluginLog.Debug(() => $"[CoversDynamicFolder] Load() - this.Plugin type: {this.Plugin.GetType().Name}");

                    // Initialize dependency injection - use the shared HaClient from Plugin
                    PluginLog.Info("[CoversDynamicFolder] Load() - Initializing dependencies");

                    this._ha = new HaClientAdapter(haPlugin.HaClient);
                    this._dataService = new HomeAssistantDataService(this._ha);
                    this._dataParser = new HomeAssistantDataParser(new CapabilityService());

                    // Use the singleton CoverStateManager from the main plugin to preserve state across folder exits/entries
                    this._coverStateManager = haPlugin.CoverStateManager;
                    var existingCount = this._coverStateManager.GetTrackedEntityIds().Count();
                    PluginLog.Info(() => $"[CoversDynamicFolder] Using singleton CoverStateManager with {existingCount} existing tracked entities");

                    this._registryService = new RegistryService();

                    // Initialize cover control service
                    this._coverSvc = new CoverControlService(this._ha);

                    PluginLog.Info("[CoversDynamicFolder] Load() - All dependencies initialized successfully");
                }
                else
                {
                    PluginLog.Error(() => $"[CoversDynamicFolder] Load() - Plugin is not HomeAssistantPlugin, actual type: {this.Plugin?.GetType()?.Name ?? "null"}");
                    return false;
                }

                HealthBus.HealthChanged += this.OnHealthChanged;

                PluginLog.Info("[CoversDynamicFolder] Load() completed successfully");
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "[CoversDynamicFolder] Load() failed with exception");
                return false;
            }
        }

        public override Boolean Unload()
        {
            PluginLog.Info("CoversDynamicFolder.Unload()");

            if (this._coverStateManager != null)
            {
                var trackedCount = this._coverStateManager.GetTrackedEntityIds().Count();
                PluginLog.Info(() => $"[CoversDynamicFolder] Unloading - CoverStateManager has {trackedCount} tracked entities");
            }

            this._coverSvc?.Dispose();
            this._eventsCts?.Cancel();
            this._events.SafeCloseAsync();

            HealthBus.HealthChanged -= this.OnHealthChanged;
            return true;
        }

        public override Boolean Activate()
        {
            PluginLog.Info("CoversDynamicFolder.Activate() -> authenticate");
            var ret = this.AuthenticateSync();
            return ret;
        }

        public override Boolean Deactivate()
        {
            PluginLog.Info("CoversDynamicFolder.Deactivate() -> close WS");

            if (this._coverStateManager != null)
            {
                var trackedCount = this._coverStateManager.GetTrackedEntityIds().Count();
                PluginLog.Info(() => $"[CoversDynamicFolder] Deactivating - CoverStateManager has {trackedCount} tracked entities");
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
                        
                        // TODO: Subscribe to cover state changes when HaEventListener supports cover events
                        // this._events.CoverStateChanged -= this.OnHaCoverStateChanged;
                        // this._events.CoverStateChanged += this.OnHaCoverStateChanged;
                        
                        PluginLog.Verbose("[WS] connecting event stream…");
                        _ = this._events.ConnectAndSubscribeAsync(baseUrl, token, this._eventsCts.Token); // fire-and-forget
                        PluginLog.Info("[events] Event listener connected (cover events not yet supported)");
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warning(ex, "[events] subscribe failed");
                    }

                    // Fetch and log the current covers
                    var okFetch = this.FetchCoversAndServices();
                    if (!okFetch)
                    {
                        PluginLog.Warning("FetchCoversAndServices encountered issues (see logs).");
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

        private Boolean FetchCoversAndServices()
        {
            try
            {
                if (this._dataService == null || this._dataParser == null || this._registryService == null || this._coverStateManager == null)
                {
                    PluginLog.Error("[CoversDynamicFolder] FetchCoversAndServices: Required services are not initialized");
                    return false;
                }

                // Use the self-contained InitOrUpdateAsync method for cover state initialization
                var (coverSuccess, coverError) = this._coverStateManager.InitOrUpdateAsync(this._dataService, this._dataParser, this._cts.Token).GetAwaiter().GetResult();
                if (!coverSuccess)
                {
                    PluginLog.Error($"[CoversDynamicFolder] Cover state initialization failed: {coverError}");
                    HealthBus.Error("Cover init failed");
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
                    PluginLog.Error($"[CoversDynamicFolder] Failed to re-fetch states for additional processing: {errStates}");
                    // Don't fail here since cover initialization succeeded - just log warning
                    PluginLog.Warning("[CoversDynamicFolder] Continuing without additional state processing");
                }
                else
                {
                    // Validate required JSON data using the parser (unique to DynamicFolder)
                    if (!this._dataParser.ValidateJsonData(statesJson, servicesJson))
                    {
                        PluginLog.Warning("[CoversDynamicFolder] JSON data validation failed - continuing without validation");
                    }

                    // Parse registry data and update registry service (unique to DynamicFolder)
                    var registryData = this._dataParser.ParseRegistries(devJson, entJson, areaJson);
                    this._registryService.UpdateRegistries(registryData);

                    // Get cover data from CoverStateManager after initialization
                    var covers = this._coverStateManager.GetAllCovers().ToList();

                    // Update internal caches for compatibility with existing code (unique to DynamicFolder)
                    this.UpdateInternalCachesFromServices(covers, registryData);
                }

                // Process services using the parser (unique to DynamicFolder)
                this._dataParser.ProcessServices(servicesJson);

                HealthBus.Ok("Fetched covers/services");
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "FetchCoversAndServices failed");
                HealthBus.Error("Fetch failed");
                return false;
            }
        }

        /// <summary>
        /// Updates internal caches from services - only maintains non-state data for UI purposes
        /// State management is now handled entirely by CoverStateManager
        /// </summary>
        private void UpdateInternalCachesFromServices(List<CoverData> covers, ParsedRegistryData registryData)
        {
            // Clear existing UI data (state is managed by CoverStateManager)
            this._coversByEntity.Clear();
            this._entityToAreaId.Clear();
            this._areaIdToName.Clear();

            PluginLog.Debug(() => $"[UpdateInternalCachesFromServices] Clearing UI caches, CoverStateManager handles all state data");

            // Update from registry data
            foreach (var (areaId, areaName) in registryData.AreaIdToName)
            {
                this._areaIdToName[areaId] = areaName;
            }

            // Update from cover data - only UI-related data, not state
            foreach (var coverEntity in covers)
            {
                var ci = new CoverItem(coverEntity.EntityId, coverEntity.FriendlyName, coverEntity.State,
                                       coverEntity.DeviceId ?? "", coverEntity.DeviceName, coverEntity.Manufacturer, coverEntity.Model);
                this._coversByEntity[coverEntity.EntityId] = ci;
                this._entityToAreaId[coverEntity.EntityId] = coverEntity.AreaId;
            }

            PluginLog.Info(() => $"[UpdateInternalCachesFromServices] Updated {covers.Count} covers, {registryData.AreaIdToName.Count} areas - state managed by CoverStateManager");
        }

        // TODO: Implement when HaEventListener supports cover state change events
        /*
        private void OnHaCoverStateChanged(String entityId, String state)
        {
            // Only covers
            if (!entityId.StartsWith("cover.", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var priorState = this._coverStateManager?.GetCoverState(entityId) ?? "unknown";
            var wasIgnored = this.ShouldIgnoreFrame(entityId, "cover_state");

            PluginLog.Verbose(() => $"[OnHaCoverStateChanged] {entityId}: receivedState={state}, priorState={priorState}, wasIgnored={wasIgnored}");

            if (wasIgnored)
            {
                return;
            }

            // Update cover state using CoverStateManager
            this._coverStateManager?.UpdateCoverState(entityId, state);
            PluginLog.Verbose(() => $"[OnHaCoverStateChanged] {entityId}: Updated to {state} state");

            // Also repaint the device tile icon if visible in the current view
            this.CommandImageChanged(this.CreateCommandName($"{PfxDevice}{entityId}"));
        }
        */

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