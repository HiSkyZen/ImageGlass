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
using ImageGlass.Common;
using ImageGlass.Common.Extensions;
using ImageGlass.Common.Loggers;
using ImageGlass.Common.Photoing;
using ImageGlass.Common.ServiceProviders;
using ImageGlass.Common.Types;
using SharpGen.Runtime;
using SkiaSharp;
using System;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2D = Vortice.Direct2D1.D2D1;
using D2DBitmapInterpolationMode = Vortice.Direct2D1.BitmapInterpolationMode;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;

namespace ImageGlass.Win32.Common.ServiceProviders;

/// <summary>
/// Windows HDR presenter for linear scRGB rasters.
/// </summary>
/// <remarks>
/// The presenter creates a disabled child HWND over the exact image destination rectangle and
/// binds an FP16 flip-model swapchain to it. The swapchain is explicitly tagged
/// <see cref="ColorSpaceType.RgbFullG10NoneP709"/>, so DWM/Advanced Color performs the final
/// display mapping instead of ImageGlass compressing the source to SDR first.
///
/// The Avalonia renderer remains underneath this HWND at all times and is never mutated; if any
/// native step fails the HWND is hidden and the existing ImageGlass rendering becomes visible
/// immediately.
/// </remarks>
public sealed partial class Win32NativeHdrPresenter : PhDisposable, INativeHdrPresenter
{
    private static readonly bool NativeHdrDisabled =
        Environment.GetEnvironmentVariable("IMAGEGLASS_NATIVE_HDR") is { } value
        && (value.Equals("0", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase));

    private static readonly bool NativeHdrForced =
        Environment.GetEnvironmentVariable("IMAGEGLASS_NATIVE_HDR_FORCE") is { } force
        && (force.Equals("1", StringComparison.OrdinalIgnoreCase)
            || force.Equals("true", StringComparison.OrdinalIgnoreCase)
            || force.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static readonly D3DFeatureLevel[] FeatureLevels =
    [
        D3DFeatureLevel.Level_11_1,
        D3DFeatureLevel.Level_11_0,
        D3DFeatureLevel.Level_10_1,
        D3DFeatureLevel.Level_10_0,
    ];

    private const uint WS_CHILD = 0x40000000;
    private const uint WS_DISABLED = 0x08000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    private Window? _window;
    private nint _parentHwnd;
    private nint _childHwnd;
    private bool _displayHdrEnabled;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _d3dContext;
    private IDXGIFactory2? _dxgiFactory;
    private IDXGISwapChain1? _swapChain;
    private IDXGISwapChain3? _swapChain3;

    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private ID2D1Bitmap1? _targetBitmap;
    private ID2D1Bitmap1? _sourceBitmap;
    private ID3D11Texture2D? _sourceTexture;

    private SKImage? _sourceIdentity;
    private int _pixelWidth;
    private int _pixelHeight;
    private int _windowX = int.MinValue;
    private int _windowY = int.MinValue;
    private int _windowWidth;
    private int _windowHeight;
    private Avalonia.Rect _lastSourceRect;
    private Avalonia.Rect _lastDestinationRect;
    private bool _hasLastSourceRect;
    private bool _hasLastDestinationRect;
    private long _lastNavRevision = long.MinValue;


    public bool IsInitialized { get; private set; }

    public bool IsPresenting { get; private set; }


    public void Initialize(Window window, bool displayHdrEnabled)
    {
        if (IsInitialized)
        {
            PhotoTrace.Mark("native-hdr:init", null,
                $"already initialized hwnd=0x{_parentHwnd:X}, displayHdr={_displayHdrEnabled}");
            return;
        }

        _window = window;
        _parentHwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _displayHdrEnabled = displayHdrEnabled;
        IsInitialized = _parentHwnd != IntPtr.Zero;

        PhotoTrace.Mark("native-hdr:init", null,
            $"disabled={NativeHdrDisabled}, forced={NativeHdrForced}, hwnd=0x{_parentHwnd:X}, initialized={IsInitialized}, displayHdr={_displayHdrEnabled}");
    }


    public void OnDisplayChanged(bool displayHdrEnabled)
    {
        var old = _displayHdrEnabled;
        _displayHdrEnabled = displayHdrEnabled;

        PhotoTrace.Mark("native-hdr:display", null,
            $"hdr {old} -> {_displayHdrEnabled}, initialized={IsInitialized}, presenting={IsPresenting}");

        // DXGI color-space support is output-dependent. Tear down presentation resources on every
        // monitor / Advanced Color transition so the next frame renegotiates the swapchain.
        Hide();
        ReleaseGraphics();
    }


    public bool CanPresent(PhotoMetadata metadata)
    {
        return !NativeHdrDisabled
            && IsInitialized
            && (_displayHdrEnabled || NativeHdrForced)
            && metadata.HdrTransferFn == HdrTransferFunction.ScRgb;
    }


    private string DescribeEligibility(PhotoMetadata metadata)
    {
        return $"disabled={NativeHdrDisabled}, forced={NativeHdrForced}, initialized={IsInitialized}, displayHdr={_displayHdrEnabled}, "
            + $"transfer={metadata.HdrTransferFn}, isHdr={metadata.IsHdr}, canPresent={CanPresent(metadata)}";
    }


    public bool TryPresent(
        Control host,
        SKImage image,
        PhotoMetadata metadata,
        Avalonia.Rect sourceRect,
        Avalonia.Rect destinationRect,
        NativeHdrNavOverlayState navOverlay,
        double renderScaling)
    {
        var eligible = CanPresent(metadata);
        var disposed = image.IsDisposed();
        var supportedColorType = image.ColorType is SKColorType.RgbaF16 or SKColorType.RgbaF16Clamped;

        if (!eligible
            || disposed
            || sourceRect.IsEmpty
            || destinationRect.IsEmpty
            || renderScaling <= 0
            || !supportedColorType)
        {
            PhotoTrace.Mark("native-hdr:reject", metadata.FilePath,
                $"{DescribeEligibility(metadata)}, image={image.Width}x{image.Height}/{image.ColorType}, disposed={disposed}, "
                + $"src={sourceRect}, dst={destinationRect}, dpi={renderScaling:0.###}");

            Hide();
            return false;
        }

        if (_window is null
            || host.TranslatePoint(destinationRect.Position, _window) is not { } windowPoint)
        {
            PhotoTrace.Mark("native-hdr:reject", metadata.FilePath,
                $"coordinate translation failed, windowNull={_window is null}, dst={destinationRect}");

            Hide();
            return false;
        }

        var x = checked((int)Math.Round(windowPoint.X * renderScaling));
        var y = checked((int)Math.Round(windowPoint.Y * renderScaling));
        var width = Math.Max(1, checked((int)Math.Round(destinationRect.Width * renderScaling)));
        var height = Math.Max(1, checked((int)Math.Round(destinationRect.Height * renderScaling)));

        try
        {
            EnsureChildWindow();

            var sizeChanged = width != _pixelWidth || height != _pixelHeight;
            if (_device is null)
            {
                CreateDeviceResources();
            }

            if (_swapChain is null)
            {
                PhotoTrace.Mark("native-hdr:swapchain-create", metadata.FilePath,
                    $"{width}x{height}, source={image.Width}x{image.Height}/{image.ColorType}");
                CreateSwapChain(width, height);
                sizeChanged = false;
            }
            else if (sizeChanged)
            {
                ResizeSwapChain(width, height);
            }

            MoveChildWindow(x, y, width, height);

            var sourceChanged = !ReferenceEquals(_sourceIdentity, image) || _sourceBitmap is null;
            if (sourceChanged)
            {
                PhotoTrace.Mark("native-hdr:upload", metadata.FilePath,
                    $"{image.Width}x{image.Height}/{image.ColorType}");
                UploadSource(image);
            }

            var sourceRectChanged = !_hasLastSourceRect || _lastSourceRect != sourceRect;
            var destinationRectChanged = !_hasLastDestinationRect || _lastDestinationRect != destinationRect;
            var navChanged = _lastNavRevision != navOverlay.Revision;

            if (sourceChanged || sourceRectChanged || destinationRectChanged || navChanged || sizeChanged || !IsPresenting)
            {
                Draw(sourceRect, destinationRect, navOverlay, width, height, renderScaling);
            }

            if (!IsPresenting)
            {
                _ = ShowWindow(_childHwnd, SW_SHOWNA);
                IsPresenting = true;

                PhotoTrace.Mark("native-hdr:presenting", metadata.FilePath,
                    $"hwnd=0x{_childHwnd:X}, swapchain={_pixelWidth}x{_pixelHeight}, "
                    + $"src={sourceRect}, dst={destinationRect}, dpi={renderScaling:0.###}");
            }

            _lastSourceRect = sourceRect;
            _lastDestinationRect = destinationRect;
            _hasLastSourceRect = true;
            _hasLastDestinationRect = true;
            _lastNavRevision = navOverlay.Revision;
            return true;
        }
        catch (Exception ex)
        {
            PhotoTrace.Mark("native-hdr:error", metadata.FilePath,
                $"{ex.GetType().Name}: {ex.Message}; {DescribeEligibility(metadata)}");

            System.Diagnostics.Debug.WriteLine(
                $"[NativeHDR] presentation failed: {ex.GetType().Name}: {ex.Message}");

            Hide();
            ReleaseGraphics();
            return false;
        }
    }


    public void Hide()
    {
        if (_childHwnd != IntPtr.Zero)
        {
            _ = ShowWindow(_childHwnd, SW_HIDE);
        }

        // Do not retain a full GPU copy of the previous photo while native presentation is hidden
        // (for example after navigating to SDR content). Re-uploading on the next activation is
        // cheaper than letting an arbitrarily large HDR bitmap linger in VRAM.
        _sourceBitmap?.Dispose();
        _sourceBitmap = null;
        _sourceTexture?.Dispose();
        _sourceTexture = null;
        _sourceIdentity = null;
        _hasLastSourceRect = false;
        _hasLastDestinationRect = false;
        _lastNavRevision = long.MinValue;

        IsPresenting = false;
    }


    protected override void OnDisposing()
    {
        base.OnDisposing();

        Hide();
        ReleaseGraphics();

        if (_childHwnd != IntPtr.Zero)
        {
            _ = DestroyWindow(_childHwnd);
            _childHwnd = IntPtr.Zero;
        }

        _window = null;
        _parentHwnd = IntPtr.Zero;
        IsInitialized = false;
    }


    private void EnsureChildWindow()
    {
        if (_childHwnd != IntPtr.Zero) return;
        if (_parentHwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Parent HWND is unavailable.");
        }

        var style = WS_CHILD | WS_DISABLED | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
        var exStyle = WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;

        PhotoTrace.Mark("native-hdr:hwnd-create", null,
            $"parent=0x{_parentHwnd:X}");

        _childHwnd = CreateWindowExW(
            exStyle,
            "STATIC",
            null,
            style,
            0,
            0,
            1,
            1,
            _parentHwnd,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_childHwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowExW failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }
    }


    private void CreateDeviceResources()
    {
        PhotoTrace.Mark("native-hdr:d3d-device", null, "creating hardware D3D11 device");

        _device = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            FeatureLevels);
        _d3dContext = _device.ImmediateContext;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        _dxgiFactory = adapter.GetParent<IDXGIFactory2>();

        _d2dFactory = D2D.D2D1CreateFactory<ID2D1Factory1>(
            FactoryType.MultiThreaded,
            DebugLevel.None);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _d2dContext.UnitMode = UnitMode.Pixels;

        PhotoTrace.Mark("native-hdr:d2d-format", null,
            $"format={Format.R16G16B16A16_Float}, supported={_d2dContext.IsDxgiFormatSupported(Format.R16G16B16A16_Float)}");
    }


    private void CreateSwapChain(int width, int height)
    {
        if (_device is null || _dxgiFactory is null || _d2dContext is null)
        {
            throw new InvalidOperationException("HDR device resources are unavailable.");
        }

        var description = new SwapChainDescription1(
            (uint)width,
            (uint)height,
            Format.R16G16B16A16_Float,
            stereo: false,
            Usage.RenderTargetOutput,
            bufferCount: 2,
            Scaling.Stretch,
            SwapEffect.FlipDiscard,
            DxgiAlphaMode.Ignore,
            SwapChainFlags.None);

        var fullscreen = new SwapChainFullscreenDescription
        {
            Windowed = true,
        };

        _swapChain = _dxgiFactory.CreateSwapChainForHwnd(
            _device,
            _childHwnd,
            description,
            fullscreen);

        _swapChain3 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain3>()
            ?? throw new NotSupportedException("IDXGISwapChain3 is unavailable.");

        var support = _swapChain3.CheckColorSpaceSupport(ColorSpaceType.RgbFullG10NoneP709);
        PhotoTrace.Mark("native-hdr:colorspace", null,
            $"requested={ColorSpaceType.RgbFullG10NoneP709}, support={support}");

        if ((support & SwapChainColorSpaceSupportFlags.Present) == 0)
        {
            throw new NotSupportedException("The current output cannot present FP16 scRGB.");
        }

        _swapChain3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
        PhotoTrace.Mark("native-hdr:colorspace-applied", null,
            $"{ColorSpaceType.RgbFullG10NoneP709}, format={Format.R16G16B16A16_Float}");

        _pixelWidth = width;
        _pixelHeight = height;
        CreateTargetBitmap();
    }


    private void ResizeSwapChain(int width, int height)
    {
        if (_swapChain is null) return;

        ReleaseTargetBitmap();

        _swapChain.ResizeBuffers(
            bufferCount: 2,
            width: (uint)width,
            height: (uint)height,
            newFormat: Format.R16G16B16A16_Float,
            swapChainFlags: SwapChainFlags.None).CheckError();

        _pixelWidth = width;
        _pixelHeight = height;
        CreateTargetBitmap();

        // Resize does not change the requested color space, but explicitly reassert it so a
        // driver cannot silently fall back to the default SDR color space.
        _swapChain3?.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
    }


    private void CreateTargetBitmap()
    {
        if (_swapChain is null || _d2dContext is null)
        {
            throw new InvalidOperationException("HDR swapchain resources are unavailable.");
        }

        using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
        var properties = new BitmapProperties1(
            new D2DPixelFormat(Format.R16G16B16A16_Float, D2DAlphaMode.Ignore),
            96.0f,
            96.0f,
            BitmapOptions.Target | BitmapOptions.CannotDraw);

        _targetBitmap = _d2dContext.CreateBitmapFromDxgiSurface(backBuffer, properties);
        _d2dContext.Target = _targetBitmap;
    }


    private void UploadSource(SKImage image)
    {
        if (_device is null || _d2dContext is null)
        {
            throw new InvalidOperationException("D3D11 / Direct2D device resources are unavailable.");
        }

        using var pixmap = image.PeekPixels();
        if (pixmap is null)
        {
            throw new NotSupportedException("Native HDR currently requires a CPU-backed SKImage.");
        }

        PhotoTrace.Mark("native-hdr:pixmap", null,
            $"{pixmap.Width}x{pixmap.Height}, colorType={pixmap.ColorType}, alpha={pixmap.AlphaType}, rowBytes={pixmap.RowBytes}, colorSpace={(pixmap.ColorSpace is null ? "none" : pixmap.ColorSpace.ToString())}");

        if (pixmap.ColorType is not (SKColorType.RgbaF16 or SKColorType.RgbaF16Clamped))
        {
            throw new NotSupportedException(
                $"Unsupported native HDR source format: {pixmap.ColorType}.");
        }

        // Do not use ID2D1DeviceContext.CreateBitmap(sourceData, ...) here. Although the device
        // context supports R16G16B16A16_FLOAT, some drivers reject CPU-initialized high-color
        // D2D bitmaps with E_INVALIDARG. Upload the exact FP16 bytes into a D3D11 texture first,
        // then share that texture with Direct2D through IDXGISurface. This is also the natural
        // path for future zero-copy codec output.
        _sourceBitmap?.Dispose();
        _sourceBitmap = null;
        _sourceTexture?.Dispose();
        _sourceTexture = null;

        var textureDesc = new Texture2DDescription(
            Format.R16G16B16A16_Float,
            checked((uint)image.Width),
            checked((uint)image.Height),
            arraySize: 1,
            mipLevels: 1,
            bindFlags: BindFlags.ShaderResource,
            usage: ResourceUsage.Default,
            cpuAccessFlags: CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            miscFlags: ResourceOptionFlags.None);

        var rowPitch = checked((uint)pixmap.RowBytes);
        var slicePitch = checked(rowPitch * (uint)image.Height);
        var initialData = new SubresourceData(pixmap.GetPixels(), rowPitch, slicePitch);

        _sourceTexture = _device.CreateTexture2D(textureDesc, initialData);

        using var sourceSurface = _sourceTexture.QueryInterface<IDXGISurface>();
        var properties = new BitmapProperties1(
            new D2DPixelFormat(Format.R16G16B16A16_Float, D2DAlphaMode.Ignore),
            96.0f,
            96.0f,
            BitmapOptions.None);

        _sourceBitmap = _d2dContext.CreateBitmapFromDxgiSurface(sourceSurface, properties);

        PhotoTrace.Mark("native-hdr:source-ready", null,
            $"d3d11Texture={image.Width}x{image.Height}/{Format.R16G16B16A16_Float}, rowPitch={rowPitch}");

        _sourceIdentity = image;
    }


    private void Draw(
        Avalonia.Rect sourceRect,
        Avalonia.Rect destinationRect,
        NativeHdrNavOverlayState navOverlay,
        int width,
        int height,
        double renderScaling)
    {
        if (_d2dContext is null || _sourceBitmap is null || _swapChain is null)
        {
            throw new InvalidOperationException("HDR drawing resources are unavailable.");
        }

        var dest = new RectangleF(0, 0, width, height);
        var src = new RectangleF(
            (float)sourceRect.X,
            (float)sourceRect.Y,
            (float)sourceRect.Width,
            (float)sourceRect.Height);

        PhotoTrace.Mark("native-hdr:draw-begin", null,
            $"target={width}x{height}, src={src}, dest={dest}, navRev={navOverlay.Revision}");

        _d2dContext.BeginDraw();
        _d2dContext.Transform = Matrix3x2.Identity;
        _d2dContext.Clear(new Color4(0, 0, 0, 1));

        ((ID2D1RenderTarget)_d2dContext).DrawBitmap(
            _sourceBitmap,
            new Vortice.Mathematics.Rect(
                dest.X,
                dest.Y,
                dest.Width,
                dest.Height),
            1.0f,
            D2DBitmapInterpolationMode.Linear,
            new Vortice.Mathematics.Rect(
                src.X,
                src.Y,
                src.Width,
                src.Height));

        DrawNavigationOverlay(destinationRect, navOverlay, renderScaling);

        PhotoTrace.Mark("native-hdr:draw-issued", null, "DrawBitmap queued");

        var endDraw = _d2dContext.EndDraw();
        PhotoTrace.Mark("native-hdr:draw-end", null, $"result={endDraw}");
        endDraw.CheckError();

        var present = _swapChain.Present(0, PresentFlags.None);
        PhotoTrace.Mark("native-hdr:present", null, $"result={present}");
        present.CheckError();
    }


    private void DrawNavigationOverlay(
        Avalonia.Rect destinationRect,
        NativeHdrNavOverlayState navOverlay,
        double renderScaling)
    {
        if (_d2dContext is null || !navOverlay.Enabled) return;

        DrawNavigationButton(
            navOverlay.LeftButtonRect,
            destinationRect,
            navOverlay.LeftProgress,
            navOverlay.LeftPressed,
            isLeft: true,
            renderScaling);

        DrawNavigationButton(
            navOverlay.RightButtonRect,
            destinationRect,
            navOverlay.RightProgress,
            navOverlay.RightPressed,
            isLeft: false,
            renderScaling);
    }


    private void DrawNavigationButton(
        Avalonia.Rect viewerButtonRect,
        Avalonia.Rect destinationRect,
        double progress,
        bool isPressed,
        bool isLeft,
        double renderScaling)
    {
        if (_d2dContext is null || progress <= 0) return;

        var opacity = Math.Clamp(progress, 0, 1);
        var slideLogical = 10.0 * (1.0 - opacity) * (isLeft ? 1.0 : -1.0);

        var x = (viewerButtonRect.X + slideLogical - destinationRect.X) * renderScaling;
        var y = (viewerButtonRect.Y - destinationRect.Y) * renderScaling;
        var w = viewerButtonRect.Width * renderScaling;
        var h = viewerButtonRect.Height * renderScaling;

        if (isPressed)
        {
            const double pressScale = 0.95;
            var cx = x + w / 2.0;
            var cy = y + h / 2.0;
            w *= pressScale;
            h *= pressScale;
            x = cx - w / 2.0;
            y = cy - h / 2.0;
        }

        var center = new Vector2(
            (float)(x + w / 2.0),
            (float)(y + h / 2.0));
        var ellipse = new Ellipse(center, (float)(w / 2.0), (float)(h / 2.0));

        using var baseBrush = _d2dContext.CreateSolidColorBrush(
            ToColor4(Core.Theme.BaseColor, 120, opacity));
        using var accentFillBrush = _d2dContext.CreateSolidColorBrush(
            ToColor4(Core.AccentColor, isPressed ? (byte)180 : (byte)120, opacity));
        using var accentStrokeBrush = _d2dContext.CreateSolidColorBrush(
            ToColor4(Core.AccentColor, 100, opacity));
        using var iconBrush = _d2dContext.CreateSolidColorBrush(
            ToColor4(Core.Theme.InvertedBaseColor, 255, opacity));

        _d2dContext.FillEllipse(ellipse, baseBrush);
        _d2dContext.FillEllipse(ellipse, accentFillBrush);
        _d2dContext.DrawEllipse(
            ellipse,
            accentStrokeBrush,
            (float)((isPressed ? 1.5 : 1.0) * renderScaling));

        // Theme nav icons are chevron-style arrows. Draw the same compact 50%-sized glyph
        // geometrically so the native HWND needs no separate SVG parser or input surface.
        var iconW = w * 0.5;
        var iconH = h * 0.5;
        var cx2 = x + w / 2.0;
        var cy2 = y + h / 2.0;
        var tipX = cx2 + (isLeft ? -iconW * 0.16 : iconW * 0.16);
        var tailX = cx2 + (isLeft ? iconW * 0.16 : -iconW * 0.16);
        var top = new Vector2((float)tailX, (float)(cy2 - iconH * 0.28));
        var tip = new Vector2((float)tipX, (float)cy2);
        var bottom = new Vector2((float)tailX, (float)(cy2 + iconH * 0.28));
        var iconStroke = (float)Math.Max(1.5, 2.4 * renderScaling);

        _d2dContext.DrawLine(top, tip, iconBrush, iconStroke);
        _d2dContext.DrawLine(tip, bottom, iconBrush, iconStroke);
    }


    private static Color4 ToColor4(Avalonia.Media.Color color, byte alpha, double opacity)
    {
        var a = (float)(alpha / 255.0 * Math.Clamp(opacity, 0, 1));
        return new Color4(
            color.R / 255.0f,
            color.G / 255.0f,
            color.B / 255.0f,
            a);
    }


    private void MoveChildWindow(int x, int y, int width, int height)
    {
        if (_childHwnd == IntPtr.Zero) return;
        if (_windowX == x && _windowY == y && _windowWidth == width && _windowHeight == height)
        {
            return;
        }

        if (SetWindowPos(
            _childHwnd,
            IntPtr.Zero,
            x,
            y,
            width,
            height,
            SWP_NOACTIVATE | SWP_NOOWNERZORDER) == 0)
        {
            throw new InvalidOperationException(
                $"SetWindowPos failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        _windowX = x;
        _windowY = y;
        _windowWidth = width;
        _windowHeight = height;
    }


    private void ReleaseTargetBitmap()
    {
        if (_d2dContext is not null)
        {
            _d2dContext.Target = null;
        }

        _targetBitmap?.Dispose();
        _targetBitmap = null;
    }


    private void ReleaseGraphics()
    {
        _sourceBitmap?.Dispose();
        _sourceBitmap = null;
        _sourceTexture?.Dispose();
        _sourceTexture = null;
        _sourceIdentity = null;
        _hasLastSourceRect = false;
        _hasLastDestinationRect = false;
        _lastNavRevision = long.MinValue;

        ReleaseTargetBitmap();

        _swapChain3?.Dispose();
        _swapChain3 = null;
        _swapChain?.Dispose();
        _swapChain = null;

        _d2dContext?.Dispose();
        _d2dContext = null;
        _d2dDevice?.Dispose();
        _d2dDevice = null;
        _d2dFactory?.Dispose();
        _d2dFactory = null;

        _d3dContext?.ClearState();
        _d3dContext?.Flush();
        _d3dContext?.Dispose();
        _d3dContext = null;
        _device?.Dispose();
        _device = null;

        _dxgiFactory?.Dispose();
        _dxgiFactory = null;

        _pixelWidth = 0;
        _pixelHeight = 0;
        _windowX = int.MinValue;
        _windowY = int.MinValue;
        _windowWidth = 0;
        _windowHeight = 0;
    }


    [LibraryImport(
        "user32.dll",
        EntryPoint = "CreateWindowExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);


    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int DestroyWindow(nint hWnd);


    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);


    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint hWnd, int nCmdShow);
}
