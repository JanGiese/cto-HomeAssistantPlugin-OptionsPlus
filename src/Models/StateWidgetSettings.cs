namespace Loupedeck.HomeAssistantPlugin.Models
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Text.RegularExpressions;

    internal sealed class StateWidgetSettings
    {
        internal static readonly IReadOnlyDictionary<String, String> BuiltinIcons = new Dictionary<String, String>(StringComparer.Ordinal)
        {
            ["light_on"] = "light_on_icon.svg",
            ["light_off"] = "light_off_icon.svg",
            ["light"] = "light_bulb_icon.svg",
            ["switch_on"] = "switch_on_icon.svg",
            ["switch_off"] = "switch_off_icon.svg",
            ["switch"] = "switch_icon.svg",
            ["issue"] = "issue_status_icon.svg",
            ["script"] = "run_script_icon.svg",
            ["cover"] = "cover_icon.svg"
        };

        private readonly Dictionary<String, String> _icons = new(StringComparer.Ordinal);

        internal String EntityId { get; }
        internal String Domain { get; } = String.Empty;
        internal String Service { get; } = String.Empty;
        internal String TargetEntityId { get; }
        internal JsonElement? Data { get; }
        internal String Fallback { get; } = "builtin:issue";
        internal String? Error { get; }
        internal String ErrorControl { get; private set; } = StateWidgetAction.EntityControl;

        internal StateWidgetSettings(String entityId, String service, String targetEntityId, String data, String icons, String fallback)
        {
            this.EntityId = entityId.Trim();
            this.TargetEntityId = targetEntityId.Trim();
            if (!IsEntityId(this.EntityId))
            {
                this.Error = "Select a state entity.";
                return;
            }
            if (!IsEntityId(this.TargetEntityId))
            {
                this.ErrorControl = StateWidgetAction.TargetControl;
                this.Error = "Select an explicit target entity.";
                return;
            }
            this.ErrorControl = StateWidgetAction.ServiceControl;
            var parts = service.Trim().Split('.');
            if (parts.Length != 2 || !IsEntityId(service.Trim()))
            {
                this.Error = "Service must be domain.service, for example light.toggle.";
                return;
            }
            this.Domain = parts[0];
            this.Service = parts[1];
            try
            {
                this.ErrorControl = StateWidgetAction.DataControl;
                if (!String.IsNullOrWhiteSpace(data))
                {
                    using var document = JsonDocument.Parse(data);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        this.Error = "Service data must be a JSON object.";
                        return;
                    }
                    // Routing belongs to the explicit target, not service_data.
                    if (document.RootElement.TryGetProperty("entity_id", out _))
                    {
                        this.Error = "Use Target entity instead of entity_id in service data.";
                        return;
                    }
                    this.Data = document.RootElement.Clone();
                }
                if (!String.IsNullOrWhiteSpace(icons))
                {
                    this.ErrorControl = StateWidgetAction.IconsControl;
                    using var document = JsonDocument.Parse(icons);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        this.Error = "State icons must be a JSON object.";
                        return;
                    }
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.String ||
                            !IsIconReference(property.Value.GetString()!) ||
                            !this._icons.TryAdd(property.Name, property.Value.GetString()!))
                        {
                            this.Error = "State icons need unique state keys and builtin: names or absolute PNG/SVG paths.";
                            return;
                        }
                    }
                }
                if (!String.IsNullOrWhiteSpace(fallback))
                {
                    this.ErrorControl = StateWidgetAction.FallbackControl;
                    if (!IsIconReference(fallback.Trim()))
                    {
                        this.Error = "Fallback icon must be a builtin: name or absolute PNG/SVG path.";
                        return;
                    }
                    this.Fallback = fallback.Trim();
                }
            }
            catch (JsonException)
            {
                this.Error = "Service data and state icons must contain valid JSON objects.";
            }
        }

        internal static Boolean IsEntityId(String value) =>
            Regex.IsMatch(value, @"^[a-z0-9_]+\.[a-z0-9_]+$", RegexOptions.CultureInvariant);

        internal static Boolean IsIconReference(String value)
        {
            return value.StartsWith("builtin:", StringComparison.Ordinal)
                ? BuiltinIcons.ContainsKey(value.Substring("builtin:".Length))
                : Path.IsPathFullyQualified(value) &&
                (String.Equals(Path.GetExtension(value), ".png", StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(Path.GetExtension(value), ".svg", StringComparison.OrdinalIgnoreCase));
        }

        internal String IconFor(String state)
        {
            if (this._icons.TryGetValue(state, out var reference))
            {
                return reference;
            }
            if (state == "on" || state == "off")
            {
                if (this.EntityId.StartsWith("light.", StringComparison.Ordinal))
                {
                    return "builtin:light_" + state;
                }
                if (this.EntityId.StartsWith("switch.", StringComparison.Ordinal))
                {
                    return "builtin:switch_" + state;
                }
            }
            return this.Fallback;
        }
    }

    internal sealed record StateWidgetEntity(String EntityId, String State, String FriendlyName)
    {
        internal Boolean IsAvailable => !String.IsNullOrEmpty(this.State) && this.State != "unknown" && this.State != "unavailable";

        internal static StateWidgetEntity? Parse(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("entity_id", out var id) || id.ValueKind != JsonValueKind.String ||
                !element.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
                !StateWidgetSettings.IsEntityId(id.GetString()!))
            {
                return null;
            }
            var name = id.GetString()!;
            if (element.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object &&
                attributes.TryGetProperty("friendly_name", out var friendly) && friendly.ValueKind == JsonValueKind.String)
            {
                name = friendly.GetString()!;
            }
            return new StateWidgetEntity(id.GetString()!, state.GetString()!, name);
        }
    }
}