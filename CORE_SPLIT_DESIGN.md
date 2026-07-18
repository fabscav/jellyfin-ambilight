# Core / Adapter Split - Design Document

**Status**: Proposal — not yet implemented
**Goal**: Make the plugin's general functionality developable and testable without a running Jellyfin server.

---

## 🎯 Motivation

Today every change to extraction, colour processing, LED geometry or WLED streaming has to be validated by building a DLL, restarting a Jellyfin server, and playing a video at real hardware. Jellyfin has no plugin hot-reload, so the iteration loop is ~30 seconds at best, and much of the behaviour (frame timing, LED ordering, colour output) can only be judged by eye.

This is disproportionate, because **most of the plugin isn't about Jellyfin at all**. The AMb2 format, ffmpeg extraction, LED zone geometry, the colour pipeline, and UDP streaming are general-purpose. Jellyfin only answers two questions: *which media exists* and *when is something playing*.

Separating those concerns gives us a fast test loop, a usable standalone CLI, and a codebase where contributors don't need a media server to contribute. It also shrinks the surface affected by Jellyfin version upgrades to a single thin adapter.

---

## 📏 Current State (measured)

Jellyfin/ASP.NET imports per file:

| File | Lines | Jellyfin imports | Destination |
|---|---:|---:|---|
| `Api/AmbilightController.cs` | 508 | 5 | adapter |
| `Server/AmbilightEntryPoint.cs` | 410 | 5 | adapter |
| `Plugin.cs` | 50 | 4 | adapter |
| `Tasks/ExtractPendingAmbilightTask.cs` | 129 | 2 | adapter |
| `Server/AmbilightServiceRegistrator.cs` | 26 | 2 | adapter |
| `PluginConfiguration.cs` | 97 | 1 | adapter (+ mapper) |
| `Services/AmbilightPlaybackService.cs` | 415 | 3 | **straddles** |
| `Services/AmbilightExtractorService.cs` | 430 | 3 | **straddles** |
| `Services/EmbeddedBinariesResolver.cs` | 189 | 1 | dead — delete |
| **`Services/AmbilightInProcessPlayer.cs`** | 776 | **0** | core |
| **`Services/AmbilightInProcessExtractor.cs`** | 727 | **0** | core |
| **`Services/AmbilightStorageService.cs`** | 284 | **0** | core |

**1,787 lines across three files already have zero Jellyfin imports** — and they are precisely the domain: format, extraction, colour, streaming, storage. Of the remaining ~2,250, most is legitimately adapter code that *should* stay coupled (HTTP controller, hosted service, plugin shell). Only two services genuinely straddle the boundary, and both split cleanly along an identifiable seam (see below).

Two things prevent the core three from compiling standalone:

1. **`PluginConfiguration : BasePluginConfiguration`** — a Jellyfin base class, and it is the object every core service receives.
2. **18 `Plugin.Instance?.Configuration` reads across 9 files** — a static reach-back into the plugin host, present even in the three otherwise-clean files.

Remove those two and the core is genuinely portable. This project *began* as a standalone daemon (see `old-daemon-approach/`), so this is closer to restoring a lost capability than inventing a new one.

---

## ✅ Goals / ❌ Non-Goals

**Goals**

- Core functionality builds and tests with **no Jellyfin packages referenced**
- Fast unit tests over format, geometry, colour and device-matching logic
- A CLI that can extract, inspect, render and play `.bin` files standalone
- A virtual WLED target so LED behaviour is observable without hardware
- No behavioural change for existing users

**Non-Goals**

- Supporting other media servers *now* (the split makes it possible; we're not building it)
- Changing the AMb2 format (that's `AMB3_FORMAT_ROADMAP.md`)
- Reworking the config page UI
- Rewriting the colour pipeline — it moves as-is, then gets characterisation tests

---

## 🏗 Target Architecture

Dependency direction is strictly one-way: **adapter → core**. Core never references Jellyfin.

```
Ambilight.Core/                   ← no Jellyfin packages
  Amb2/          Amb2Reader, Amb2Writer, Amb2Header
  Leds/          ComputeLedZones, RemapLedFrame, colour pipeline
  Extraction/    ffmpeg driving, extraction orchestration
  Playback/      UDP streaming, pause/seek/timing
  Storage/       .bin + .json on disk
  Options/       AmbilightOptions, DeviceMapping   (plain POCOs)
  Abstractions/  IMediaCatalog, IAmbilightOptionsProvider

Jellyfin.Plugin.Ambilight/        ← the adapter
  Plugin.cs, PluginConfiguration (maps → AmbilightOptions)
  Server/  EntryPoint, ServiceRegistrator
  Api/     AmbilightController
  Tasks/   ExtractPendingAmbilightTask
  Adapters/ JellyfinMediaCatalog, SessionTargetResolver

Ambilight.Core.Tests/             ← xUnit, no Jellyfin
tools/
  ambilight-cli/                  ← standalone driver
  fake-wled/                      ← UDP listener + visualiser
```

---

## 🔌 The Seams

### 1. Configuration

Core defines plain options with no base class:

```csharp
public sealed class AmbilightOptions
{
    public string DataFolder { get; init; } = "/data/ambilight";
    public double Gamma { get; init; } = 2.2;
    public IReadOnlyList<DeviceMapping> DeviceMappings { get; init; } = [];
    // ...
}
```

`PluginConfiguration` stays in the adapter, keeps deriving `BasePluginConfiguration`, and gains a `ToOptions()` mapper.

> ⚠️ **Do not reshape `PluginConfiguration`.** Jellyfin serialises it to XML on disk. Its property names and types must stay byte-compatible, or existing users silently lose their settings on upgrade. The mapper is additive; the persisted shape does not change.

The 18 static `Plugin.Instance?.Configuration` reads are replaced by an injected snapshot provider:

```csharp
public interface IAmbilightOptionsProvider { AmbilightOptions Current { get; } }
```

The adapter supplies `Plugin.Instance!.Configuration.ToOptions()`; tests supply a literal. This **preserves today's deliberate "always read the latest config" behaviour** — the reason device mapping edits apply without a server restart.

### 2. Media catalog

`AmbilightExtractorService` currently blends *which items need extraction* (`ILibraryManager`, `InternalItemsQuery`, `BaseItemKind`, `CollectionFolder`) with *extract this file* (pure). Split on exactly that line:

```csharp
public sealed record MediaEntry(
    string Id, string Name, string Path,
    string LibraryId, string Kind, DateTimeOffset AddedAt);

public interface IMediaCatalog
{
    IReadOnlyList<MediaEntry> GetCandidates();
    MediaEntry? Find(string id);
}
```

- `JellyfinMediaCatalog` wraps `ILibraryManager` (adapter)
- `FolderMediaCatalog` scans a directory (tests, CLI)

Extraction orchestration — priority ordering, library exclusions, pending detection, concurrency limits — moves into Core and becomes testable against a fake catalog.

### 3. Playback and device matching

`AmbilightPlaybackService` is thinner than its size suggests. `ResolveWledTargets` and `StripDeviceIdTimestamp` are pure functions of `(deviceId, deviceName, mappings)` and move to Core:

```csharp
public static IReadOnlyList<DeviceMapping> ResolveTargets(
    string? deviceId, string? deviceName, IEnumerable<DeviceMapping> mappings);
```

Only `SessionInfo` unwrapping stays in the adapter. The base64 device-id timestamp stripping is fiddly, currently untested, and a known source of "why didn't my mapping match" reports — it deserves a test suite far more than it deserves a server.

Its injected `ISessionManager` is never used and should be dropped.

### 4. AMb2 format

Header parsing is currently inline in both the extractor (writer) and player (reader), with the layout duplicated. Extract to `Amb2Reader`/`Amb2Writer`:

| Offset | Type | Field |
|---:|---|---|
| 0 | `byte[4]` | magic `AMb2` |
| 4 | `float32` | fps |
| 8 | `uint16` ×4 | top, bottom, left, right counts |
| 16 | `byte` | fmt (reserved) |
| 17… | repeating | `uint64` timestamp µs + `(T+B+L+R)×3` RGB bytes |

Little-endian. Payload is in canonical order: **Top L→R, Right T→B, Bottom R→L, Left B→T** (clockwise from top-left).

Deduplicating this is a prerequisite for the AMb3 work, which otherwise has to change the layout in two places.

---

## 🧪 Testing Strategy

**Tier 1 — Unit (milliseconds, no I/O)**

AMb2 round-trip · LED zone geometry for arbitrary counts · `RemapLedFrame` across all rotation/direction combinations · colour pipeline characterisation tests · device matching · extraction orchestration against a fake catalog.

**Tier 2 — Component (seconds, no Jellyfin)**

Generate a deterministic input with ffmpeg — no media files committed to the repo:

```bash
ffmpeg -f lavfi -i testsrc=size=320x180:rate=10 -t 2 test.mp4
```

Then extract → inspect → play into the fake WLED and assert the exact bytes on the wire.

Synthetic `.bin` fixtures are more decisive than real video: a file where one bright LED steps clockwise per frame makes ordering errors obvious, and can't be confused with corner smearing from the source/target rescale.

**Tier 3 — Integration (minutes, real Jellyfin)**

Only for what Tiers 1–2 cannot reach: session/device matching end-to-end, `ISessionManager` event wiring, config page save/load round-trip, scheduled task registration. Docker Compose with the DLL bind-mounted into `/config/plugins/Ambilight/`.

> The fake WLED must be reachable **from inside** the Jellyfin container. Run it as a service on the same Compose network. If Jellyfin shares a VPN container's network namespace (e.g. gluetun), LAN egress needs `FIREWALL_OUTBOUND_SUBNETS` — a silent failure mode, because UDP sends always succeed locally regardless of whether anything receives them.

---

## 🛠 Dev Tooling

**`tools/ambilight-cli`** — the general-purpose driver:

```
ambilight extract <video> -o out.bin --top 89 --bottom 89 --left 49 --right 49
ambilight inspect out.bin                  # header, fps, frame count, duration
ambilight render out.bin                   # ASCII preview, no network
ambilight play out.bin --host 127.0.0.1 --port 19446 --input-position 137 --reverse
```

**`tools/fake-wled`** — UDP listener on 19446 that renders the LED ring as terminal ANSI truecolour blocks. Zero dependencies, works over SSH.

> ⚠️ The visualiser must be configured with the **physical wiring** (start corner + direction) and place LED index *i* accordingly. It must not know the plugin's canonical order. If it shares the plugin's assumptions, both sides make the same mistake and the test validates nothing.

---

## ⚠️ Risks

**Packaging — the highest risk.** Jellyfin loads DLLs from the plugin folder, so `Ambilight.Core.dll` must ship alongside the plugin DLL. CI currently copies exactly one file by name (`build.yaml:33`, same in `release.yaml`), and the csproj sets `CopyLocalLockFileAssemblies=false`. Getting this wrong produces a plugin that **passes CI, loads, and then throws `FileNotFoundException` at first use**. Both workflows must be updated in the same change as the project split.

*Mitigation*: an intermediate option is to keep one assembly and enforce the boundary by folder structure plus a test project only. Weaker — nothing prevents a stray `using MediaBrowser` — but zero packaging change. Two projects is the honest version; take the packaging hit and verify the zip contents in CI.

**Config compatibility.** Covered above: keep `PluginConfiguration`'s persisted shape frozen.

**Breadth of the `Plugin.Instance` removal.** Mechanical but touches 9 files. Must preserve "read latest config" semantics.

**No solution file.** The repo has no `.sln`. Adding projects requires one, and CI must build the plugin project explicitly rather than the folder.

---

## 🚚 Migration Plan

Each step is independently shippable and leaves the plugin working.

| # | Step | Risk |
|---:|---|---|
| 1 | Add `.sln` + test project. Lift `Amb2Reader`/`Amb2Writer` and the LED index math into pure helpers; test them. No production restructuring. | very low |
| 2 | Introduce `AmbilightOptions` + `IAmbilightOptionsProvider`. Remove `Plugin.Instance` from the three already-clean services. Still one assembly. | low |
| 3 | Extract `Ambilight.Core` into its own project. **Update CI packaging and assert zip contents.** | **high** |
| 4 | Add `IMediaCatalog`; move extraction orchestration into Core. Move device matching into Core with tests. | medium |
| 5 | Add `tools/ambilight-cli` and `tools/fake-wled`. | low |

Steps 1–2 deliver most of the testability at minimal risk. Step 3 is where the boundary is actually paid for.

This composes well with the Jellyfin 10.11 port: Core is version-agnostic, so the version-coupled surface shrinks to the adapter.

---

## ❓ Open Questions

1. **Core target framework** — `net8.0` to match, or `netstandard2.0` so the CLI and any future adapter have maximum freedom?
2. **Does the CLI ship?** Dev-only tool, or a released artifact for headless/non-Jellyfin users? Affects whether it goes in the release workflow.
3. **`fake-wled` language** — Python (fast to hack, no solution pollution) or a C# CLI subcommand (one toolchain)?
4. **Characterisation tests for the colour pipeline** — pin current output as golden files, accepting that the current values become the spec? Any existing bug gets frozen in, but regressions become visible.
5. **Is `EmbeddedBinariesResolver` deleted as part of this?** It is entirely unreferenced dead code and would otherwise need a home in the new layout.
