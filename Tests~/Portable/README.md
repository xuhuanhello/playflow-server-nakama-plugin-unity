# Portable protocol/security suite

Requires .NET SDK 10. This is a console contract suite, independent of the Unity Editor and cloud credentials.

`.github/workflows/portable-tests.yml` runs this portable target only. Unity reference-assembly compilation remains a separately documented local validation step and is not represented as a Unity player/Editor test.

```sh
dotnet run --project Tests~/Portable/PortableTests.csproj
```

The default restores Newtonsoft.Json 13.0.2, matching the assembly version packaged by Unity's `com.unity.nuget.newtonsoft-json` 3.2.1. To use an already-installed official Unity package and avoid restoring that dependency:

```sh
dotnet run --project Tests~/Portable/PortableTests.csproj \
  -p:UnityNewtonsoftPath="/absolute/path/to/com.unity.nuget.newtonsoft-json@3.2.1/Runtime/Newtonsoft.Json.dll"
```

Add `-p:UnityManagedPath="/absolute/path/to/Unity.app/Contents/Managed/UnityEngine"` to compile the runtime agent and minimal sample against real Unity reference assemblies as part of this run. This validates the referenced API surface; it does not run those Unity components or a live UnityWebRequest. Linux/Windows Editor installations have equivalent managed assembly directories.

For an additional compile against Unity's .NET Standard 2.1 base API surface:

```sh
dotnet build Tests~/Portable/UnityCompile.csproj \
  -p:UnityManagedPath="/absolute/path/to/Unity.app/Contents/Managed/UnityEngine" \
  -p:UnityNewtonsoftPath="/absolute/path/to/com.unity.nuget.newtonsoft-json@3.2.1/Runtime/Newtonsoft.Json.dll"
```

The fixed fixture key is public test data. Regenerate the cross-language ticket with Go's standard library:

```sh
cd Tests~/Portable/fixtures
go run generate.go > admission-v1.json
```

The C# tests verify the Go-produced token and byte-identical C# serialization/signing. Do not use the fixture key in any deployment. Test artifacts under `bin/` and `obj/` are local outputs and must not be published in the UPM package.
