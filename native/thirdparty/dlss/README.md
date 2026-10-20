# Bundled DLSS runtime

Place the unmodified NVIDIA-signed release DLL `nvngx_dlss.dll` here before building a public package. It is intentionally excluded from git.

Pinned version: 310.9.1.0. SHA-256: `3975567b8943c53acce397f2b72380092f84f162d00b0d2c7d08a1025c563983`.

The managed startup code validates that hash and upgrades an older `bin/Win64_Shipping_Client/nvngx_dlss.dll` using an atomic replacement. The first original is retained as `nvngx_dlss.dll.bannerlordvr-original.bak`. Equal or newer game DLLs are preserved. If DLSS was already loaded, a restart is required; a failure is logged and the game continues with its current runtime.

The package includes `module/NVIDIA-DLSS-LICENSE.txt` and the NVIDIA guide's third-party notices in `THIRD_PARTY_NOTICES.md`. The DLL is proprietary NVIDIA software, separate from BannerlordVR's MIT license. Updating the pinned runtime requires updating both `VrDlssRuntime.cs` and `package-release.ps1` and revalidating its NVIDIA signature.

Official SDK: https://github.com/NVIDIA/DLSS
