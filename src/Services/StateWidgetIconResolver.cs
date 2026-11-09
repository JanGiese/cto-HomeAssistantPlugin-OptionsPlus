namespace Loupedeck.HomeAssistantPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    using Loupedeck.HomeAssistantPlugin.Models;

    internal sealed class StateWidgetIconResolver
    {
        private readonly IResourceProvider _resources = new PluginResourceProvider(typeof(HomeAssistantPlugin).Assembly);
        private readonly Dictionary<String, (DateTime Modified, Int64 Length, BitmapImage? Image)> _cache = new(StringComparer.Ordinal);
        private readonly Object _gate = new();

        internal BitmapImage? Get(String reference, String fallback)
        {
            lock (this._gate)
            {
                return this.Load(reference) ?? this.Load(fallback) ?? this.Load("builtin:issue");
            }
        }

        private BitmapImage? Load(String reference)
        {
            try
            {
                var builtin = reference.StartsWith("builtin:", StringComparison.Ordinal);
                var modified = DateTime.MinValue;
                Int64 length = 0;
                if (!builtin)
                {
                    var file = new FileInfo(reference);
                    if (!file.Exists)
                    {
                        this._cache.Remove(reference);
                        return null;
                    }
                    modified = file.LastWriteTimeUtc;
                    length = file.Length;
                }
                if (this._cache.TryGetValue(reference, out var cached) && cached.Modified == modified && cached.Length == length)
                {
                    return cached.Image;
                }
                var image = builtin
                    ? StateWidgetSettings.BuiltinIcons.TryGetValue(reference.Substring("builtin:".Length), out var resource)
                        ? this._resources.LoadImage(resource) : null
                    : String.Equals(Path.GetExtension(reference), ".svg", StringComparison.OrdinalIgnoreCase)
                        ? BitmapImage.FromSvg(File.ReadAllText(reference)) : BitmapImage.FromFile(reference);
                // BitmapImage holds managed encoded bytes and is not IDisposable.
                if (this._cache.Count >= 64)
                {
                    this._cache.Clear();
                }
                this._cache[reference] = (modified, length, image);
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal void Clear()
        {
            lock (this._gate)
            {
                this._cache.Clear();
            }
        }
    }
}