// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Jellyfin Ambilight Contributors
// This file is part of Jellyfin Ambilight Plugin.
// Jellyfin Ambilight Plugin is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Ambilight
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string JellyfinBaseUrl { get; set; } = string.Empty;
        public string JellyfinApiKey { get; set; } = string.Empty;
        
        // Extraction
        public string ExtractionPriority { get; set; } = "newest_first";
        public bool ExtractNewlyAddedItems { get; set; } = true;
        public int MaxConcurrentExtractions { get; set; } = 1;
        public string HardwareAcceleration { get; set; } = "auto"; // "auto", "none", "vaapi", "qsv", "cuda", "videotoolbox"
        
        // WLED Device Mappings
        public string DeviceMatchField { get; set; } = "DeviceName";
        public List<DeviceMapping> DeviceMappings { get; set; } = new();
        
        // Ambilight Extraction Settings (for creating binary files)
        public int AmbilightTopLedCount { get; set; } = 89;
        public int AmbilightBottomLedCount { get; set; } = 89;
        public int AmbilightLeftLedCount { get; set; } = 49;
        public int AmbilightRightLedCount { get; set; } = 49;
        // Ambilight Visual Settings (global preferences)
        public double AmbilightSyncLeadSeconds { get; set; } = 0.2;
        /// <summary>
        /// Smoothing window in seconds for temporal blending between frames.
        /// Set to 0 to disable smoothing entirely.
        /// Higher values = smoother but more lag; lower values = more responsive but can flicker.
        /// </summary>
        public double AmbilightSmoothSeconds { get; set; } = 0.12;
        /// <summary>
        /// Applied as pow(x, gamma) — values above 1 darken, matching HyperHDR's user gamma.
        /// When <see cref="AmbilightAutoBrightness"/> is enabled this is instead used as the base for
        /// the legacy scene-adaptive lift, where higher values brighten.
        /// </summary>
        public double AmbilightGamma { get; set; } = 1.5;
        public double AmbilightSaturation { get; set; } = 1.0;

        /// <summary>
        /// Flat output multiplier applied when <see cref="AmbilightAutoBrightness"/> is disabled.
        /// </summary>
        public double AmbilightBrightness { get; set; } = 1.0;

        /// <summary>
        /// Target mean luminance (0-255) for the legacy auto-gain loop.
        /// Only read when <see cref="AmbilightAutoBrightness"/> is enabled.
        /// </summary>
        public double AmbilightBrightnessTarget { get; set; } = 60.0;

        /// <summary>
        /// When true, zone colors are picked with the legacy Sobel edge-detection weighting instead of a
        /// plain mean in linear light. The edge weighting biases each zone toward high-contrast pixels,
        /// which makes dark scenes read far too bright. Changing this requires re-extracting existing items.
        /// </summary>
        public bool AmbilightEdgeWeightedExtraction { get; set; } = false;

        /// <summary>
        /// When true, restores the legacy scene-adaptive brightness behaviour: a gamma curve that lifts
        /// harder as the frame darkens, plus an auto-gain loop driving each frame toward
        /// <see cref="AmbilightBrightnessTarget"/>.
        /// </summary>
        public bool AmbilightAutoBrightness { get; set; } = false;


        public double AmbilightGammaRed { get; set; } = 1.0;
        public double AmbilightGammaGreen { get; set; } = 1.0;
        public double AmbilightGammaBlue { get; set; } = 1.0;
        
        public double AmbilightRedBoost { get; set; } = 0.0;
        public double AmbilightBlueBoost { get; set; } = 0.0;
        public double AmbilightGreenBoost { get; set; } = 0.0;
        
        public double AmbilightMinLedBrightness { get; set; } = 0.0;

        /// <summary>
        /// Libraries (by Id) that should be excluded from extraction.
        /// </summary>
        public List<string> ExcludedLibraryIds { get; set; } = new();

        /// <summary>
        /// Folder where ambilight binary files are stored. Filenames are {ItemId}.bin.
        /// </summary>
        public string AmbilightDataFolder { get; set; } = "/data/ambilight";

        /// <summary>
        /// When true, enables verbose logging for play/pause/seek, binary load, WLED connection and broadcast.
        /// </summary>
        public bool Debug { get; set; } = false;

        public string? RustExtractorPath { get; set; }
    }
    
    public class DeviceMapping
    {
        public string DeviceIdentifier { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 19446;
        
        // LED Layout Configuration (per WLED instance)
        public int TopLedCount { get; set; } = 89;
        public int BottomLedCount { get; set; } = 89;
        public int LeftLedCount { get; set; } = 49;
        public int RightLedCount { get; set; } = 49;
        public int InputPosition { get; set; } = 0;
    }
}
