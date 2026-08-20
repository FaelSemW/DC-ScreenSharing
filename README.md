# DC-ScreenSharing

DC-ScreenSharing is a Windows desktop application that performs per-application network routing for supported applications. It directs targeted application network traffic through a secure WireGuard tunnel while leaving all other system applications on the standard network interface.

## Supported Platforms

* Windows 10 x64 (Build 19041 or higher)
* Windows 11 x64

## Installation & Updates

* **Latest Version**: `v1.0.5`
* **New Users**: Download and run `DC-ScreenSharing-Setup-1.0.5.exe` from the [Latest GitHub Release](https://github.com/FaelSemW/DC-ScreenSharing/releases/latest).
* **Existing Users**: Updates are detected, verified, and installed automatically on launch.

## System Architecture

The solution consists of the following components:

* **DC-ScreenSharing.App**: Modern WPF client providing server selection, one-click connection control, activation management, and diagnostics export.
* **DCSS.NetworkService**: Privileged Windows Service running as LocalSystem, managing interface creation, routing rules, and tunnel lifecycle.
* **ProcessRoutingEngine**: Network routing subsystem utilizing sing-box and Wintun for process-level split tunneling.
* **DCSS.ProfileService**: Backend service for managing server catalogs, cryptographic generations, and secure profile distribution.
* **DCSS.Maintainer**: Operator utility for parsing WireGuard configurations, signing catalog generations with DPAPI keys, and publishing updates.
* **DC-ScreenSharing.Updater**: Automatic update checking, SHA-256 verification, and silent installer coordination.

## Building from Source

### Prerequisites

* .NET 8.0 SDK (x64)
* Inno Setup 6 (for packaging installer executables)

### Build Commands

```powershell
# Restore and run automated test suite
dotnet test

# Build and package production installer
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Configuration Release -Version 1.0.5
```

The compiled installer is output to `dist/installer/DC-ScreenSharing-Setup-1.0.5.exe`.

## ProfileService Development Setup

For local testing and backend development:

```powershell
dotnet run --project server/DCSS.ProfileService
```

The development service listens on `http://localhost:5000`.

## Testing

The solution includes automated unit and integration test suites covering cryptographic operations, proof-of-possession signing, IPC communication, configuration generation, and process isolation.

```powershell
dotnet test tests/DC-ScreenSharing.Core.Tests
dotnet test tests/DC-ScreenSharing.Networking.Tests
dotnet test tests/DC-ScreenSharing.IntegrationTests
```

## Security Policy

* Never commit production WireGuard profiles (`configs/*.conf`), `ADMIN_API_KEY`, maintainer signing keys, production `.env` files, or activation codes.
* All production profile distribution uses Proof-of-Possession challenge signing.
* Sensitive client storage is protected using Windows DPAPI.

## License

See the [LICENSE](LICENSE) file for licensing details.
