namespace Loupedeck.HomeAssistantPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using Loupedeck;
    using Loupedeck.HomeAssistantPlugin.Models;
    using Loupedeck.HomeAssistantPlugin.Services;

    /// <summary>
    /// Advanced action for controlling multiple Home Assistant covers with position and tilt settings.
    /// Supports setting specific position (0-100%) and tilt position (0-100%) values across multiple covers.
    /// Uses modern dependency injection pattern with cover control service for optimal performance.
    /// </summary>
    public sealed class AdvancedToggleCoversAction : ActionEditorCommand, IDisposable
    {
        /// <summary>
        /// Logging prefix for this action's log messages.
        /// </summary>
        private const String LogPrefix = "[AdvancedToggleCovers]";

        /// <summary>
        /// Home Assistant client interface for WebSocket communication.
        /// </summary>
        private IHaClient? _ha;

        /// <summary>
        /// Cover control service for position and tilt control.
        /// </summary>
        private ICoverControlService? _coverSvc;

        /// <summary>
        /// Cover state manager for tracking cover properties and capabilities.
        /// </summary>
        private ICoverStateManager? _coverStateManager;

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
        /// Capability service for analyzing cover feature support.
        /// </summary>
        private readonly CapabilityService _capSvc = new();

        /// <summary>
        /// Indicates whether this instance has been disposed.
        /// </summary>
        private Boolean _disposed = false;


        /// <summary>
        /// Control name for primary cover selection dropdown.
        /// </summary>
        private const String ControlCovers = "ha_covers";

        /// <summary>
        /// Control name for additional covers text input (comma-separated entity IDs).
        /// </summary>
        private const String ControlAdditionalCovers = "ha_additional_covers";

        /// <summary>
        /// Control name for position adjustment (0-100%).
        /// </summary>
        private const String ControlPosition = "ha_position";

        /// <summary>
        /// Control name for tilt position adjustment (0-100%).
        /// </summary>
        private const String ControlTiltPosition = "ha_tilt_position";

        /// <summary>
        /// Minimum position value supported by Home Assistant (0-100% range).
        /// </summary>
        private const Int32 MinPosition = 0;

        /// <summary>
        /// Maximum position value supported by Home Assistant (0-100% range).
        /// </summary>
        private const Int32 MaxPosition = 100;

        /// <summary>
        /// Minimum tilt position value (0-100% range).
        /// </summary>
        private const Int32 MinTiltPosition = 0;

        /// <summary>
        /// Maximum tilt position value (0-100% range).
        /// </summary>
        private const Int32 MaxTiltPosition = 100;

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
        /// Cached covers list to prevent registry refetching on every dropdown open.
        /// </summary>
        private List<CoverData>? _cachedCovers = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdvancedToggleCoversAction"/> class.
        /// Sets up action editor controls for cover selection and parameter configuration.
        /// </summary>
        public AdvancedToggleCoversAction()
        {
            this.Name = "HomeAssistant.AdvancedToggleCovers";
            this.DisplayName = "Advanced Set Covers";
            this.GroupName = "Covers";
            this.Description = "Control multiple Home Assistant covers with specific position and tilt settings. Set position (0-100%) and tilt position (0-100%) values for one or more covers.";

            // Primary cover selection (single)
            this.ActionEditor.AddControlEx(new ActionEditorListbox(ControlCovers, "Primary Cover (retry if empty)"));

            // Additional covers (comma-separated entity IDs)
            this.ActionEditor.AddControlEx(
                new ActionEditorTextbox(ControlAdditionalCovers, "Additional Covers (comma-separated)")
                    .SetPlaceholder("cover.living_room,cover.kitchen")
            );

            // Position control (0-100%)
            this.ActionEditor.AddControlEx(
                new ActionEditorTextbox(ControlPosition, "Position (0-100%)")
                    .SetPlaceholder("50")
            );

            // Tilt position control (0-100%)
            this.ActionEditor.AddControlEx(
                new ActionEditorTextbox(ControlTiltPosition, "Tilt Position (0-100%)")
                    .SetPlaceholder("50")
            );

            this.ActionEditor.ListboxItemsRequested += this.OnListboxItemsRequested;

            this._icons = new IconService(new Dictionary<String, String>
            {
                { IconId.Cover, "cover_icon.svg" } 
            });

            PluginLog.Info($"{LogPrefix} Constructor completed - dependency initialization deferred to OnLoad()");
        }

        /// <summary>
        /// Gets the command image for the action button.
        /// </summary>
        /// <param name="parameters">Action editor parameters.</param>
        /// <param name="width">Requested image width.</param>
        /// <param name="height">Requested image height.</param>
        /// <returns>Bitmap image showing a cover icon.</returns>
        protected override BitmapImage GetCommandImage(ActionEditorActionParameters parameters, Int32 width, Int32 height) =>
            // Using light bulb icon for now - will be replaced with proper cover icon
            this._icons.Get(IconId.Cover);

        /// <summary>
        /// Loads the action and initializes service dependencies using modern dependency injection pattern.
        /// Creates adapters for Home Assistant client, data services, and cover control.
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

                    // Use the singleton CoverStateManager from the main plugin
                    this._coverStateManager = haPlugin.CoverStateManager;
                    var existingCount = this._coverStateManager.GetTrackedEntityIds().Count();
                    PluginLog.Info($"{LogPrefix} Using singleton CoverStateManager with {existingCount} existing tracked entities");

                    this._registryService = new RegistryService();

                    // Initialize cover control service
                    this._coverSvc = new CoverControlService(this._ha);

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
        /// Disposes of managed resources, particularly the cover control service.
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
                this._coverSvc?.Dispose();
                this._coverSvc = null;

                // Don't dispose shared services - they're managed by the main plugin
                this._ha = null;
                this._dataService = null;
                this._dataParser = null;
                this._coverStateManager = null;
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
        /// Executes the advanced covers command with position and tilt control.
        /// Processes multiple covers with position and tilt position parameters.
        /// Always applies the user's configured position and tilt settings.
        /// </summary>
        /// <param name="ps">Action editor parameters containing user-configured values.</param>
        /// <returns><c>true</c> if all cover operations succeeded; otherwise, <c>false</c>.</returns>
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

                // Get selected covers
                var selectedCovers = new List<String>();

                // Add primary cover
                if (ps.TryGetString(ControlCovers, out var primaryCover) && !String.IsNullOrWhiteSpace(primaryCover))
                {
                    selectedCovers.Add(primaryCover.Trim());
                }

                // Add additional covers
                if (ps.TryGetString(ControlAdditionalCovers, out var additionalCovers) && !String.IsNullOrWhiteSpace(additionalCovers))
                {
                    var additionalList = additionalCovers.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => !String.IsNullOrEmpty(s) && !selectedCovers.Contains(s, StringComparer.OrdinalIgnoreCase));

                    selectedCovers.AddRange(additionalList);
                }

                if (!selectedCovers.Any())
                {
                    PluginLog.Warning($"{LogPrefix} RunCommand: No covers selected");
                    return false;
                }

                PluginLog.Info($"{LogPrefix} Press: Processing {selectedCovers.Count} covers with individual capabilities");

                // Parse control values
                var position = this.ParseIntParameter(ps, ControlPosition, MinPosition, MaxPosition);
                var tiltPosition = this.ParseIntParameter(ps, ControlTiltPosition, MinTiltPosition, MaxTiltPosition);

                // Process covers with individual capability filtering
                var success = this.ProcessCoversIndividually(selectedCovers, position, tiltPosition);

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
        /// Parses and validates an integer parameter from action editor parameters.
        /// Clamps the value to the specified range and logs warnings for invalid inputs.
        /// </summary>
        /// <param name="ps">Action editor parameters.</param>
        /// <param name="controlName">Name of the control to parse.</param>
        /// <param name="min">Minimum allowed value (inclusive).</param>
        /// <param name="max">Maximum allowed value (inclusive).</param>
        /// <returns>Parsed and clamped integer value, or <c>null</c> if parsing failed or value was empty.</returns>
        private Int32? ParseIntParameter(ActionEditorActionParameters ps, String controlName, Int32 min, Int32 max)
        {
            if (!ps.TryGetString(controlName, out var valueStr) || String.IsNullOrWhiteSpace(valueStr))
            {
                return null;
            }

            if (Int32.TryParse(valueStr, out var value))
            {
                var clamped = Math.Clamp(value, min, max);
                if (clamped != value)
                {
                    PluginLog.Debug($"{LogPrefix} Parameter {controlName}: {value} clamped to {clamped} (range {min}-{max})");
                }
                return clamped;
            }

            PluginLog.Warning($"{LogPrefix} Parameter {controlName}: failed to parse '{valueStr}' as integer");
            return null;
        }

        /// <summary>
        /// Processes multiple covers with individual capability filtering.
        /// Key difference from common capabilities: each cover uses its own maximum supported features.
        /// Always applies the user's configured position and tilt settings.
        /// </summary>
        /// <param name="entityIds">Collection of cover entity IDs to process.</param>
        /// <param name="position">Position value (0-100%) or null.</param>
        /// <param name="tiltPosition">Tilt position value (0-100%) or null.</param>
        /// <returns><c>true</c> if all covers processed successfully; otherwise, <c>false</c>.</returns>
        private Boolean ProcessCoversIndividually(IEnumerable<String> entityIds, Int32? position, Int32? tiltPosition)
        {
            PluginLog.Info($"{LogPrefix} Processing {entityIds.Count()} covers with user-configured settings: position={position}%, tiltPosition={tiltPosition}%");

            var success = true;

            foreach (var entityId in entityIds)
            {
                try
                {
                    // Get INDIVIDUAL capabilities for this specific cover
                    var individualCaps = this._coverStateManager?.GetCapabilities(entityId)
                        ?? new CoverCaps(true, false, false);

                    PluginLog.Info($"{LogPrefix} Processing {entityId} with individual capabilities: OnOff={individualCaps.OnOff}, Position={individualCaps.Position}, TiltPosition={individualCaps.TiltPosition}");

                    // Process this cover with ITS OWN capabilities
                    success &= this.ProcessSingleCover(entityId, individualCaps, position, tiltPosition);
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, $"{LogPrefix} Failed to process cover {entityId}");
                    success = false;
                }
            }

            return success;
        }

        /// <summary>
        /// Processes a single cover with the specified position and tilt parameters.
        /// Always applies the user's configured position and tilt settings.
        /// </summary>
        /// <param name="entityId">Cover entity ID.</param>
        /// <param name="caps">Cover capabilities.</param>
        /// <param name="position">Position value (0-100%) or null.</param>
        /// <param name="tiltPosition">Tilt position value (0-100%) or null.</param>
        /// <returns><c>true</c> if the operation succeeded; otherwise, <c>false</c>.</returns>
        private Boolean ProcessSingleCover(String entityId, CoverCaps caps, Int32? position, Int32? tiltPosition)
        {
            PluginLog.Info($"{LogPrefix} Processing cover: {entityId}");
            PluginLog.Info($"{LogPrefix} Cover capabilities: onoff={caps.OnOff} position={caps.Position} tiltPosition={caps.TiltPosition}");
            PluginLog.Info($"{LogPrefix} Input parameters: position={position}% tiltPosition={tiltPosition}%");

            if (this._coverSvc == null)
            {
                PluginLog.Error($"{LogPrefix} ProcessSingleCover: CoverControlService not available");
                return false;
            }

            PluginLog.Info($"{LogPrefix} Applying user-configured settings to cover {entityId}");

            var overallSuccess = true;
            var anyActionTaken = false;

            // Set position if supported and specified
            if (position.HasValue && caps.Position)
            {
                var pos = Math.Clamp(position.Value, MinPosition, MaxPosition);
                var posSuccess = this._coverSvc.SetCoverPositionAsync(entityId, pos).GetAwaiter().GetResult();
                PluginLog.Info($"{LogPrefix} HA SERVICE CALL: set_cover_position entity_id={entityId} position={pos} -> success={posSuccess}");
                overallSuccess &= posSuccess;
                anyActionTaken = true;

                if (posSuccess && this._coverStateManager != null)
                {
                    // Update local state
                    this._coverStateManager.UpdateCoverPosition(entityId, pos);
                    this._coverStateManager.UpdateCoverState(entityId, pos == 0 ? "closed" : "open");
                }
            }
            else if (position.HasValue && !caps.Position)
            {
                PluginLog.Warning($"{LogPrefix} Position {position.Value}% requested but not supported by {entityId}");
            }

            // Set tilt position if supported and specified
            if (tiltPosition.HasValue && caps.TiltPosition)
            {
                var tiltPos = Math.Clamp(tiltPosition.Value, MinTiltPosition, MaxTiltPosition);
                var tiltSuccess = this._coverSvc.SetCoverTiltPositionAsync(entityId, tiltPos).GetAwaiter().GetResult();
                PluginLog.Info($"{LogPrefix} HA SERVICE CALL: set_cover_tilt_position entity_id={entityId} tilt_position={tiltPos} -> success={tiltSuccess}");
                overallSuccess &= tiltSuccess;
                anyActionTaken = true;

                if (tiltSuccess && this._coverStateManager != null)
                {
                    // Update local state
                    this._coverStateManager.UpdateCoverTiltPosition(entityId, tiltPos);
                }
            }
            else if (tiltPosition.HasValue && !caps.TiltPosition)
            {
                PluginLog.Warning($"{LogPrefix} Tilt position {tiltPosition.Value}% requested but not supported by {entityId}");
            }

            // If no specific parameters were provided or supported, just open the cover
            if (!anyActionTaken && caps.OnOff)
            {
                PluginLog.Info($"{LogPrefix} No position/tilt parameters specified or supported, opening cover {entityId}");
                var openSuccess = this._coverSvc.OpenCoverAsync(entityId).GetAwaiter().GetResult();
                PluginLog.Info($"{LogPrefix} HA SERVICE CALL: open_cover entity_id={entityId} -> success={openSuccess}");
                overallSuccess = openSuccess;

                if (openSuccess && this._coverStateManager != null)
                {
                    // Update local state - cover is now open
                    this._coverStateManager.UpdateCoverState(entityId, "open");
                    this._coverStateManager.UpdateCoverPosition(entityId, 100);
                }
            }

            if (!overallSuccess)
            {
                var friendlyName = entityId; // Could be enhanced to get friendly name
                this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                    $"Failed to control cover {friendlyName}");
            }

            return overallSuccess;
        }

        /// <summary>
        /// Handles listbox items requested event for cover selection dropdown.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void OnListboxItemsRequested(Object? sender, ActionEditorListboxItemsRequestedEventArgs e)
        {
            if (!e.ControlName.EqualsNoCase(ControlCovers))
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
                        // Report configuration error to user
                        this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                            "Home Assistant URL and Token not configured");
                    }
                    else
                    {
                        e.AddItem("!not_connected", "Could not connect to Home Assistant", "Check URL/token");
                        // Report connection error to user
                        this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                            "Could not connect to Home Assistant");
                    }
                    return;
                }

                if (this._dataService == null)
                {
                    PluginLog.Error($"{LogPrefix} ListboxItemsRequested: DataService not available");
                    e.AddItem("!no_service", "Data service not available", "Plugin initialization error");
                    // Report service error to user
                    this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                        "Plugin initialization error");
                    return;
                }

                // Check cache first to avoid refetching registry data on every dropdown open
                var now = DateTime.Now;
                var cacheExpired = (now - this._cacheTimestamp).TotalMinutes > CacheTtlMinutes;

                List<CoverData> covers;
                if (this._cachedCovers != null && !cacheExpired)
                {
                    PluginLog.Info($"{LogPrefix} Using cached covers data ({this._cachedCovers.Count} covers, age: {(now - this._cacheTimestamp).TotalMinutes:F1}min)");
                    covers = this._cachedCovers;
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

                    // Initialize CoverStateManager using self-contained method
                    if (this._coverStateManager != null && this._dataService != null && this._dataParser != null)
                    {
                        var (success, errorMessage) = this._coverStateManager.InitOrUpdateAsync(this._dataService, this._dataParser, CancellationToken.None).GetAwaiter().GetResult();
                        if (!success)
                        {
                            PluginLog.Warning($"{LogPrefix} CoverStateManager.InitOrUpdateAsync failed: {errorMessage}");
                            this.Plugin.OnPluginStatusChanged(PluginStatus.Error,
                                $"Failed to load cover data: {errorMessage}");
                            e.AddItem("!init_failed", "Failed to load covers", errorMessage ?? "Check connection to Home Assistant");
                            return;
                        }
                    }

                    // Use registry-aware parsing
                    PluginLog.Info($"{LogPrefix} Fetching registry data for registry-aware cover parsing");

                    // Ensure _dataService is not null before using it
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

                    // Ensure _dataParser is not null before using it
                    if (this._dataParser == null)
                    {
                        PluginLog.Error($"{LogPrefix} DataParser is null when trying to parse registry data");
                        e.AddItem("!no_parser", "Data parser not available", "Plugin initialization error");
                        this.Plugin.OnPluginStatusChanged(PluginStatus.Error, "Data parser not available");
                        return;
                    }

                    // Parse registry data and cover states together
                    var registryData = this._dataParser.ParseRegistries(devJson, entJson, areaJson);
                    covers = this._dataParser.ParseCoverStates(json, registryData);

                    // Cache the results
                    this._cachedCovers = covers;
                    this._cacheTimestamp = now;
                    PluginLog.Info($"{LogPrefix} Cached {covers.Count} covers with registry data (TTL: {CacheTtlMinutes}min)");
                }

                // Iterate over parsed covers
                var count = 0;
                foreach (var cover in covers)
                {
                    e.AddItem(name: cover.EntityId, displayName: cover.EntityId, description: "Home Assistant cover");
                    count++;
                }

                PluginLog.Info($"{LogPrefix} List populated with {count} cover(s) using modern service architecture");

                // Clear any previous error status since we successfully loaded covers
                if (count > 0)
                {
                    this.Plugin.OnPluginStatusChanged(PluginStatus.Normal,
                        $"Successfully loaded {count} covers");
                }

                // Keep current selection
                var current = e.ActionEditorState?.GetControlValue(ControlCovers) as String;
                if (!String.IsNullOrEmpty(current))
                {
                    PluginLog.Info($"{LogPrefix} Keeping current selection: '{current}'");
                    e.SetSelectedItemName(current);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"{LogPrefix} List population failed");
                e.AddItem("!error", "Error reading covers", ex.Message);
            }
        }
    }
}