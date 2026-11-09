namespace Loupedeck.HomeAssistantPlugin
{
    using System;
    using System.Linq;
    using System.Threading;

    using Loupedeck.HomeAssistantPlugin.Models;
    using Loupedeck.HomeAssistantPlugin.Services;

    public sealed class StateWidgetAction : ActionEditorCommand
    {
        internal const String EntityControl = "state_entity";
        internal const String ServiceControl = "service";
        internal const String TargetControl = "target_entity";
        internal const String DataControl = "service_data";
        internal const String IconsControl = "state_icons";
        internal const String FallbackControl = "fallback_icon";
        private static readonly String[] Controls = { EntityControl, ServiceControl, TargetControl, DataControl, IconsControl, FallbackControl };
        private readonly StateWidgetIconResolver _icons = new();
        private StateWidgetSession? _session;

        public StateWidgetAction()
        {
            this.Name = "HomeAssistant.StateWidget";
            this.DisplayName = "HA: State Widget";
            this.GroupName = "Home Assistant";
            this.Description = "Display an entity's state icon and call a configured Home Assistant service.";
            this.SetWidget(true);
            this.ActionEditor.AddControlEx(new ActionEditorListbox(EntityControl, "State entity"));
            this.ActionEditor.AddControlEx(new ActionEditorTextbox(ServiceControl, "Service").SetPlaceholder("light.toggle"));
            this.ActionEditor.AddControlEx(new ActionEditorListbox(TargetControl, "Target entity (required)"));
            this.ActionEditor.AddControlEx(new ActionEditorTextbox(DataControl, "Service data (JSON object)").SetPlaceholder("{\"transition\":1}"));
            this.ActionEditor.AddControlEx(new ActionEditorTextbox(IconsControl, "State icons (JSON object)")
                .SetPlaceholder("{\"on\":\"builtin:light_on\",\"off\":\"builtin:light_off\"}"));
            this.ActionEditor.AddControlEx(new ActionEditorTextbox(FallbackControl, "Fallback icon").SetPlaceholder("builtin:issue"));
            this.ActionEditor.ListboxItemsRequested += this.OnListboxItemsRequested;
            this.ActionEditor.Started += (_, e) => ValidateEditor(e.ActionEditorState);
            this.ActionEditor.ControlValueChanged += (_, e) =>
            {
                ValidateEditor(e.ActionEditorState);
                this.ActionImageChanged();
            };
        }

        internal static StateWidgetSettings ReadSettings(ActionEditorActionParameters parameters) =>
            new(parameters.GetString(EntityControl, String.Empty), parameters.GetString(ServiceControl, String.Empty),
                parameters.GetString(TargetControl, String.Empty), parameters.GetString(DataControl, String.Empty),
                parameters.GetString(IconsControl, String.Empty), parameters.GetString(FallbackControl, String.Empty));

        private static void ValidateEditor(ActionEditorState state)
        {
            var settings = ReadSettings(new ActionEditorActionParameters(Controls.ToDictionary(id => id, id => state.GetControlValue(id) ?? String.Empty)));
            foreach (var control in Controls)
            {
                state.SetValidity(control, settings.Error == null || settings.ErrorControl != control,
                    settings.ErrorControl == control ? settings.Error ?? String.Empty : String.Empty);
            }
        }

        protected override Boolean OnLoad()
        {
            if (this.Plugin is not HomeAssistantPlugin plugin)
            {
                return false;
            }
            this._session = plugin.StateWidgets;
            this._session.Changed += this.OnStateChanged;
            this._session.Start();
            return true;
        }

        protected override Boolean OnUnload()
        {
            if (this._session != null)
            {
                this._session.Changed -= this.OnStateChanged;
                this._session = null;
            }
            this._icons.Clear();
            return true;
        }

        private void OnStateChanged() => this.ActionImageChanged();

        protected override Boolean RunCommand(ActionEditorActionParameters parameters)
        {
            if (this._session == null)
            {
                return false;
            }
            var result = this._session.ExecuteAsync(ReadSettings(parameters), CancellationToken.None).GetAwaiter().GetResult();
            if (!result.Ok)
            {
                this.Plugin.OnPluginStatusChanged(PluginStatus.Warning, result.Error ?? "Service call failed.");
                this.ActionImageChanged();
            }
            return result.Ok;
        }

        protected override String GetCommandDisplayName(ActionEditorActionParameters parameters)
        {
            var settings = ReadSettings(parameters);
            return String.IsNullOrEmpty(settings.EntityId) ? "HA: State Widget" : settings.EntityId;
        }

        protected override BitmapImage GetCommandImage(ActionEditorActionParameters parameters, Int32 width, Int32 height)
        {
            var settings = ReadSettings(parameters);
            var entity = this._session?.Get(settings.EntityId);
            var available = settings.Error == null && this._session?.IsConnected == true && entity?.IsAvailable == true;
            var detail = settings.Error != null ? "Check settings" :
                this._session?.IsConnected != true ? this._session?.Status ?? "Connecting..." :
                entity == null ? "Entity not found" : entity.State;
            var image = this._icons.Get(available ? settings.IconFor(entity!.State) : settings.Fallback, settings.Fallback);
            return Draw(width, height, image, detail, available);
        }

        internal static BitmapImage Draw(Int32 width, Int32 height, BitmapImage? image, String detail, Boolean available)
        {
            using var bitmap = new BitmapBuilder(width, height);
            bitmap.Clear(available ? new BitmapColor(0, 0, 0) : new BitmapColor(45, 45, 45));
            var iconHeight = height * 2 / 3;
            if (image != null && image.Width > 0 && image.Height > 0)
            {
                var scale = Math.Min((width - 8.0) / image.Width, (iconHeight - 4.0) / image.Height);
                var w = Math.Max(1, (Int32)(image.Width * scale));
                var h = Math.Max(1, (Int32)(image.Height * scale));
                bitmap.DrawImage(image, (width - w) / 2, (iconHeight - h) / 2, w, h);
            }
            bitmap.DrawText(detail.Length > 80 ? detail.Substring(0, 77) + "..." : detail,
                2, iconHeight, width - 4, height - iconHeight, fontSize: Math.Max(9, width / 9),
                color: new BitmapColor(255, 255, 255));
            return bitmap.ToImage();
        }

        private void OnListboxItemsRequested(Object? sender, ActionEditorListboxItemsRequestedEventArgs e)
        {
            if ((e.ControlName != EntityControl && e.ControlName != TargetControl) || this._session == null)
            {
                return;
            }
            var entities = this._session.Entities;
            foreach (var entity in entities)
            {
                e.AddItem(entity.EntityId, entity.EntityId, entity.EntityId);
            }
            var selected = e.ActionEditorState?.GetControlValue(e.ControlName);
            if (!String.IsNullOrEmpty(selected) && !entities.Any(entity => entity.EntityId == selected))
            {
                e.AddItem(selected, selected, "Currently unavailable");
            }
            if (!String.IsNullOrEmpty(selected))
            {
                e.SetSelectedItemName(selected);
            }
            if (entities.Count == 0)
            {
                this.Plugin.OnPluginStatusChanged(PluginStatus.Warning, this._session.Status + " - reopen the entity list after connecting.");
            }
        }
    }
}