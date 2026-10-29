# Wallpaper Engine Extraction and Translation

A portable **Windows and Linux** desktop tool for turning locally installed **Wallpaper Engine** wallpapers into static high-quality JPEGs, preparing images for a **manual** personal-use import back into Wallpaper Engine, and — if you ask it to — uploading wallpapers you own to the **Steam Workshop** through Valve's SteamCMD, **private by default**.

It can also look up the **original artwork** behind an extracted frame with reverse image search, so you can see the artist or source and, only if you confirm, replace a low-resolution candidate with a higher-resolution copy.

> Personal use only. Wallpaper Studio does not bypass DRM or protected formats and does not call undocumented Steam/Wallpaper Engine APIs. Workshop publishing is opt-in, uses Valve's documented `steamcmd +workshop_build_item`, uploads as **Private** by default, stores **no** Steam credentials, and reports success only when SteamCMD writes a real Workshop id back. Reverse image search is **off by default**, uploads only a 1280-pixel copy of the frame, and never downloads or replaces anything without your explicit confirmation.

![Wallpaper Studio](src/WallpaperStudio/Assets/app-icon.png)

Wallpaper Studio v2 ships two interchangeable front ends built on one shared core (`src/WallpaperStudio.Core`):

- **WPF head** (`src/WallpaperStudio`) — the native Windows build, with the original custom window chrome.
- **Avalonia head** (`src/WallpaperStudio.Avalonia`) — the cross-platform build used on Linux (and runnable on Windows). Same five screens, same acrylic look, same portable data and safety rules.

Both heads read the same `settings.json` and `state.json`, so a portable data folder can be moved between them.

## Download

Grab the latest build from the [**Releases**](../../releases) page:

- **`WallpaperStudio-win-x64.zip`** — self-contained 64-bit Windows build (WPF head). No .NET install required. Extract anywhere and run `WallpaperStudio.exe`.
- **`WallpaperStudio-linux-x64.tar.gz`** — self-contained 64-bit Linux build (Avalonia head). No .NET install required. Extract anywhere, run `chmod +x WallpaperStudio` if your archiver dropped the executable bit, then run `./WallpaperStudio`.
- FFmpeg is optional (video frames only) and is not bundled; see [Requirements](#requirements).
- The Windows zip also contains this README and `Unblock-WallpaperStudio.ps1`; the Linux tarball contains this README.

### The Windows "Windows protected your PC" prompt

The Windows build is unsigned, so the very first launch may show SmartScreen after you extract it from a downloaded zip. Choose **More info → Run anyway** once.

Windows normally repeats that prompt on every launch because the extracted `WallpaperStudio.exe` carries the "downloaded from the internet" mark (a `Zone.Identifier` data stream). Wallpaper Studio deletes that mark from its own executables at startup, so the prompt appears at most once per extracted copy. You can also clear it before the first launch from inside the extracted folder:

```powershell
.\Unblock-WallpaperStudio.ps1
```

or, without PowerShell, open the folder properties in Explorer and tick **Unblock**. A signed build would avoid the prompt entirely; no certificate is used for these personal-use releases.


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
- Extracts a **full-resolution static image from scene packages**: it follows the package's own `scene.json` → model → material → texture chain and decodes the referenced textures in-process (embedded JPEG/PNG, or raw/BC1/BC2/BC3/RGBA GPU data, including LZ4-compressed mips). Parent group transforms, rotations, scaled parallax backdrops, sprite sheets and additive light layers are applied, and you can choose how much of the scene is baked into the still (**Balanced**, **Everything**, **Background only**). A plain, validated JPEG/PNG preview inside the package is used as a clearly-labelled fallback only when extraction is not possible.
- Auto-approves clean sources for export: images, and scenes whose real image resolves to at least 1080p. An explicit review/approval step is required before exporting a **video**, a **sub-1080p scene**, or a scene frame that comes out **nearly all black**.
- Provides an import queue with a 1080p gate and a manual staging workflow.
- Finds the **original artwork** behind an extracted frame with optional reverse image search:
  - uploads a cleaned, **1280-pixel** JPEG copy of the frame to the providers you enable;
  - **trace.moe** identifies anime scenes and **IQDB** searches the major anime boorus (both keyless); **SauceNAO** finds Pixiv/booru originals and **Google Vision** (Web Detection) covers the rest;
  - shows result cards with a real resolution and a match score, highlights copies that are **larger** than the current candidate, and only downloads a full image after you confirm it — the download becomes a **pending** candidate, never an automatic replacement;
  - searches automatically after extraction (with an optional daily cap) or on demand with **Search again**;
  - caches results and thumbnails locally so badges survive a restart and repeated searches cost no quota.
- Offers 12 sampled frames for videos (and blank-looking frames) so you can pick the clearest moment to search from.
- Publishes wallpapers you choose to the Steam Workshop through Valve's SteamCMD:
  - uploads existing `project.json` project folders as-is, updating the item when the manifest already has a `workshopid`;
  - builds new **video** wallpapers from loose videos;
  - builds new **scene** wallpapers from static images using a generated texture (**experimental**);
  - uploads **Private** by default, so items are visible only to your account;
  - never stores Steam credentials and never reports a publish it did not verify.
- Keeps your settings and approval memory in your user profile, so installing a new version never loses them. Drop a `portable.txt` file beside the executable to keep everything next to the app instead.

## Requirements

- **Windows 10 or Windows 11, x64** (WPF head), or a **64-bit Linux desktop** (Avalonia head) with X11 or Wayland and the usual GTK/ICU libraries. The Linux build is self-contained and needs glibc 2.17 or newer.
- .NET SDK 8.0 or newer to build
- FFmpeg **optional**, required only for video frame extraction
  - Windows: place `ffmpeg.exe`/`ffprobe.exe` in an `ffmpeg` folder next to the app, add them to `PATH`, or choose the folder in Settings.
  - Linux: install your distribution's `ffmpeg` package, put `ffmpeg`/`ffprobe` in an `ffmpeg` folder next to the app, or choose the folder in Settings.
  - FFmpeg is licensed separately from this project and is not bundled.
- SteamCMD **optional**, required only for Workshop publishing
  - Windows uses `steamcmd.exe`; Linux uses `steamcmd.sh`. Install it from your distribution, place it in a `steamcmd` folder next to the app, add it to `PATH`, or choose it in Settings.
  - Sign in once inside Valve's own SteamCMD window. On Linux the sign-in opens in a terminal emulator (`x-terminal-emulator`, GNOME Terminal, Konsole, Xfce Terminal or xterm). Wallpaper Studio never sees or stores your password or Steam Guard code.
  - SteamCMD and Steam are Valve's tools, distributed by Valve under Valve's terms; they are not bundled or downloaded by this app.
- **Wallpaper Engine on Linux** runs as a Proton-hosted Windows application installed by Steam. Wallpaper Studio reads its files from the Steam library (including secondary libraries listed in `libraryfolders.vdf`) and launches it with `steam -applaunch 431960`.
- Reverse image search **optional** and **off by default**
  - **trace.moe** and **IQDB** need no key. **SauceNAO** and **Google Vision** need a free API key of your own, pasted into Settings.
  - Keys are stored as plain text in `settings.json` on your PC. Free tiers are roughly 150 searches/month for SauceNAO and 1,000/month for the Vision Web Detection feature.
  - The app uploads only the extracted frame, downscaled to a 1280-pixel JPEG. No filenames, paths, library contents or account details are sent.
  - Automatic searches are capped at 40 per day by default. That cap only protects the free monthly quotas above; **Search again** is never blocked, cached results never count, and the cap can be switched off entirely in Settings.

## Build

```bash
dotnet restore WallpaperStudio.sln
dotnet build WallpaperStudio.sln -c Release
```

The solution builds both heads: the WPF app on Windows only, and the Avalonia app everywhere. Run from source:

```bash
# Windows WPF head
dotnet run --project src/WallpaperStudio/WallpaperStudio.csproj

# Cross-platform Avalonia head (Windows, Linux, macOS)
dotnet run --project src/WallpaperStudio.Avalonia/WallpaperStudio.Avalonia.csproj
```

## Publish a portable build

Windows (WPF head):

```powershell
dotnet publish src/WallpaperStudio/WallpaperStudio.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

The publish folder contains `WallpaperStudio.exe`. Data is stored in your user profile by default (see [Data and state](#data-and-state)); add a `portable.txt` file beside the executable if you want `WallpaperStudioData/` and `Exports/` in the app folder instead. SkiaSharp's native assets are included automatically; use `IncludeNativeLibrariesForSelfExtract` for single-file publishing.

Linux (Avalonia head) — the checked-in publish profile produces a single self-contained executable:

```bash
dotnet publish src/WallpaperStudio.Avalonia/WallpaperStudio.Avalonia.csproj -p:PublishProfile=linux-x64
chmod +x publish/linux-x64/WallpaperStudio
tar -czf WallpaperStudio-linux-x64.tar.gz -C publish/linux-x64 WallpaperStudio
```

The profile sets `linux-x64`, self-contained, single-file, compression and no debug symbols. To target a different architecture, add `-r linux-arm64` and a matching profile.

Build the Avalonia head for Windows with the same command and `-r win-x64` if you want to try the cross-platform UI there.

## Usage

1. Open the **Library** page and choose your source folder. Typical locations:
   - Windows: `...\Steam\steamapps\workshop\content\431960`, `...\Steam\steamapps\common\wallpaper_engine\projects\myprojects`
   - Linux: `~/.steam/steam/steamapps/workshop/content/431960`, `~/.local/share/Steam/steamapps/workshop/content/431960`, or the same path inside any library listed in `libraryfolders.vdf` (including the Flatpak Steam location)
   - any folder containing isolated videos/images/projects
2. Select **Scan library**. Detection is read-only.
3. For images, press **Export JPEG**. The magnifier button on the card opens the same frame review without an approval step, which is where reverse search results appear.
4. For videos and scenes, press **Review frame**:
   - videos use FFmpeg to render the chosen timestamp;
   - scenes are decoded to their **full resolution** from the package's own textures; use **Re-extract scene image** to refresh one;
   - for scenes, **Frame composition** chooses how much of the layer stack is baked into the still: **Balanced** (background plus props at normal size), **Everything** (also stretched gradients and effect layers) or **Background only**. The choice is remembered per source, even for auto-approved scenes;
   - if extraction is not possible, the project preview, a sibling `preview.*`, or a validated embedded preview is used and labelled as a fallback;
   - scenes that resolve to at least 1080p are auto-approved by default (toggle **Auto-approve extracted scene frames** in Settings). Sub-1080p scenes, videos, all-black frames and low-resolution fallbacks still need **Approve this frame** before export;
   - **Sample frames** renders twelve timestamps when a video opens on a fade, logo or black screen, and preselects the clearest one.
5. Use the **Import queue** to prepare images for Wallpaper Engine:
   - add images or a whole folder;
   - the 1080p rule requires a long edge of at least 1920 **and** a short edge of at least 1080;
   - low-resolution images must be explicitly approved or tossed;
   - **Stage approved** copies files to a normal folder and writes an instructions file.
6. Import the staged files manually in Wallpaper Engine. The app can optionally launch Wallpaper Engine for you, but it does not automate the import.

### Find the original artwork (optional, off by default)

1. Turn on **Enable reverse image search** in Settings and confirm the one-time consent prompt. Both the automatic and the manual search wait for that consent.
2. Enable at least one provider: **trace.moe** and **IQDB** work without a key; add a **SauceNAO** and/or **Google Vision** key for general artwork.
3. Open **Frame review** for a video or scene, or use the magnifier button on an image card. A search runs automatically after the candidate frame is ready; **Search again** repeats it on demand.
4. Read the result cards:
   - **IDENTIFICATION** means the source was named (anime, episode, timestamp) but no image can be downloaded — trace.moe never returns the picture itself;
   - **HIGHER RES** marks a copy that is genuinely bigger than your current frame;
   - the confidence score and the real pixel size come from the provider plus a small enrichment pass against public booru APIs.
5. **Use image** downloads that one result. It must decode, pass the 1080p rule, and not be all black; otherwise it is rejected with an explanation. The download becomes a **pending** candidate — approve it explicitly in Frame review.
6. Use **Sample frames** when the opening second is a logo, fade or black screen. Twelve frames are rendered, the clearest one is preselected, and clicking a frame re-extracts at that timestamp and searches again.

**Why is there a daily limit?** SauceNAO's free key covers roughly 150 calls a month and Google's free Vision tier about 1,000, so a first scan of a 400-wallpaper library would use them up in one go. Only *automatic* background searches count: each frame is searched once, a cached result costs nothing, and pressing **Search again** is never blocked or counted. Turn off **Cap automatic searches per day** in Settings to remove the cap completely.

IQDB is keyless but rate-limited by the site itself, so it is queried one image at a time and can take 20-45 seconds to answer. It only reports a booru page and a similarity score, never the original file; the page link is always shown, and a direct download is offered only when the booru's own public API supplies one. IQDB is best at near-exact copies (95-99%), which is exactly the case where a higher-resolution original exists.

### Publish to the Steam Workshop (optional)

1. Open the **Publish to Workshop** page.
2. Add what you want to upload:
   - **Add project folder…** — an existing folder containing `project.json` is uploaded as-is. If its manifest has a `workshopid`, the existing item is updated.
   - **Add video…** — a loose video becomes a new `video` wallpaper project.
   - **Add image…** — a static image becomes a new `scene` wallpaper project using a generated texture (**experimental** — verify it in Wallpaper Engine first).
3. For new items, press **Build** to generate the project folder under the app's `publish` data folder. Review it before uploading.
4. Point Settings (or the card on the page) at `steamcmd.exe` (Windows) or `steamcmd.sh` (Linux), then press **Sign in to SteamCMD** once and sign in inside Valve's window.
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

Settings, approval memory, logs, caches and drafts live in a stable per-user folder, so extracting a new version somewhere else never loses them:

```text
%LOCALAPPDATA%\WallpaperStudio\        (Windows)
~/.local/share/WallpaperStudio/       (Linux, or $XDG_DATA_HOME/WallpaperStudio)
  settings.json
  state.json
  logs\
  cache\thumbs\
  cache\frames\
  cache\reverse\                  query JPEGs, result JSON, provider thumbnails, daily usage
  publish\                       built project folders + Workshop .vdf scripts
```

Exports default to `%USERPROFILE%\Pictures\Wallpaper Studio Exports` on Windows and `~/Pictures/Wallpaper Studio Exports` on Linux.

**Portable mode (opt-in).** Create an empty file named `portable.txt` next to `WallpaperStudio.exe`. Data then lives in `<app>\WallpaperStudioData\` and exports in `<app>\Exports\`, and nothing is written to your profile. If the app folder is read-only, the per-user folder is used instead.

**Upgrades.** On first start, if a `WallpaperStudioData\` folder from an older build sits beside the executable — or in a neighbouring extracted build such as `Downloads\WallpaperStudio-win-x64\` — and the per-user folder has no settings yet, the app imports `settings.json`, `state.json`, `publish\` and the cache automatically. The old folder is left untouched and marked with `migrated-to-user-data.txt`, so it can never overwrite newer choices later. Both heads read the same files, so the portable data folder can move between Windows and Linux.

Writes are atomic and corrupt state files are archived instead of being overwritten silently.

`state.json` remembers approvals, tosses, exports and source fingerprints, so unchanged sources can be skipped. Re-approving a previously exported item clears its export status so the next scan offers it again. **Re-export everything** overrides the skip logic for one batch. It also remembers the Workshop ids of items you published, so a re-publish updates the same item.

## Safety and limitations

- **Scene extraction is best-effort, not a renderer.** Static images are decoded from the package's own asset chain (scene → model → material → texture). Objects are placed with their scene origin, scale and Z rotation, combined with their parent groups, and sprite sheets/padded textures are cropped to one visible frame. Effects, shaders, particles and animation are not simulated, and values that only exist at runtime (a script or user property that moves or hides a layer) use the defaults stored in the package, so the result is a faithful but simplified still. **Frame composition** lets you choose how much of the layer stack is included. Every extracted frame is validated, including an all-black check, and can be re-reviewed with **Re-extract scene image**. When a scene is just a puppet/model or animated overlay over black, the app recovers the largest usable picture in the package instead of showing a black frame.
- **No DRM or protection bypass.** Unsupported or protected packages are marked unsupported.
- **No Steam credentials** are ever requested, read or stored. Sign-in happens inside Valve's own SteamCMD window; Wallpaper Studio launches that window and never sees your password or Steam Guard code.
- **Publishing is explicit and opt-in.** Nothing is uploaded until you press **Publish**. New items upload as **Private** by default and are visible only to your account.
- **No faked successes.** An item is marked Published only when SteamCMD writes a new, non-zero `publishedfileid` back into its script; SteamCMD's own output and any errors are shown verbatim.
- **Image → scene generation is experimental.** It writes a minimal, unencrypted scene using a texture container reconstructed from a public open-source reader, not an official specification. Open the built folder in Wallpaper Engine and confirm it renders before sharing.
- **Composition is approximate.** A scene is flattened by stacking its image layers bottom-to-top on black at the scene's own projection resolution (for example 3840×2160). Object transforms are composed with their parent groups, sprite sheets are cropped to a single cell, padded textures to their real picture, and additive light layers (`colorBlendMode = 31` or black-backed sheets) are added instead of pasted. **Frame composition** controls whether props, stretched effect gradients and the background are included. Puppet/model meshes, particles, shaders, script-driven animation and per-user property variants are not reconstructed, so those elements may be missing or approximate: the export is one still moment, not the animation.
- **Reverse image search is opt-in and bounded.** It stays off until you enable it and confirm the consent prompt. Only a 1280-pixel JPEG of the extracted frame is uploaded, and only to the providers you enable. Nothing is downloaded or published automatically: a downloaded original always lands as a **pending** candidate that you approve yourself.
- **Results can be wrong or incomplete.** trace.moe only identifies anime scenes and cannot return the artwork; IQDB only reports a booru page and a similarity score; SauceNAO and Google Vision depend on their own indexes and free quotas. A high score is a lead, not proof of authorship. Check the source before reusing anything.
- **API keys are stored in plain text** in `settings.json` in the data folder. Use free, revocable keys and clear them if you share the folder.
- Video frames depend on the installed FFmpeg build and its codec support.
- Wallpaper Engine import remains a manual workflow because the application exposes no documented CLI/import API. Uploading is likewise left to Valve's SteamCMD rather than any invented API.
- Respect creators' rights. Do not redistribute or publish content you do not own or license.

## Project structure

```text
src/WallpaperStudio.Core/          Shared, UI-neutral code (net8.0 + net8.0-windows)
  Models/                          Sources, settings, state, scene composition, import/publish queue models
  Services/
    SourceScanner.cs               Recursive detection of projects/videos/images/pkg
    ProjectManifestReader.cs       Safe top-level project.json hints
    PkgPreviewExtractor.cs         Conservative embedded JPEG/PNG scan
    SceneFrameExtractor.cs         Full-resolution scene → image extraction
    PkgArchive.cs                  Read-only PKGV package index reader
    TexDecoder.cs                  Read-only TEXB texture header/decoder
    BlockTextureDecoder.cs         BC1/BC2/BC3/RGBA8/RG8/R8 → RGBA
    Lz4BlockDecoder.cs             LZ4 block decompression for texture mips
    FrameAnalyzer.cs               All-black frame detection and frame quality scoring
    FfmpegService.cs               Locate FFmpeg, probe media, extract frames and filmstrips
    ImageService.cs                SkiaSharp probing and thumbnail cache
    SkiaImage.cs                   Decode, EXIF orientation, resize, JPEG encode
    ExportService.cs               Approved-frame JPEG export and state updates
    ImportService.cs               Manual import staging and instructions
    SteamCmdService.cs             Locate/sign in/run Valve's SteamCMD honestly
    WorkshopVdfWriter.cs           Build/parse the workshop_build_item VDF
    WallpaperProjectBuilder.cs     Build new project folders from images/videos
    TexWriter.cs                   Minimal TEXB0003 texture container (experimental)
    StateStore.cs                  Portable, atomic library and publish memory
    SettingsService.cs             Portable settings
    WallpaperEngineLocator.cs      Steam/Proton discovery and Linux launch through Steam
    DialogService.cs               IDialogService contract
    ReverseSearch/                 Provider clients, ranking, cache and downloads
  ViewModels/                      Library, review, import, publish, settings, shell
  Infrastructure/                  Data paths + legacy import, MOTW cleanup, shell, logging, MVVM, UI thread

src/WallpaperStudio/               WPF head (Windows)
  App.xaml, App.xaml.cs            Composition and global error handling
  MainWindow.xaml                  Custom chrome, navigation rail, status bar
  Themes/Theme.xaml                Dark glass theme and control styles
  Views/                           Five pages plus the low-resolution dialog
  Services/WpfDialogService.cs     Native pickers, dialogs and clipboard
  Infrastructure/                  Converters and DWM acrylic

src/WallpaperStudio.Avalonia/      Avalonia head (Windows + Linux)
  Program.cs, App.axaml(.cs)       Composition, services and DataTemplates
  MainWindow.axaml                 Custom chrome, navigation rail, status bar
  Themes/Theme.axaml               Shared palette, templates and control styles
  Views/                           Five pages plus dialogs (Material icons)
  Services/AvaloniaDialogService.cs StorageProvider pickers, async dialogs, shell
  Converters/AppConverters.cs      Thumbnail, tone and visibility converters
  Properties/PublishProfiles/      linux-x64 self-contained publish profile

scripts/Unblock-WallpaperStudio.ps1  Clears the Windows download mark from an extracted build
```

## Third-party components

- **SkiaSharp** — MIT license. Used for image decode, orientation, resizing and JPEG encoding.
- **Avalonia** — MIT license. Cross-platform UI toolkit used by the Linux head.
- **Material.Icons.Avalonia** — MIT license. Icon set used by the Avalonia UI.
- **FFmpeg** — separate project, typically LGPL/GPL depending on the build. Not bundled or downloaded by this app. If you redistribute FFmpeg, include its license and source offer.
- **SteamCMD** — Valve's command-line Steam client. Distributed by Valve under Steam's terms; not bundled or downloaded by this app. Used only when you publish.
- **Reverse image search providers** — traffic goes to [trace.moe](https://trace.moe), [IQDB](https://iqdb.org), [SauceNAO](https://saucenao.com) and, if you add a key, Google Cloud Vision, each under its own terms and privacy policy. Optional size/URL enrichment uses the public Danbooru, Yande.re and Konachan JSON APIs. No key, image or result is shared with this project; all requests come from your own machine and your own quotas.

Wallpaper Engine and Steam are trademarks of their respective owners. This project is not affiliated with or endorsed by them.
