namespace Loupedeck.HomeAssistantPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using Loupedeck;
    using Loupedeck.HomeAssistantPlugin.Models;
    using Loupedeck.HomeAssistantPlugin.Services;

    /// <summary>
    /// Unified action for toggling multiple Home Assistant switches with comprehensive control options.
    /// Supports up to 3 switches via dropdowns plus unlimited additional switches via text input.
    /// Uses modern dependency injection pattern with switch control for optimal performance.
    /// Replaces both ToggleSwitchAction and AdvancedToggleSwitchesAction for simplified configuration.
    /// </summary>
    public sealed class ToggleSwitchesAction : ActionEditorCommand, IDisposable
    {
        /// <summary>
        /// Logging prefix for this action's log messages.
        /// </summary>
        private const String LogPrefix = "[ToggleSwitches]";

        /// <summary>
        /// Home Assistant client interface for WebSocket communication.
        /// </summary>
        private IHaClient? _ha;

        /// <summary>
        /// Switch control service for switch operations.
        /// </summary>
        private ISwitchControlService? _switchSvc;

        /// <summary>
        /// Switch state manager for tracking switch properties and capabilities.
        /// </summary>
        private ISwitchStateManager? _switchStateManager;

        /// <summary>
        /// Data service for fetching Home Assistant entity states.
        /// </summary>
        private IHomeAssistantDataService? _dataService;

        /// <summary>
        /// Data parser for processing Home Assistant JSON responses.
        /// </summary>
        private IHomeAssistantDataParser? _dataParser;

        /// <summary>
        /// Registry service for device, entity, and area management.
        /// </summary>
        private IRegistryService? _registryService;

        /// <summary>
        /// Capability service for analyzing switch feature support.
        /// </summary>
        private readonly CapabilityService _capSvc = new();

        /// <summary>
        /// Indicates whether this instance has been disposed.
        /// </summary>
        private Boolean _disposed = false;

        /// <summary>
        /// Simple toggle state for all selected switches. Defaults to false (off state).
        /// This boolean alternates between true/false on each press, and all switches get the same command.
        /// </summary>
        private Boolean _switchesState = false;

        /// <summary>
        /// Control name for first switch selection dropdown.
        /// </summary>
        private const String ControlSwitch1 = "ha_switch_1";

        /// <summary>
        /// Control name for second switch selection dropdown.
        /// </summary>
        private const String ControlSwitch2 = "ha_switch_2";

        /// <summary>
        /// Control name for third switch selection dropdown.
        /// </summary>
        private const String ControlSwitch3 = "ha_switch_3";

        /// <summary>
        /// Control name for additional switches text input (comma-separated entity IDs).
        /// </summary>
        private const String ControlAdditionalSwitches = "ha_additional_switches";

        /// <summary>
        /// Authentication timeout in seconds for Home Assistant connections.
        /// </summary>
        private const Int32 AuthTimeoutSeconds = 8;

        /// <summary>
        /// Cache TTL in minutes for registry data to prevent refetching on every dropdown open.
        /// </summary>
        private const Int32 CacheTtlMinutes = 5;

        /// <summary>
        /// Icon service for rendering action button graphics.
        /// </summary>
        private readonly IconService _icons;

        /// <summary>
        /// Cache timestamp for registry data to implement basic TTL.
        /// </summary>
        private DateTime _cacheTimestamp = DateTime.MinValue;

        /// <summary>
        /// Cached switches list to prevent registry refetching on every dropdown open.
        /// </summary>
        private List<SwitchData>? _cachedSwitches = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="ToggleSwitchesAction"/> class.
        /// Sets up action editor controls for switch selection with 3 dropdowns and additional text input.
        /// </summary>
        public ToggleSwitchesAction()
        {
            this.Name = "HomeAssistant.ToggleSwitches";
            this.DisplayName = "Toggle Switches";
            this.GroupName = "Switches";
            this.Description = "Toggle multiple Home Assistant switches on/off with up to 3 dropdown selections plus additional switches via text input.";

            // Three switch selection dropdowns
            this.ActionEditor.AddControlEx(new ActionEditorListbox(ControlSwitch1, "Switch 1 (retry if empty)"));
            this.ActionEditor.AddControlEx(new ActionEditorListbox(ControlSwitch2, "Switch 2 (optional)"));
            this.ActionEditor.AddControlEx(new ActionEditorListbox(ControlSwitch3, "Switch 3 (optional)"));

            // Additional switches (comma-separated entity IDs)
            this.ActionEditor.AddControlEx(
                new ActionEditorTextbox(ControlAdditionalSwitches, "Additional Switches (comma-separated)")
                    .SetPlaceholder("switch.living_room,switch.kitchen")
            );

            this.ActionEditor.ListboxItemsRequested += this.OnListboxItemsRequested;

            this._icons = new IconService(new Dictionary<String, String>
            {
                { IconId.Switch, "switch_icon.svg" }
            });

            PluginLog.Info($"{LogPrefix} Constructor completed - dependency initialization deferred to OnLoad()");
        }

        /// <summary>
        /// Gets the command image for the action button.
        /// </summary>
        /// <param name="parameters">Action editor parameters.</param>
        /// <param name="width">Requested image width.</param>
        /// <param name="height">Requested image height.</param>
        /// <returns>Bitmap image showing a switch icon.</returns>
        protected override BitmapImage GetCommandImage(ActionEditorActionParameters parameters, Int32 width, Int32 height) =>
            // Always show switch icon
            this._icons.Get(IconId.Switch);

        /// <summary>
        /// Loads the action and initializes service dependencies using modern dependency injection pattern.
        /// Creates adapters for Home Assistant client, data services, and switch control.
        /// </summary>
        /// <returns><c>true</c> if initialization succeeded; otherwise, <c>false</c>.</returns>
        protected override Boolean OnLoad()
        {
            PluginLog.Info($"{LogPrefix} OnLoad() START");

            try
            {
                if (this.Plugin is HomeAssistantPlugin haPlugin)
                {
                    PluginLog.Info($"{LogPrefix} Initializing dependencies using modern service architecture");

                    // Initialize dependency injection - use the shared HaClient from Plugin
                    this._ha = new HaClientAdapter(haPlugin.HaClient);
                    this._dataService = new HomeAssistantDataService(this._ha);
                    this._dataParser = new HomeAssistantDataParser(this._capSvc);

                    // Use the singleton SwitchStateManager from the main plugin
                    this._switchStateManager = haPlugin.SwitchStateManager;
                    var existingCount = this._switchStateManager.GetTrackedEntityIds().Count();
                    PluginLog.Info($"{LogPrefix} Using singleton SwitchStateManager with {existingCount} existing tracked entities");

                    this._registryService = new RegistryService();

                    // Initialize switch control service
                    this._switchSvc = new SwitchControlService(this._ha);

                    PluginLog.Info($"{LogPrefix} All dependencies initialized successfully");
                    return true;
                }
                else
                {
                    PluginLog.Error($"{LogPrefix} Plugin is not HomeAssistantPlugin, actual type: {this.Plugin?.GetType()?.Name ?? "null"}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"{LogPrefix} OnLoad() failed with exception");
                return false;
            }
        }

        /// <summary>
        /// Unloads the action and disposes of resources.
        /// </summary>
        /// <returns>Always <c>true</c> indicating successful unload.</returns>
        protected override Boolean OnUnload()
        {
            PluginLog.Info($"{LogPrefix} OnUnload()");
            this.Dispose();
            return true;
        }

        /// <summary>
        /// Disposes of managed resources, particularly the switch control service.
        /// Shared services are managed by the main plugin and are not disposed here.
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            PluginLog.Info($"{LogPrefix} Disposing resources");

            try
            {
                this._switchSvc?.Dispose();
                this._switchSvc = null;

                // Don't dispose shared services - they're managed by the main plugin
                this._ha = null;
                this._dataService = null;
                this._dataParser = null;
                this._switchStateManager = null;
                this._registryService = null;

                this._disposed = true;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"{LogPrefix} Error during disposal");
            }
        }

        /// <summary>
        /// Ensures Home Assistant connection is established and authenticated.
        /// Validates configuration settings and attempts connection if not already authenticated.
        /// </summary>
        /// <returns><c>true</c> if connection is ready; otherwise, <c>false</c>.</returns>
        private async Task<Boolean> EnsureHaReadyAsync()
        {
            if (this._ha == null)
            {
                PluginLog.Error($"{LogPrefix} EnsureHaReady: HaClient not initialized");
                return false;
            }

            if (this._ha.IsAuthenticated)
            {
                PluginLog.Info($"{LogPrefix} EnsureHaReady: already authenticated");
                return true;
            }

            if (!this.Plugin.TryGetPluginSetting(HomeAssistantPlugin.SettingBaseUrl, out var baseUrl) ||
                String.IsNullOrWhiteSpace(baseUrl))
            {
                PluginLog.Warning($"{LogPrefix} EnsureHaReady: Missing ha.baseUrl setting");
                HealthBus.Error("Missing Base URL");
                return false;
            }

            if (!this.Plugin.TryGetPluginSetting(HomeAssistantPlugin.SettingToken, out var token) ||
                String.IsNullOrWhiteSpace(token))
            {
                PluginLog.Warning($"{LogPrefix} EnsureHaReady: Missing ha.token setting");
                HealthBus.Error("Missing Token");
                return false;
            }

            try
            {
                PluginLog.Info($"{LogPrefix} Connecting to HA using modern service architecture… url='{baseUrl}'");
                var (ok, msg) = await this._ha.ConnectAndAuthenticateAsync(
                    baseUrl, token, TimeSpan.FromSeconds(AuthTimeoutSeconds), CancellationToken.None
                ).ConfigureAwait(false);

                if (ok)
                {
                    HealthBus.Ok("Auth OK");
                    PluginLog.Info($"{LogPrefix} Auth result ok={ok} msg='{msg}'");
                }
                else
                {
                    HealthBus.Error(msg ?? "Auth failed");
                    PluginLog.Warning($"{LogPrefix} Auth failed: {msg}");
                }

                return ok;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"{LogPrefix} EnsureHaReady exception");
                HealthBus.Error("Auth error");
                return false;
            }
        }

        /// <summary>
        /// Executes the toggle switches command with comprehensive switch control.
        /// Processes multiple switches from dropdowns and additional text input with simple on/off toggle behavior.
        /// All switches get the same command (on or off) based on toggle state.
        /// </summary>
        /// <param name="ps">Action editor parameters containing user-configured values.</param>
        /// <returns><c>true</c> if all switch operations succeeded; otherwise, <c>false</c>.</returns>
        protected override Boolean RunCommand(ActionEditorActionParameters ps)
        {
            try
            {
                PluginLog.Info($"{LogPrefix} RunCommand START");

                // Make sure we're online before doing anything
                if (!this.EnsureHaReadyAsync().GetAwaiter().GetResult())
                {
                    PluginLog.Warning($"{LogPrefix} RunCommand: EnsureHaReady failed");
                    return false;
                }

                // Get selected switches from all sources
                var selectedSwitches = new List<String>();

                // Add switches from dropdowns
                if (ps.TryGetString(ControlSwitch1, out var switch1) && !String.IsNullOrWhiteSpace(switch1))
                {
                    selectedSwitches.Add(switch1.Trim());
                }

                if (ps.TryGetString(ControlSwitch2, out var switch2) && !String.IsNullOrWhiteSpace(switch2))
                {
                    var trimmed = switch2.Trim();
                    if (!selectedSwitches.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    {
                        selectedSwitches.Add(trimmed);
                    }
                }

                if (ps.TryGetString(ControlSwitch3, out var switch3) && !String.IsNullOrWhiteSpace(switch3))
                {
                    var trimmed = switch3.Trim();
                    if (!selectedSwitches.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    {
                        selectedSwitches.Add(trimmed);
                    }
                }

                // Add additional switches from text input
                if (ps.TryGetString(ControlAdditionalSwitches, out var additionalSwitches) && !String.IsNullOrWhiteSpace(additionalSwitches))
                {
                    var additionalList = additionalSwitches.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => !String.IsNullOrEmpty(s) && !selectedSwitches.Contains(s, StringComparer.OrdinalIgnoreCase));

                    selectedSwitches.AddRange(additionalList);
                }

                if (!selectedSwitches.Any())
                {
                    PluginLog.Warning($"{LogPrefix} RunCommand: No switches selected");
                    return false;
                }

                PluginLog.Info($"{LogPrefix} Press: Processing {selectedSwitches.Count} switches with individual capabilities");

                // Process switches with individual capability filtering
                var success = this.ProcessSwitchesIndividually(selectedSwitches);

                PluginLog.Info($"{LogPrefix} RunCommand completed with success={success}");
                return success;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"{LogPrefix} RunCommand exception");
                return false;
            }
            finally
            {
                PluginLog.Info($"{LogPrefix} RunCommand END");
            }
        }

        /// <summary>
        /// Processes multiple switches with individual capability filtering.
        /// For switches, capabilities are simple (just on/off), so this mainly handles the toggle state logic.
        /// </summary>
        /// <param name="entityIds">Collection of switch entity IDs to process.</param>
        /// <returns><c>true</c> if all switches processed successfully; otherwise, <c>false</c>.</returns>
        private Boolean ProcessSwitchesIndividually(IEnumerable<String> entityIds)
        {
            // Toggle the switches state before processing switches - all switches get the same command
            this._switchesState = !this._switchesState;
            PluginLog.Info($"{LogPrefix} Toggled switches state to: {(this._switchesState ? "ON" : "OFF")}");

            var success = true;

            foreach (var entityId in entityIds)
            {
                try
                {
                    // Get INDIVIDUAL capabilities for this specific switch (switches are simpler than lights)
                    var individualCaps = this._switchStateManager?.GetCapabilities(entityId)
                        ?? new SwitchCaps(true); // Switches just support on/off

                    PluginLog.Info($"{LogPrefix} Processing {entityId} with individual capabilities: OnOff={individualCaps.OnOff}");

                    // Process this switch with ITS OWN capabilities
                    success &= this.ProcessSingleSwitch(entityId, individualCaps);
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, $"{LogPrefix} Failed to process switch {entityId}");
                    success = false;
                }
            }

            return success;
        }

        /// <summary>
        /// Processes a single switch with simple on/off logic.
        /// Much simpler than lights since switches only support on/off functionality.
        /// </summary>
        /// <param name="entityId">Switch entity ID.</param>
        /// <param name="caps">Switch capabilities (just on/off for switches).</param>
        /// <returns><c>true</c> if switch processed successfully; otherwise, <c>false</c>.</returns>
        private Boolean ProcessSingleSwitch(String entityId, SwitchCaps caps)
        {
            PluginLog.Info($"{LogPrefix} Processing switch: {entityId}");
            PluginLog.Info($"{LogPrefix} Switch capabilities: onoff={caps.OnOff}");

            if (this._switchSvc == null)
            {
                PluginLog.Error($"{LogPrefix} ProcessSingleSwitch: SwitchControlService not available");
                return false;
            }

            // Always use switches state to determine on/off
            PluginLog.Info($"{LogPrefix} Using switches state to determine command: {(this._switchesState ? "ON" : "OFF")}");

            // Use simple switches toggle state - all switches get the same command
            if (!this._switchesState)
            {
                PluginLog.Info($"{LogPrefix} Switches state is OFF, turning OFF switch {entityId}");
                var success = this._switchSvc.TurnOffAsync(entityId).GetAwaiter().GetResult();
                PluginLog.Info($"{LogPrefix} HA SERVICE CALL: switch.turn_off entity_id={entityId} -> success={success}");

                if (success && this._switchStateManager != null)
                {
                    // Update local state - switch is now off
                    this._switchStateManager.UpdateSwitchState(entityId, false);
                    PluginLog.Info($"{LogPrefix} Updated local state: {entityId} turned OFF");
                }

                if (!success)
                {
                    var friendlyName = entityId; // Could be enhanced to get friendly name
                    this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                        $"Failed to turn off switch {friendlyName}");
                }

                return success;
            }
            else
            {
                // Switches state is ON, turn switch ON
                PluginLog.Info($"{LogPrefix} Switches state is ON, turning ON switch {entityId}");
                var success = this._switchSvc.TurnOnAsync(entityId).GetAwaiter().GetResult();
                PluginLog.Info($"{LogPrefix} HA SERVICE CALL: switch.turn_on entity_id={entityId} -> success={success}");

                if (success && this._switchStateManager != null)
                {
                    // Update local state - switch is now on
                    this._switchStateManager.UpdateSwitchState(entityId, true);
                    PluginLog.Info($"{LogPrefix} Updated local state: {entityId} turned ON");
                }

                if (!success)
                {
                    var friendlyName = entityId; // Could be enhanced to get friendly name
                    this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                        $"Failed to turn on switch {friendlyName}");
                }

                return success;
            }
        }

        /// <summary>
        /// Handles listbox items requested event to populate the switch selection dropdowns.
        /// Uses modern service architecture with caching for performance.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Listbox items requested event arguments.</param>
        private void OnListboxItemsRequested(Object? sender, ActionEditorListboxItemsRequestedEventArgs e)
        {
            // Handle all switch dropdown controls
            if (!e.ControlName.EqualsNoCase(ControlSwitch1) &&
                !e.ControlName.EqualsNoCase(ControlSwitch2) &&
                !e.ControlName.EqualsNoCase(ControlSwitch3))
            {
                return;
            }

            PluginLog.Info($"{LogPrefix} ListboxItemsRequested({e.ControlName}) using modern service architecture");
            try
            {
                // Ensure we're connected before asking HA for states
                if (!this.EnsureHaReadyAsync().GetAwaiter().GetResult())
                {
                    PluginLog.Warning($"{LogPrefix} List: EnsureHaReady failed (not connected/authenticated)");
                    if (!this.Plugin.TryGetPluginSetting(HomeAssistantPlugin.SettingBaseUrl, out var _) ||
                        !this.Plugin.TryGetPluginSetting(HomeAssistantPlugin.SettingToken, out var _))
                    {
                        e.AddItem("!not_configured", "Home Assistant not configured", "Open plugin settings");
                        this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                            "Home Assistant URL and Token not configured");
                    }
                    else
                    {
                        e.AddItem("!not_connected", "Could not connect to Home Assistant", "Check URL/token");
                        this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                            "Could not connect to Home Assistant");
                    }
                    return;
                }

                if (this._dataService == null)
                {
                    PluginLog.Error($"{LogPrefix} ListboxItemsRequested: DataService not available");
                    e.AddItem("!no_service", "Data service not available", "Plugin initialization error");
                    this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                        "Plugin initialization error");
                    return;
                }

                // Check cache first to avoid refetching registry data on every dropdown open
                var now = DateTime.Now;
                var cacheExpired = (now - this._cacheTimestamp).TotalMinutes > CacheTtlMinutes;

                List<SwitchData> switches;
                if (this._cachedSwitches != null && !cacheExpired)
                {
                    PluginLog.Info($"{LogPrefix} Using cached switches data ({this._cachedSwitches.Count} switches, age: {(now - this._cacheTimestamp).TotalMinutes:F1}min)");
                    switches = this._cachedSwitches;
                }
                else
                {
                    PluginLog.Info($"{LogPrefix} Cache expired or empty, fetching fresh registry-aware data");

                    // Fetch states using modern data service
                    var (ok, json, error) = this._dataService.FetchStatesAsync(CancellationToken.None)
                        .GetAwaiter().GetResult();
                    PluginLog.Info($"{LogPrefix} FetchStatesAsync ok={ok} error='{error}' bytes={json?.Length ?? 0}");

                    if (!ok || String.IsNullOrEmpty(json))
                    {
                        e.AddItem("!no_states", $"Failed to fetch states: {error ?? "unknown"}", "Check connection");
                        this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                            $"Failed to fetch entity states error: {error}");
                        return;
                    }

                    // Initialize SwitchStateManager using self-contained method
                    if (this._switchStateManager != null && this._dataService != null && this._dataParser != null)
                    {
                        var (success, errorMessage) = this._switchStateManager.InitOrUpdateAsync(this._dataService, this._dataParser, CancellationToken.None).GetAwaiter().GetResult();
                        if (!success)
                        {
                            PluginLog.Warning($"{LogPrefix} SwitchStateManager.InitOrUpdateAsync failed: {errorMessage}");
                            this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                                $"Failed to load switch data: {errorMessage}");
                            e.AddItem("!init_failed", "Failed to load switches", errorMessage ?? "Check connection to Home Assistant");
                            return;
                        }
                    }

                    // Use registry-aware parsing for switches
                    PluginLog.Info($"{LogPrefix} Fetching registry data for registry-aware switch parsing");

                    if (this._dataService == null)
                    {
                        PluginLog.Error($"{LogPrefix} DataService is null when trying to fetch registry data");
                        e.AddItem("!no_service", "Data service not available", "Plugin initialization error");
                        this.Plugin.OnPluginStatusChanged(PluginStatus.Error, "Data service not available");
                        return;
                    }

                    var (entSuccess, entJson, _) = this._dataService.FetchEntityRegistryAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var (devSuccess, devJson, _) = this._dataService.FetchDeviceRegistryAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var (areaSuccess, areaJson, _) = this._dataService.FetchAreaRegistryAsync(CancellationToken.None).GetAwaiter().GetResult();

                    if (this._dataParser == null)
                    {
                        PluginLog.Error($"{LogPrefix} DataParser is null when trying to parse registry data");
                        e.AddItem("!no_parser", "Data parser not available", "Plugin initialization error");
                        this.Plugin.OnPluginStatusChanged(PluginStatus.Error, "Data parser not available");
                        return;
                    }

                    // Parse registry data and switch states together
                    var registryData = this._dataParser.ParseRegistries(devJson, entJson, areaJson);
                    switches = this._dataParser.ParseSwitchStates(json, registryData);

                    // Cache the results
                    this._cachedSwitches = switches;
                    this._cacheTimestamp = now;
                    PluginLog.Info($"{LogPrefix} Cached {switches.Count} switches with registry data (TTL: {CacheTtlMinutes}min)");
                }

                // Add empty option for optional dropdowns
                if (e.ControlName.EqualsNoCase(ControlSwitch2) || e.ControlName.EqualsNoCase(ControlSwitch3))
                {
                    e.AddItem(name: "", displayName: "(None)", description: "No switch selected");
                }

                // Iterate over parsed switches instead of raw JSON elements
                var count = 0;
                foreach (var switchEntity in switches)
                {
                    e.AddItem(name: switchEntity.EntityId, displayName: switchEntity.EntityId, description: "Home Assistant switch");
                    count++;
                }

                PluginLog.Info($"{LogPrefix} List populated with {count} switch(es) using modern service architecture");

                // Clear any previous error status since we successfully loaded switches
                if (count > 0)
                {
                    this.Plugin.OnPluginStatusChanged(PluginStatus.Normal,
                        $"Successfully loaded {count} switches");
                }

                // Keep current selection
                var current = e.ActionEditorState?.GetControlValue(e.ControlName) as String;
                if (!String.IsNullOrEmpty(current))
                {
                    PluginLog.Info($"{LogPrefix} Keeping current selection: '{current}'");
                    e.SetSelectedItemName(current);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"{LogPrefix} List population failed");
                e.AddItem("!error", "Error reading switches", ex.Message);
            }
        }
    }
}