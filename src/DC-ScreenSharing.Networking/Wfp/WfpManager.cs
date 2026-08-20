using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Networking.Wfp;

public class WfpManager : IDisposable
{
    private readonly IAppLogger _logger;
    private IntPtr _engineHandle = IntPtr.Zero;
    private readonly List<ulong> _activeFilterIds = new();
    private bool _disposed;

    public WfpManager(IAppLogger logger)
    {
        _logger = logger;
    }

    public bool InitializeSession(bool isDynamic = true)
    {
        if (_engineHandle != IntPtr.Zero)
            return true;

        var session = new WfpNative.FWPM_SESSION0
        {
            flags = isDynamic ? WfpNative.FWPM_SESSION_FLAG_DYNAMIC : 0,
            txnWaitTimeoutInMSec = 5000
        };

        uint result = WfpNative.FwpmEngineOpen0(
            null,
            WfpNative.RPC_C_AUTHN_DEFAULT,
            IntPtr.Zero,
            ref session,
            out _engineHandle);

        if (result != 0)
        {
            _logger.Error($"Failed to open WFP engine session. Error code: 0x{result:X8}");
            _engineHandle = IntPtr.Zero;
            return false;
        }

        _logger.Info("WFP engine dynamic session opened successfully.");
        EnsureProviderAndSublayer();
        return true;
    }

    private void EnsureProviderAndSublayer()
    {
        if (_engineHandle == IntPtr.Zero) return;

        // Register Provider
        var provider = new WfpNative.FWPM_PROVIDER0
        {
            providerKey = WfpNative.DCSS_WFP_PROVIDER_GUID,
            displayData = new WfpNative.FWPM_DISPLAY_DATA0
            {
                name = "DC-ScreenSharing WFP Provider",
                description = "Provides process-aware routing isolation for DC-ScreenSharing"
            }
        };

        uint res = WfpNative.FwpmProviderAdd0(_engineHandle, ref provider, IntPtr.Zero);
        if (res == 0 || res == 0x80320016) // FWP_E_ALREADY_EXISTS
        {
            _logger.Debug("DCSS WFP provider registered.");
        }
        else
        {
            _logger.Warning($"FwpmProviderAdd0 returned 0x{res:X8}");
        }

        // Register Sublayer (High weight 0xFF00)
        IntPtr providerKeyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(WfpNative.DCSS_WFP_PROVIDER_GUID, providerKeyPtr, false);

        try
        {
            var sublayer = new WfpNative.FWPM_SUBLAYER0
            {
                subLayerKey = WfpNative.DCSS_WFP_SUBLAYER_GUID,
                displayData = new WfpNative.FWPM_DISPLAY_DATA0
                {
                    name = "DCSS Discord Routing Sublayer",
                    description = "Sublayer for Discord process traffic redirection"
                },
                providerKey = providerKeyPtr,
                weight = 0xFF00
            };

            res = WfpNative.FwpmSubLayerAdd0(_engineHandle, ref sublayer, IntPtr.Zero);
            if (res == 0 || res == 0x80320016)
            {
                _logger.Debug("DCSS WFP sublayer registered.");
            }
            else
            {
                _logger.Warning($"FwpmSubLayerAdd0 returned 0x{res:X8}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(providerKeyPtr);
        }

        // Register Callouts in BFE
        EnsureCallout(
            WfpNative.DCSS_WFP_CONNECT_REDIRECT_V4_CALLOUT_GUID,
            WfpNative.FWPM_LAYER_ALE_CONNECT_REDIRECT_V4,
            "DCSS IPv4 Connect Redirect Callout",
            "Kernel callout for IPv4 connection redirection");

        EnsureCallout(
            WfpNative.DCSS_WFP_CONNECT_REDIRECT_V6_CALLOUT_GUID,
            WfpNative.FWPM_LAYER_ALE_CONNECT_REDIRECT_V6,
            "DCSS IPv6 Connect Redirect Callout",
            "Kernel callout for IPv6 connection redirection");
    }

    private void EnsureCallout(Guid calloutKey, Guid layerKey, string name, string description)
    {
        IntPtr providerKeyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(WfpNative.DCSS_WFP_PROVIDER_GUID, providerKeyPtr, false);

        try
        {
            var callout = new WfpNative.FWPM_CALLOUT0
            {
                calloutKey = calloutKey,
                displayData = new WfpNative.FWPM_DISPLAY_DATA0
                {
                    name = name,
                    description = description
                },
                providerKey = providerKeyPtr,
                applicableLayer = layerKey
            };

            uint res = WfpNative.FwpmCalloutAdd0(_engineHandle, ref callout, IntPtr.Zero, out _);
            if (res == 0 || res == 0x80320016) // FWP_E_ALREADY_EXISTS
            {
                _logger.Debug($"DCSS WFP callout '{name}' registered in BFE.");
            }
            else
            {
                _logger.Warning($"FwpmCalloutAdd0 '{name}' returned 0x{res:X8}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(providerKeyPtr);
        }
    }

    public bool InstallDiscordFilters(IReadOnlyList<string> discordExePaths)
    {
        if (_engineHandle == IntPtr.Zero && !InitializeSession(true))
            return false;

        _logger.Info($"Installing WFP redirect filters for {discordExePaths.Count} Discord executable(s)...");

        foreach (var path in discordExePaths)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _logger.Warning($"Skipping invalid or non-existent Discord path: {path}");
                continue;
            }

            // Get AppId for verified full path
            uint res = WfpNative.FwpmGetAppIdFromFileName0(path, out IntPtr appIdBlobPtr);
            if (res != 0 || appIdBlobPtr == IntPtr.Zero)
            {
                _logger.Warning($"Failed to get WFP AppId for '{path}'. Code: 0x{res:X8}");
                continue;
            }

            try
            {
                var appIdBlob = Marshal.PtrToStructure<WfpNative.FWP_BYTE_BLOB>(appIdBlobPtr);
                _logger.Info($"Obtained WFP AppId for '{path}' (Blob size: {appIdBlob.size} bytes).");

                // Install V4 and V6 Connect Redirect Filters
                InstallAppFilter(appIdBlobPtr, WfpNative.FWPM_LAYER_ALE_CONNECT_REDIRECT_V4, WfpNative.DCSS_WFP_CONNECT_REDIRECT_V4_CALLOUT_GUID, path, "IPv4");
                InstallAppFilter(appIdBlobPtr, WfpNative.FWPM_LAYER_ALE_CONNECT_REDIRECT_V6, WfpNative.DCSS_WFP_CONNECT_REDIRECT_V6_CALLOUT_GUID, path, "IPv6");
            }
            finally
            {
                WfpNative.FwpmFreeMemory0(ref appIdBlobPtr);
            }
        }

        _logger.Info($"Total active DCSS WFP filters installed: {_activeFilterIds.Count}");
        return _activeFilterIds.Count > 0;
    }

    private void InstallAppFilter(IntPtr appIdBlobPtr, Guid layerKey, Guid calloutKey, string appPath, string ipVersion)
    {
        var condition = new WfpNative.FWPM_FILTER_CONDITION0
        {
            fieldKey = WfpNative.FWPM_CONDITION_ALE_APP_ID,
            matchType = WfpNative.FWP_MATCH_EQUAL,
            conditionValue = new WfpNative.FWP_CONDITION_VALUE0
            {
                type = WfpNative.FWP_DATA_TYPE_BYTE_BLOB,
                byteBlob = appIdBlobPtr
            }
        };

        IntPtr condPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WfpNative.FWPM_FILTER_CONDITION0>());
        Marshal.StructureToPtr(condition, condPtr, false);

        IntPtr providerKeyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(WfpNative.DCSS_WFP_PROVIDER_GUID, providerKeyPtr, false);

        try
        {
            var filter = new WfpNative.FWPM_FILTER0
            {
                filterKey = Guid.NewGuid(),
                displayData = new WfpNative.FWPM_DISPLAY_DATA0
                {
                    name = $"DCSS Discord Redirect Filter ({ipVersion})",
                    description = $"Redirects {Path.GetFileName(appPath)} to DCSS local proxy"
                },
                layerKey = layerKey,
                subLayerKey = WfpNative.DCSS_WFP_SUBLAYER_GUID,
                providerKey = providerKeyPtr,
                weight = new WfpNative.FWP_VALUE0 { type = WfpNative.FWP_DATA_TYPE_UINT8, uint64Val = 15 },
                numFilterConditions = 1,
                filterCondition = condPtr,
                action = new WfpNative.FWPM_ACTION0
                {
                    type = WfpNative.FWP_ACTION_CALLOUT_TERMINATING,
                    calloutOrFilterKey = calloutKey
                }
            };

            uint res = WfpNative.FwpmFilterAdd0(_engineHandle, ref filter, IntPtr.Zero, out ulong filterId);
            if (res == 0)
            {
                _activeFilterIds.Add(filterId);
                _logger.Info($"Installed DCSS {ipVersion} filter ID: {filterId} for {Path.GetFileName(appPath)}");
            }
            else
            {
                _logger.Warning($"FwpmFilterAdd0 ({ipVersion}) returned 0x{res:X8} for {appPath}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(condPtr);
            Marshal.FreeHGlobal(providerKeyPtr);
        }
    }

    public void RemoveAllFilters()
    {
        if (_engineHandle == IntPtr.Zero) return;

        foreach (var id in _activeFilterIds)
        {
            WfpNative.FwpmFilterDeleteById0(_engineHandle, id);
        }
        _activeFilterIds.Clear();
        _logger.Info("All active DCSS WFP filters removed.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RemoveAllFilters();

        if (_engineHandle != IntPtr.Zero)
        {
            WfpNative.FwpmEngineClose0(_engineHandle);
            _engineHandle = IntPtr.Zero;
            _logger.Info("WFP engine session closed.");
        }
    }
}
