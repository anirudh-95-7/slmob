# SL Mobile Viewer

Lightweight Second Life / OpenSim client for Android. .NET 8 MAUI + LibreMetaverse.
Features: async login, local chat + IM log, LSL blue-menu (llDialog) bottom sheet,
and a 20 m spatial-cull engine that purges any primitive beyond a 20-meter bubble.

## Build (produces signed APK)

```bash
dotnet workload install maui-android
dotnet restore
dotnet publish -f net8.0-android -c Release -p:AndroidPackageFormat=apk
```

Or run `./build.sh` (bash) / `pwsh build.ps1`. Output:

```
bin/Release/net8.0-android/publish/com.viewer.slmobile-Signed.apk
```

The publish target signs with an auto-generated debug keystore. For a release
keystore add:

```
-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=my.keystore \
-p:AndroidSigningKeyAlias=slmobile -p:AndroidSigningKeyPass=*** -p:AndroidSigningStorePass=***
```

Prereqs: .NET 8 SDK, JDK 17, Android SDK (API 34). If the Android SDK is missing:
`dotnet build -t:InstallAndroidDependencies -f net8.0-android -p:AndroidSdkDirectory=$HOME/android-sdk -p:AcceptAndroidSDKLicenses=true`

## CI

Pushing this repo to GitHub triggers `.github/workflows/android-build.yml`,
which builds and uploads `com.viewer.slmobile-Signed.apk` as an artifact.

## Structure

- `Services/SecondLifeService.cs` — GridClient singleton; async login; adult maturity
  handshake; chat/IM/ScriptDialog callbacks marshalled to the UI thread;
  `ReplyToScriptDialog(channel, index, label, objectID)`.
- `Services/SpatialCullEngine.cs` — 20 m bubble: intercepts ObjectUpdate/TerseObjectUpdate,
  `Vector3.Distance(avatarPos, primPos)`, retains only `distance <= 20.0f`, 2 s purge sweep.
- `MainPage.xaml(.cs)` — login panel, chat viewport, live 20 m prim list, blue-menu modal.
- `Platforms/Android/AndroidManifest.xml` — INTERNET / ACCESS_NETWORK_STATE /
  ACCESS_WIFI_STATE, cleartext traffic allowed (OpenSim grids).
