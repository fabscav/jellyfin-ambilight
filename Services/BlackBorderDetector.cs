// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Jellyfin Ambilight Contributors
// This file is part of Jellyfin Ambilight Plugin.
// Jellyfin Ambilight Plugin is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;

namespace Jellyfin.Plugin.Ambilight.Services;

/// <summary>
/// A detected letterbox/pillarbox border, expressed as inset depths in pixels.
/// </summary>
/// <param name="TopBottom">Depth of the horizontal bars along the top and bottom edges.</param>
/// <param name="LeftRight">Depth of the vertical bars along the left and right edges.</param>
/// <param name="Unknown">True when no non-black pixel was found, so the frame carries no usable information.</param>
public readonly record struct BlackBorder(int TopBottom, int LeftRight, bool Unknown)
{
    public static BlackBorder None => new(0, 0, false);

    /// <summary>
    /// Two unknown borders are equal regardless of their sizes, matching HyperHDR's
    /// BlackBorder::operator== (sources/blackborder/BlackBorderDetector.cpp:29-36). Without this the
    /// consistency counter could never settle while the picture is black.
    /// </summary>
    public bool Equals(BlackBorder other)
    {
        if (Unknown)
        {
            return other.Unknown;
        }

        return !other.Unknown && TopBottom == other.TopBottom && LeftRight == other.LeftRight;
    }

    public override int GetHashCode() => Unknown ? 0 : HashCode.Combine(TopBottom, LeftRight);
}

/// <summary>
/// Port of HyperHDR's "default" black border detector
/// (sources/blackborder/BlackBorderDetector.cpp:38-92), operating on a packed RGB24 buffer.
/// </summary>
public static class BlackBorderDetector
{
    /// <summary>
    /// Byte cutoff below which all three channels count as black. HyperHDR's configured default is 5,
    /// read as a percentage (BlackBorderProcessor.cpp:211), giving ceil(0.05 * 255) = 13.
    /// </summary>
    public const byte DefaultThreshold = 13;

    /// <summary>
    /// Walks inward from each edge over at most a third of the axis, testing three lines per axis so a
    /// centred dark object cannot be mistaken for a border.
    /// </summary>
    public static BlackBorder Detect(byte[] frame, int width, int height, byte threshold)
    {
        if (width <= 0 || height <= 0)
        {
            return new BlackBorder(0, 0, true);
        }

        int width33 = width / 3;
        int height33 = height / 3;
        int width66 = width33 * 2;
        int height66 = height33 * 2;
        int xCenter = width / 2;
        int yCenter = height / 2;

        // Last valid index on each axis.
        int maxX = width - 1;
        int maxY = height - 1;

        bool IsBlack(int x, int y)
        {
            int idx = (y * width + x) * 3;
            return frame[idx] < threshold && frame[idx + 1] < threshold && frame[idx + 2] < threshold;
        }

        int firstNonBlackX = -1;
        for (int x = 0; x < width33; x++)
        {
            if (!IsBlack(maxX - x, yCenter) || !IsBlack(x, height33) || !IsBlack(x, height66))
            {
                firstNonBlackX = x;
                break;
            }
        }

        int firstNonBlackY = -1;
        for (int y = 0; y < height33; y++)
        {
            if (!IsBlack(xCenter, maxY - y) || !IsBlack(width33, y) || !IsBlack(width66, y))
            {
                firstNonBlackY = y;
                break;
            }
        }

        bool unknown = firstNonBlackX == -1 || firstNonBlackY == -1;

        return new BlackBorder(
            TopBottom: unknown ? 0 : firstNonBlackY,
            LeftRight: unknown ? 0 : firstNonBlackX,
            Unknown: unknown);
    }
}

/// <summary>
/// Temporal hysteresis around <see cref="BlackBorderDetector"/>, ported from HyperHDR's
/// BlackBorderProcessor (sources/blackborder/BlackBorderProcessor.cpp:91-199).
///
/// A single frame is never trusted: a new border must repeat <see cref="BorderFrameCount"/> times
/// before it is applied, and a short burst of disagreeing frames is discarded outright without
/// resetting the consistency count. That is what keeps a fade to black or a dark scene from yanking
/// the LED zones around.
/// </summary>
public sealed class BlackBorderTracker
{
    /// <summary>Consecutive detections required before a new border is applied.</summary>
    private const int BorderFrameCount = 50;

    /// <summary>Consecutive detections required before falling back to "unknown".</summary>
    private const int UnknownFrameCount = 600;

    /// <summary>Disagreeing frames tolerated before the candidate border is allowed to compete.</summary>
    private const int MaxInconsistentCount = 10;

    /// <summary>Extra pixels trimmed past a detected bar, to step over its soft edge.</summary>
    private const int BlurRemoveCount = 1;

    private readonly byte _threshold;
    private BlackBorder _previousDetected;
    private int _consistentCount;
    private int _inconsistentCount;

    public BlackBorderTracker(byte threshold = BlackBorderDetector.DefaultThreshold)
    {
        _threshold = threshold;

        // Seeded exactly as HyperHDR does (BlackBorderProcessor.cpp:26-34): both borders start
        // "unknown" and the inconsistency counter starts already at its limit. Together these make the
        // first real detection apply immediately — the alternative (starting at a known zero border)
        // costs ~60 frames of lock-on during which a scope film is still extracted with its bars.
        // Subsequent changes still need BorderFrameCount consistent frames.
        Current = Unknown;
        _previousDetected = Unknown;
        _inconsistentCount = MaxInconsistentCount;
    }

    private static BlackBorder Unknown => new(0, 0, true);

    /// <summary>The border currently applied to the LED zone mapping.</summary>
    public BlackBorder Current { get; private set; }

    /// <summary>
    /// Runs detection on one frame and folds it into the tracked state.
    /// Returns true only when <see cref="Current"/> changed and the zones need rebuilding.
    /// </summary>
    public bool ProcessFrame(byte[] frame, int width, int height)
    {
        var detected = BlackBorderDetector.Detect(frame, width, height, _threshold);

        // Widen a real border slightly to clear the blur at the bar edge.
        if (!detected.Unknown && (detected.TopBottom > 0 || detected.LeftRight > 0))
        {
            detected = detected with
            {
                TopBottom = detected.TopBottom > 0 ? detected.TopBottom + BlurRemoveCount : 0,
                LeftRight = detected.LeftRight > 0 ? detected.LeftRight + BlurRemoveCount : 0
            };
        }

        return Update(detected);
    }

    private bool Update(BlackBorder detected)
    {
        if (detected == _previousDetected)
        {
            _consistentCount++;
            _inconsistentCount = 0;
        }
        else
        {
            _inconsistentCount++;
            if (_inconsistentCount <= MaxInconsistentCount)
            {
                // Too few dissenting frames to be believed — drop this one and keep the running
                // consistency count for the previous candidate intact.
                return false;
            }

            _previousDetected = detected;
            _consistentCount = 0;
        }

        if (Current == detected)
        {
            _inconsistentCount = 0;
            return false;
        }

        if (detected.Unknown)
        {
            if (_consistentCount == UnknownFrameCount)
            {
                Current = detected;
                return true;
            }
        }
        else if (Current.Unknown || _consistentCount == BorderFrameCount)
        {
            Current = detected;
            return true;
        }

        return false;
    }
}
