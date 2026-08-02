# Local Windows/macOS/Linux build -> signed APK
# Prereqs: .NET 8 SDK (https://dot.net), JDK 17, Android SDK (or let MAUI install it: see README)
$ErrorActionPreference = "Stop"

# Step 1: Install the MAUI Android workload
dotnet workload install maui-android

# Step 2: Restore project dependencies
dotnet restore

# Step 3: Publish the compiled Release APK (signed with debug keystore by default)
dotnet publish -f net8.0-android -c Release -p:AndroidPackageFormat=apk

$apk = Get-ChildItem -Recurse "bin/Release/net8.0-android/publish/*-Signed.apk" | Select-Object -First 1
Write-Host "`nSIGNED APK: $($apk.FullName)"
