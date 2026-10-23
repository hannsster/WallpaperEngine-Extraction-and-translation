# Wallpaper Engine Extraction and Translation

A portable Windows desktop tool for turning locally installed **Wallpaper Engine** wallpapers into static high-quality JPEGs, preparing images for a **manual** personal-use import back into Wallpaper Engine, and — if you ask it to — uploading wallpapers you own to the **Steam Workshop** through Valve's SteamCMD, **private by default**.

> Personal use only. Wallpaper Studio does not bypass DRM or protected formats and does not call undocumented Steam/Wallpaper Engine APIs. Workshop publishing is opt-in, uses Valve's documented `steamcmd +workshop_build_item`, uploads as **Private** by default, stores **no** Steam credentials, and reports success only when SteamCMD writes a real Workshop id back.

![Wallpaper Studio](src/WallpaperStudio/Assets/app-icon.png)

## Download

Grab the latest portable build from the [**Releases**](../../releases) page:

- **`WallpaperStudio-win-x64.zip`** — self-contained 64-bit Windows build. No .NET install required. Extract anywhere and run `WallpaperStudio.exe`.
- FFmpeg is optional (video frames only) and is not bundled; see [Requirements](#requirements).


## What it does

- Recursively scans a folder of Wallpaper Engine content:
  - workshop project folders containing `project.json`
  - standalone `project.json` folders
  - `.pkg` scene packages
  - loose videos (`.mp4`, `.webm`, `.mov`, …)
  - loose images (`.jpg`, `.png`, `.webp`, `.bmp`, …)
- Builds a thumbnail library with deduplication and persistent state.
- Exports approved sources as JPEG at a configurable quality (default 90).
- Extracts a frame from a video with FFmpeg at a chosen timestamp.
- Finds a **plain, validated JPEG/PNG preview** inside a scene package when no sibling preview exists. This is a conservative signature scan, not scene decompilation.
- Requires an explicit review/approval step before any video or scene is exported.
- Provides an import queue with a 1080p gate and a manual staging workflow.
- Publishes wallpapers you choose to the Steam Workshop through Valve's SteamCMD:
  - uploads existing `project.json` project folders as-is, updating the item when the manifest already has a `workshopid`;
  - builds new **video** wallpapers from loose videos;
  - builds new **scene** wallpapers from static images using a generated texture (**experimental**);
  - uploads **Private** by default, so items are visible only to your account;
  - never stores Steam credentials and never reports a publish it did not verify.
- Works portably: settings, state, logs and caches live beside the executable when that folder is writable, with a `%LOCALAPPDATA%` fallback.

## Requirements

- Windows 10 or Windows 11, x64
- .NET SDK 8.0 or newer to build
- FFmpeg **optional**, required only for video frame extraction
  - Place `ffmpeg.exe`/`ffprobe.exe` in an `ffmpeg` folder next to the app, add them to `PATH`, or choose the folder in Settings.
  - FFmpeg is licensed separately from this project and is not bundled.
- SteamCMD **optional**, required only for Workshop publishing
  - Download it from Valve, place `steamcmd.exe` in a `steamcmd` folder next to the app, add it to `PATH`, or choose it in Settings.
  - Sign in once inside Valve's own SteamCMD window. Wallpaper Studio never sees or stores your password or Steam Guard code.
  - SteamCMD and Steam are Valve's tools, distributed by Valve under Valve's terms; they are not bundled or downloaded by this app.

## Build

```powershell
dotnet restore WallpaperStudio.sln
dotnet build WallpaperStudio.sln -c Release
```

Run from source:

```powershell
dotnet run --project src/WallpaperStudio/WallpaperStudio.csproj
```

## Publish a portable build

```powershell
dotnet publish src/WallpaperStudio/WallpaperStudio.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

The publish folder contains `WallpaperStudio.exe`. Keep `WallpaperStudioData/` and `Exports/` beside it if you want the portable layout. SkiaSharp's native assets are included automatically; use `IncludeNativeLibrariesForSelfExtract` for single-file publishing.

## Usage

1. Open the **Library** page and choose your source folder. Typical locations:
   - `...\Steam\steamapps\workshop\content\431960`
   - `...\Steam\steamapps\common\wallpaper_engine\projects\myprojects`
   - any folder containing isolated videos/images/projects
2. Select **Scan library**. Detection is read-only.
3. For images, press **Export JPEG**.
4. For videos and scenes, press **Review frame**:
   - videos use FFmpeg to render the chosen timestamp;
   - scenes use the project preview, a sibling `preview.*`, or a validated embedded preview;
   - the candidate is labelled as a fallback when applicable;
   - **Approve this frame** before export.
5. Use the **Import queue** to prepare images for Wallpaper Engine:
   - add images or a whole folder;
   - the 1080p rule requires a long edge of at least 1920 **and** a short edge of at least 1080;
   - low-resolution images must be explicitly approved or tossed;
   - **Stage approved** copies files to a normal folder and writes an instructions file.
6. Import the staged files manually in Wallpaper Engine. The app can optionally launch Wallpaper Engine for you, but it does not automate the import.

### Publish to the Steam Workshop (optional)

1. Open the **Publish to Workshop** page.
2. Add what you want to upload:
   - **Add project folder…** — an existing folder containing `project.json` is uploaded as-is. If its manifest has a `workshopid`, the existing item is updated.
   - **Add video…** — a loose video becomes a new `video` wallpaper project.
   - **Add image…** — a static image becomes a new `scene` wallpaper project using a generated texture (**experimental** — verify it in Wallpaper Engine first).
3. For new items, press **Build** to generate the project folder under the app's `publish` data folder. Review it before uploading.
4. Point Settings (or the card on the page) at `steamcmd.exe`, then press **Sign in to SteamCMD** once and sign in inside Valve's window.
5. Set a **Visibility** per item — new items default to **Private (hidden)**.
6. Press **Publish** (or **Publish all**). The page streams SteamCMD's own output so you can see exactly what happened.

Wallpaper Studio only marks an item **Published** when SteamCMD writes a new, non-zero `publishedfileid` back into the item script. It never invents a success result. You can change visibility later in Wallpaper Engine or on the item's Workshop page.

## Resolution rule

An image passes when:

```text
max(width, height) >= 1920  AND  min(width, height) >= 1080
```

Portrait 1080×1920 passes. 1280×720 and 1080×1080 do not. Low-resolution images are never upscaled or silently accepted.

## Data and state

When the executable directory is writable:

```text
<app>\WallpaperStudioData\
  settings.json
  state.json
  logs\
  cache\thumbs\
  cache\frames\
  publish\                       built project folders + Workshop .vdf scripts
<app>\Exports\
```

Otherwise the same structure is created under `%LOCALAPPDATA%\WallpaperStudio`. Writes are atomic and corrupt state files are archived instead of being overwritten silently.

`state.json` remembers approvals, tosses, exports and source fingerprints, so unchanged sources can be skipped. Re-approving a previously exported item clears its export status so the next scan offers it again. **Re-export everything** overrides the skip logic for one batch. It also remembers the Workshop ids of items you published, so a re-publish updates the same item.

## Safety and limitations

- **No scene rendering.** Wallpaper Engine scene formats are not publicly documented. For protected packages the app offers the project preview or a plain embedded image only.
- **No DRM or protection bypass.** Unsupported or protected packages are marked unsupported.
- **No Steam credentials** are ever requested, read or stored. Sign-in happens inside Valve's own SteamCMD window; Wallpaper Studio launches that window and never sees your password or Steam Guard code.
- **Publishing is explicit and opt-in.** Nothing is uploaded until you press **Publish**. New items upload as **Private** by default and are visible only to your account.
- **No faked successes.** An item is marked Published only when SteamCMD writes a new, non-zero `publishedfileid` back into its script; SteamCMD's own output and any errors are shown verbatim.
- **Image → scene generation is experimental.** It writes a minimal, unencrypted scene using a texture container reconstructed from a public open-source reader, not an official specification. Open the built folder in Wallpaper Engine and confirm it renders before sharing.
- **No guarantee of preview quality.** A scene's preview may be a static thumbnail rather than a full-resolution frame. The UI labels these candidates.
- Video frames depend on the installed FFmpeg build and its codec support.
- Wallpaper Engine import remains a manual workflow because the application exposes no documented CLI/import API. Uploading is likewise left to Valve's SteamCMD rather than any invented API.
- Respect creators' rights. Do not redistribute or publish content you do not own or license.

## Project structure

```text
src/WallpaperStudio/
  App.xaml, App.xaml.cs          Startup composition and global error handling
  MainWindow.xaml                Custom chrome, navigation rail, status bar
  Themes/Theme.xaml              Dark glass theme and control styles
  Models/                        Sources, settings, state, import queue models
  Services/
    SourceScanner.cs             Recursive detection of projects/videos/images/pkg
    ProjectManifestReader.cs     Safe top-level project.json hints
    PreviewResolver.cs           Sibling/embedded preview discovery
    PkgPreviewExtractor.cs       Conservative embedded JPEG/PNG scan
    FfmpegService.cs             Locate FFmpeg, probe media, extract frames
    ImageService.cs              SkiaSharp probing and thumbnail cache
    SkiaImage.cs                 Decode, EXIF orientation, resize, JPEG encode
    ExportService.cs             Approved-frame JPEG export and state updates
    ImportService.cs             Manual import staging and instructions
    SteamCmdService.cs           Locate/sign in/run Valve's SteamCMD honestly
    WorkshopVdfWriter.cs         Build/parse the workshop_build_item VDF
    WallpaperProjectBuilder.cs   Build new project folders from images/videos
    TexWriter.cs                 Minimal TEXB0003 texture container (experimental)
    StateStore.cs                Portable, atomic library and publish memory
    SettingsService.cs           Portable settings
    WallpaperEngineLocator.cs    Read-only installation discovery
  ViewModels/                    Library, review, import, publish, settings, shell
  Views/                         Five pages plus the low-resolution dialog
  Infrastructure/                Paths, logging, MVVM primitives, converters, DWM
```

## Third-party components

- **SkiaSharp** — MIT license. Used for image decode, orientation, resizing and JPEG encoding.
- **FFmpeg** — separate project, typically LGPL/GPL depending on the build. Not bundled or downloaded by this app. If you redistribute FFmpeg, include its license and source offer.
- **SteamCMD** — Valve's command-line Steam client. Distributed by Valve under Steam's terms; not bundled or downloaded by this app. Used only when you publish.

Wallpaper Engine and Steam are trademarks of their respective owners. This project is not affiliated with or endorsed by them.
