# Windows Native HDR Presentation

This branch introduces a Windows-only native HDR presentation path for static scRGB images while preserving the existing Avalonia renderer as an immediate fallback.

## Goal

Keep decoded linear scRGB values intact through presentation:

```text
HDR codec (RGBA16F scRGB)
    -> ImageGlass raw HDR frame
    -> D3D11 / Direct2D
    -> DXGI R16G16B16A16_FLOAT swapchain
    -> DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709
    -> DWM / Windows Advanced Color
    -> HDR display
```

This avoids the current ImageGlass HDR path:

```text
HDR source -> ImageGlass tone mapping -> SDR Avalonia surface
```

for sources that are already linear scRGB.

## Phase 1: scRGB passthrough

Implemented in this branch:

- Windows-only `INativeHdrPresenter` service
- `Win32NativeHdrPresenter` backed by D3D11, Direct2D and DXGI
- FP16 `R16G16B16A16_FLOAT` flip-model swapchain
- explicit `RgbFullG10NoneP709` swapchain color space
- raw pre-tone-map HDR frame retention in `ViewerControl`
- existing tone-mapped Avalonia image retained underneath as fail-safe fallback
- source-rect / destination-rect synchronization with ImageGlass zoom and pan
- display HDR state invalidates output-specific native resources
- native HDR disabled while selection UI is active to avoid HWND airspace hiding selection overlays
- manual-only NativeAOT Windows build workflow

Phase 1 deliberately accepts only `HdrTransferFunction.ScRgb`. PQ, HLG, gain-map and ambiguous linear-HDR inputs continue through the existing ImageGlass path.

## Known Phase 1 trade-offs

### HWND airspace

The HDR surface is a native child HWND. Native child windows compose above Avalonia content, so Avalonia overlays that intersect the image surface cannot appear above it.

The current implementation avoids the most disruptive case by disabling native HDR while selection UI is enabled. Navigation/message overlays can still be obscured where they intersect the image.

### Duplicate display copy

For reliability, ImageGlass retains:

1. the raw scRGB source for the native HDR presenter; and
2. the existing tone-mapped Avalonia image underneath.

This consumes more memory than the final design, but guarantees an immediate fallback if DirectX initialization, color-space negotiation or presentation fails.

### CPU-backed upload

Phase 1 consumes the CPU-backed `SKImage` produced by the codec and uploads it to a Direct2D bitmap. It does not yet share a GPU texture directly with the codec.

## Planned follow-up work

### Phase 2: presentation robustness

- replace the generic STATIC child HWND with a dedicated window class
- explicitly handle device-lost / display-change / DPI-change cases
- improve overlay coexistence or move overlays to the native composition path
- instrument active color space, swapchain format and presentation failures

### Phase 3: HDR transfer normalization

Convert additional HDR encodings into the same FP16 scRGB presentation space before presentation:

- PQ / HDR10 Rec.2020
- HLG Rec.2020
- linear wide-gamut EXR
- gain-map reconstruction

The native swapchain remains scRGB; only the source-to-scRGB stage changes.

### Phase 4: display-aware HDR policy

- query output luminance capabilities through DXGI output metadata
- establish consistent SDR/reference-white policy
- validate behavior across Windows HDR calibration, ACM and multi-monitor transitions
- compare numerically and visually against Microsoft Photos

### Phase 5: memory and upload optimization

After native presentation is stable:

- avoid producing the SDR tone-mapped fallback eagerly when native HDR is confirmed
- retain/recreate fallback lazily only after native failure
- accept shared D3D textures from future GPU-native codecs
- eliminate CPU RGBA16F -> Direct2D upload where possible

## Validation targets

Initial validation should use the same scRGB JXR in:

1. Microsoft Photos
2. ImageGlass with native HDR enabled
3. ImageGlass fallback HDR tone mapping

Compare:

- diffuse white
- highlight headroom
- saturated highlight colors
- shadow and midtone chroma
- clipping
- 1:1 pixel rendering
- scale-to-fit rendering
- monitor transitions with HDR enabled/disabled

The manual GitHub Actions workflow is intentionally `workflow_dispatch` only to avoid recurring CI cost.
