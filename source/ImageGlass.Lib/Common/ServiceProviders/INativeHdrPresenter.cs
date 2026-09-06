/*
ImageGlass - A Fast, Seamless Photo Viewer
Copyright (C) 2010 - 2026 DUONG DIEU PHAP
Project homepage: https://imageglass.org

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/
using Avalonia;
using Avalonia.Controls;
using ImageGlass.Common.Photoing;
using SkiaSharp;
using System;

namespace ImageGlass.Common.ServiceProviders;

/// <summary>
/// Snapshot of the viewer navigation-button visual state that must be mirrored by a native HDR
/// presentation surface when HWND airspace hides the Avalonia overlay.
/// </summary>
public readonly record struct NativeHdrNavOverlayState(
    bool Enabled,
    long Revision,
    double LeftProgress,
    double RightProgress,
    bool LeftPressed,
    bool RightPressed,
    Rect LeftButtonRect,
    Rect RightButtonRect);

/// <summary>
/// Optional platform presenter that can place a decoded HDR raster on a native
/// operating-system HDR presentation surface instead of the SDR Avalonia surface.
/// </summary>
/// <remarks>
/// The normal Avalonia-rendered image remains underneath as a fallback. Implementations
/// must therefore hide their native surface immediately when presentation is unsupported
/// or fails.
/// </remarks>
public interface INativeHdrPresenter : IDisposable
{
    /// <summary>Whether the native presenter has been initialized for a window.</summary>
    bool IsInitialized { get; }

    /// <summary>Whether a native HDR surface is currently visible.</summary>
    bool IsPresenting { get; }

    /// <summary>Initializes the presenter for the owning top-level window.</summary>
    void Initialize(Window window, bool displayHdrEnabled);

    /// <summary>
    /// Notifies the presenter that the owning window moved to a display whose Advanced Color
    /// state may have changed. Implementations may invalidate display-specific resources.
    /// </summary>
    void OnDisplayChanged(bool displayHdrEnabled);

    /// <summary>
    /// Returns whether the current display and source metadata are eligible for native HDR.
    /// This deliberately excludes formats that still require transfer/gamut conversion.
    /// </summary>
    bool CanPresent(PhotoMetadata metadata);

    /// <summary>
    /// Presents the supplied source image over <paramref name="host"/> using the source and
    /// destination rectangles already calculated by the viewer.
    /// </summary>
    bool TryPresent(
        Control host,
        SKImage image,
        PhotoMetadata metadata,
        Rect sourceRect,
        Rect destinationRect,
        NativeHdrNavOverlayState navOverlay,
        double renderScaling);

    /// <summary>Hides the native surface without discarding the SDR fallback.</summary>
    void Hide();
}
