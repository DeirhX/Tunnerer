# Microsoft Store publishing (Windows)

Emberflow is reserved in Partner Center as a **Win32 PC game**. The current Store game flow uses the Microsoft Game
Development Kit (GDK) to package Win32 games as MSIXVC. This does not add Xbox services to the game; those are
optional. Microsoft documents this as the self-service PC game route.

## Build and test a package

Use a Windows PC with the .NET 10 SDK and the October 2026 Microsoft GDK (or a later version that supports MSIXVC2
and the per-executable ARM64 architecture attribute).
Open a Microsoft GDK command prompt, check out this repository, and run:

```powershell
powershell -NoProfile -File tools/store/gdk/Build-StorePackage.ps1 -Version 0.2.0
```

Use the stable three-part version being released. The script adds a final `.0` version component because the Store
reserves that component. It publishes self-contained `win-x64` and `win-arm64` desktop builds, stages them as separate
executables in one package, adds the Partner Center identity and shell images, generates a package layout, builds an
MSIXVC2 package, and runs the Submission Validator. GDK requires the x64 executable to accompany an ARM64 executable.
Output is under `artifacts/store/gdk/<version>/`.

To install the package for a local smoke test, enable Windows developer mode and add `-InstallForTest`:

```powershell
powershell -NoProfile -File tools/store/gdk/Build-StorePackage.ps1 -Version 0.2.0 -InstallForTest
```

Check that the game launches, renders, accepts input, reaches the relay, and saves settings. Test the x64 executable on
x64 Windows and the ARM64 executable on a Windows on Arm device if one is available. The single-file .NET builds
extract native SDL libraries at runtime, so that behavior needs a real Windows package test before submission.

The script **does not upload or submit** the package. Keep its output in the ignored `artifacts/` directory. Review the
Submission Validator log, then upload the package and its generated sidecar files through Partner Center. Complete the
Properties, Age ratings, Store listings, and Pricing/availability sections; submit for certification only after the
installed package has passed the smoke test.

The `MicrosoftGame.config` contains the identity assigned to this Partner Center product. Do not change its identity
fields independently of Partner Center. Store distribution signs the package; this does not add an Authenticode
signature to the separate GitHub ZIP release.

## Microsoft references

- [Publish PC games to the Microsoft Store using GDK](https://learn.microsoft.com/en-us/windows/apps/publish/whats-new-game-publishing)
- [Get started with GDK PC packaging](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/packaging/overviews/packaging-getting-started-for-pc?view=gdk-2610)
- [GDK `makepkg` commands](https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/packaging/deployment/makepkg?view=gdk-2610)
- [Publish the game in Partner Center](https://learn.microsoft.com/en-us/gaming/gdk/docs/gdk-dev/pc-dev/tutorials/pc-e2e-guide/e2e-publishing-your-game?view=gdk-2610)
