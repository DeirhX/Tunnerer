---
name: build-emberward
description: Build, test and run the Emberward project (C# / .NET 10). Use when the user asks to build, compile, run, test, or launch the game, or when build errors occur.
---

# Build Emberward

The game is a C# / .NET 10 solution, `Emberward.slnx`, at the repository root. There is no C++ build in this
repository any more. Run every command from the repository root.

## Build

```powershell
dotnet build Emberward.slnx
```

`Emberward.Desktop` compiles its HLSL shaders to DXIL, SPIR-V and MSL during the build (SDL_shadercross from NuGet).

## Test

```powershell
dotnet test Emberward.slnx
```

One test class or test:

```powershell
dotnet test tests\Emberward.Tests --filter "FullyQualifiedName~ThermalLedgerTests"
```

The tests cover `Emberward.Core` only and need no window.

## Run

```powershell
dotnet run --project src\Emberward.Desktop
```

The desktop client runs on Windows and macOS (SDL3 window, SDL_GPU renderer). `--hidden` runs it without showing a window.
