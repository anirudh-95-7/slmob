#!/usr/bin/env bash
# Local build -> signed APK. Prereqs: .NET 8 SDK, JDK 17, Android SDK.
set -euo pipefail

# Step 1: Install the MAUI Android workload
dotnet workload install maui-android

# Step 2: Restore project dependencies
dotnet restore

# Step 3: Publish the compiled Release APK (signed with debug keystore by default)
dotnet publish -f net8.0-android -c Release -p:AndroidPackageFormat=apk

echo
echo "SIGNED APK: $(ls bin/Release/net8.0-android/publish/*-Signed.apk)"
