# Fork updates (ItsSKYT)

This fork checks `https://raw.githubusercontent.com/ItsSKYT/StabilityMatrix/main/update/update-v3.json` instead of the Lykos CDN.

## Signing keys

- Public key is embedded in `StabilityMatrix.Core/Updater/SignatureChecker.cs`
- Private key lives in `Build/update-keys/private.pem` (gitignored)

Generate new keys:

```powershell
dotnet run --project Build/UpdateSigner -- gen-keys Build/update-keys
```

## Publish a release + update manifest

```powershell
$ver = "2.16.2-skyt.1"
$env:HUSKY = 0

# 1) Publish exe
dotnet publish ./StabilityMatrix.Avalonia/StabilityMatrix.Avalonia.csproj `
  -o out -c Release -r win-x64 `
  -p:Version=$ver `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishReadyToRun=true `
  -p:SelfContained=true

Copy-Item out/StabilityMatrix.Avalonia.exe out/StabilityMatrix.exe -Force

# 2) Zip for GitHub Releases (asset name must match)
Compress-Archive -Path out/StabilityMatrix.exe -DestinationPath "out/StabilityMatrix-win-x64.zip" -Force

# 3) Create GitHub release
gh release create "v$ver" "out/StabilityMatrix-win-x64.zip" `
  --title "v$ver" `
  --notes "Fork build with Output Browser metadata UI + Krea2 workflow."

# 4) Sign and write update/update-v3.json
dotnet run --project Build/UpdateSigner -- sign-release `
  --zip out/StabilityMatrix-win-x64.zip `
  --version $ver `
  --url "https://github.com/ItsSKYT/StabilityMatrix/releases/download/v$ver/StabilityMatrix-win-x64.zip" `
  --changelog "https://raw.githubusercontent.com/ItsSKYT/StabilityMatrix/main/CHANGELOG.md" `
  --keys-dir Build/update-keys `
  --out update/update-v3.json `
  --channel stable `
  --platform win-x64

git add update/update-v3.json
git commit -m "chore: publish update manifest for v$ver"
git push origin HEAD
```
