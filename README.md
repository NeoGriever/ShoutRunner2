# ShoutRunner2

Dalamud plugin for **Final Fantasy XIV** to plan and execute multi-world travel and action routes.

## Features

- Reusable route presets with physical/logical data centers, worlds and city destinations.
- Ordered city selection; execute chat commands, wait steps and navigation actions.
- Lifestream world/city travel and optional vnavmesh navigation.
- Preview and per-world progress and event history.
- Optional compact status window with progress, Gil balance changes and runtime controls.
- Pause at a safe execution boundary and restart a run.

## Commands

- `/sr`: open the route editor.
- `/sr run`: run the selected preset.
- `/sr stop`: stop the current run.

## Installation

Add this **custom Dalamud repository URL** in Dalamud settings, enable it, and install ShoutRunner through `/xlplugins`:

`https://raw.githubusercontent.com/NeoGriever/ShoutRunner2/main/repo.json`

## Building

Requires .NET SDK, Dalamud.NET.Sdk and the compatible Dalamud development environment.

```shell
dotnet build ShoutRunner2.csproj -c Release
```

The Dalamud packager creates `bin/Release/ShoutRunner/latest.zip`.

## Dependencies

In-game travel/navigation requires compatible versions of Lifestream and (for navigation actions) vnavmesh. Plugin compatibility depends on the installed Dalamud API version.

## Licensing

The project currently declares `AGPL-3.0-or-later` in its `.csproj`. Verify that all included source code may be distributed under that license.
