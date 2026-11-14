---
name: build-tunneltanks
description: Build, test and run the Tunnerer project (C# / .NET 10). Use when the user asks to build, compile, run, test, or launch the game, or when build errors occur.
---

# Build Tunnerer

The game is a C# / .NET 10 solution, `Tunnerer.slnx`, at the repository root. There is no C++ build in this
repository any more. Run every command from the repository root.

## Build

```powershell
dotnet build Tunnerer.slnx
```

`Tunnerer.Desktop` compiles its HLSL shaders to DXIL, SPIR-V and MSL during the build (SDL_shadercross from NuGet).

## Test

```powershell
dotnet test Tunnerer.slnx
```

One test class or test:

```powershell
dotnet test tests\Tunnerer.Tests --filter "FullyQualifiedName~ThermalLedgerTests"
```

The tests cover `Tunnerer.Core` only and need no window.

## Run

```powershell
dotnet run --project src\Tunnerer.Desktop
```

The desktop client runs on Windows and macOS (SDL3 window, SDL_GPU renderer). `--hidden` runs it without showing a window.
