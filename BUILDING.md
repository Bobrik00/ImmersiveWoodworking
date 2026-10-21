# Building from source

Set `VINTAGE_STORY` to your game folder and run `dotnet build ImmersiveWoodworking.csproj -c Release` (.NET 10 SDK). The build calls Windows PowerShell twice, to regenerate `SawdustPoints.cs` and to strip comments from the copied JSON assets, so it needs Windows. The finished mod, assets included, lands in `bin/Release/Mods/mod`; `powershell -File tools\pack-release.ps1` zips it into `Releases/immersivewoodworking_<version>.zip`.
