# LiteNetLib

- Version: **1.3.5**, latest stable 1.x verified on 2026-11-02 using the NuGet version index.
- Source: https://www.nuget.org/packages/LiteNetLib/1.3.5
- File: `lib/netstandard2.1/LiteNetLib.dll` from the official NuGet package.
- SHA-256: `af7674845318089130bb49571a6947253d00a6d94e96086c0c89756df16b451e`
- License: MIT; see `LiteNetLib.LICENSE.txt`.

The server references the same version through NuGet in `server/Shared.Build/ElementalLegion.Shared.csproj`. Keep the NuGet reference, lock files, this DLL, and this note in sync when upgrading. The shared asmdef explicitly references this DLL and Newtonsoft.Json.dll.
