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
using ImageGlass.Common.ServiceProviders;
using ImageGlass.Common.Types;
using SharpGen.Runtime;
using SkiaSharp;
using System;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2D = Vortice.Direct2D1.D2D1;

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
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
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

    private SKImage? _sourceIdentity;
    private int _pixelWidth;
    private int _pixelHeight;
    private int _windowX = int.MinValue;
    private int _windowY = int.MinValue;
    private Avalonia.Rect _lastSourceRect;
    private bool _hasLastSourceRect;


    public bool IsInitialized { get; private set; }

    public bool IsPresenting { get; private set; }


    public void Initialize(Window window, bool displayHdrEnabled)
    {
        if (IsInitialized) return;

        _window = window;
        _parentHwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _displayHdrEnabled = displayHdrEnabled;
        IsInitialized = _parentHwnd != IntPtr.Zero;
    }


    public void OnDisplayChanged(bool displayHdrEnabled)
    {
        _displayHdrEnabled = displayHdrEnabled;

        // DXGI color-space support is output dependent. Tear down the presentation resources on
        // every monitor/Advanced-Color transition so the next frame renegotiates the swapchain.
        Hide();
        ReleaseGraphics();
    }


    public bool CanPresent(PhotoMetadata metadata)
    {
        return IsInitialized
            && _displayHdrEnabled
            && metadata.HdrTransferFn == HdrTransferFunction.ScRgb;
    }


    public bool TryPresent(
        Control host,
        SKImage image,
        PhotoMetadata metadata,
        Avalonia.Rect sourceRect,
        Avalonia.Rect destinationRect,
        double renderScaling)
    {
        if (!CanPresent(metadata)
            || image.IsDisposed
            || sourceRect.IsEmpty
            || destinationRect.IsEmpty
            || renderScaling <= 0
            || image.ColorType is not (SKColorType.RgbaF16 or SKColorType.RgbaF16Clamped))
        {
            Hide();
            return false;
        }

        if (_window is null
            || host.TranslatePoint(destinationRect.Position, _window) is not { } windowPoint)
        {
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
                UploadSource(image);
            }

            var sourceRectChanged = !_hasLastSourceRect || _lastSourceRect != sourceRect;
            if (sourceChanged || sourceRectChanged || sizeChanged || !IsPresenting)
            {
                Draw(sourceRect, width, height);
            }

            if (!IsPresenting)
            {
                _ = ShowWindow(_childHwnd, SW_SHOWNA);
                IsPresenting = true;
            }

            _lastSourceRect = sourceRect;
            _hasLastSourceRect = true;
            return true;
        }
        catch (Exception ex)
        {
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
        if (_parentHwnd == IntPtr.Zero) throw new InvalidOperationException("Parent HWND is unavailable.");

        var style = WS_CHILD | WS_VISIBLE | WS_DISABLED | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
        var exStyle = WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;

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

        _ = ShowWindow(_childHwnd, SW_HIDE);
    }


    private void CreateDeviceResources()
    {
        _device = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            FeatureLevels);
        _d3dContext = _device.ImmediateContext;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        _dxgiFactory = adapter.GetParent<IDXGIFactory2>();

        _d2dFactory = D2D.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded, DebugLevel.None);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _d2dContext.UnitMode = UnitMode.Pixels;
    }


    private void CreateSwapChain(int width, int height)
    {
        if (_device is null || _dxgiFactory is null || _d2dContext is null)
            throw new InvalidOperationException("HDR device resources are unavailable.");

        var description = new SwapChainDescription1(
            (uint)width,
            (uint)height,
            Format.R16G16B16A16_Float,
            stereo: false,
            Usage.RenderTargetOutput,
            bufferCount: 2,
            Scaling.Stretch,
            SwapEffect.FlipDiscard,
            AlphaMode.Ignore,
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
        if ((support & SwapChainColorSpaceSupportFlags.Present) == 0)
        {
            throw new NotSupportedException("The current output cannot present FP16 scRGB.");
        }

        _swapChain3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);

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
            throw new InvalidOperationException("HDR swapchain resources are unavailable.");

        using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
        var properties = new BitmapProperties1(
            new PixelFormat(Format.R16G16B16A16_Float, AlphaMode.Ignore),
            96.0f,
            96.0f,
            BitmapOptions.Target | BitmapOptions.CannotDraw);

        _targetBitmap = _d2dContext.CreateBitmapFromDxgiSurface(backBuffer, properties);
        _d2dContext.Target = _targetBitmap;
    }


    private void UploadSource(SKImage image)
    {
        if (_d2dContext is null)
            throw new InvalidOperationException("Direct2D device context is unavailable.");

        using var pixmap = image.PeekPixels();
        if (pixmap is null)
        {
            throw new NotSupportedException("Native HDR currently requires a CPU-backed SKImage.");
        }

        if (pixmap.ColorType is not (SKColorType.RgbaF16 or SKColorType.RgbaF16Clamped))
        {
            throw new NotSupportedException($"Unsupported native HDR source format: {pixmap.ColorType}.");
        }

        _sourceBitmap?.Dispose();

        var properties = new BitmapProperties1(
            new PixelFormat(Format.R16G16B16A16_Float, AlphaMode.Ignore),
            96.0f,
            96.0f,
            BitmapOptions.None);

        _sourceBitmap = _d2dContext.CreateBitmap(
            new SizeI(image.Width, image.Height),
            pixmap.GetPixels(),
            checked((uint)pixmap.RowBytes),
            properties);

        _sourceIdentity = image;
    }


    private void Draw(Avalonia.Rect sourceRect, int width, int height)
    {
        if (_d2dContext is null || _sourceBitmap is null || _swapChain is null)
            throw new InvalidOperationException("HDR drawing resources are unavailable.");

        var dest = new RectangleF(0, 0, width, height);
        var src = new RectangleF(
            (float)sourceRect.X,
            (float)sourceRect.Y,
            (float)sourceRect.Width,
            (float)sourceRect.Height);

        _d2dContext.BeginDraw();
        _d2dContext.Transform = Matrix3x2.Identity;
        _d2dContext.Clear(new Color4(0, 0, 0, 1));
        _d2dContext.DrawBitmap(
            _sourceBitmap,
            dest,
            1.0f,
            InterpolationMode.HighQualityCubic,
            src,
            Matrix4x4.Identity);
        _d2dContext.EndDraw().CheckError();

        // DWM performs composition; Present(0) avoids blocking the Avalonia UI thread on v-sync.
        _swapChain.Present(0, PresentFlags.None).CheckError();
    }


    private void MoveChildWindow(int x, int y, int width, int height)
    {
        if (_childHwnd == IntPtr.Zero) return;
        if (_windowX == x && _windowY == y && _pixelWidth == width && _pixelHeight == height) return;

        if (!SetWindowPos(
            _childHwnd,
            IntPtr.Zero,
            x,
            y,
            width,
            height,
            SWP_NOACTIVATE | SWP_NOOWNERZORDER))
        {
            throw new InvalidOperationException(
                $"SetWindowPos failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        _windowX = x;
        _windowY = y;
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
        _sourceIdentity = null;
        _hasLastSourceRect = false;

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);


    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
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
