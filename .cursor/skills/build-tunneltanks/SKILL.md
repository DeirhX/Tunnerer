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

`Tunnerer.Desktop` compiles its HLSL shaders with `fxc.exe` from the Windows SDK during the build. If the build
fails on shader compilation, pass the compiler path: `-p:Dx11ShaderCompiler="C:\path\to\fxc.exe"`.

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

The desktop client is Windows only (SDL2 window, DirectX 11). `--hidden` runs it without showing a window.
