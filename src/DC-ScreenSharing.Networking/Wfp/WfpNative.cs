using System;
using System.Runtime.InteropServices;

namespace DCScreenSharing.Networking.Wfp;

public static class WfpNative
{
    public const uint RPC_C_AUTHN_WINNT = 10;
    public const uint RPC_C_AUTHN_DEFAULT = 0xFFFFFFFF;

    public const uint FWPM_SESSION_FLAG_DYNAMIC = 0x00000001;

    // Standard WFP Layer GUIDs
    // FWPM_LAYER_ALE_CONNECT_REDIRECT_V4: {4A723C91-B61A-483E-B147-975D275A9C0C}
    public static readonly Guid FWPM_LAYER_ALE_CONNECT_REDIRECT_V4 =
        new(0x4a723c91, 0xb61a, 0x483e, 0xb1, 0x47, 0x97, 0x5d, 0x27, 0x5a, 0x9c, 0x0c);

    // FWPM_LAYER_ALE_CONNECT_REDIRECT_V6: {7EB8BE8A-CAEC-4D88-9226-5F7932822B4D}
    public static readonly Guid FWPM_LAYER_ALE_CONNECT_REDIRECT_V6 =
        new(0x7eb8be8a, 0xcaec, 0x4d88, 0x92, 0x26, 0x5f, 0x79, 0x32, 0x82, 0x2b, 0x4d);

    // FWPM_CONDITION_ALE_APP_ID: {3970E102-18A8-433E-BDC7-F0D6EC83C25D}
    public static readonly Guid FWPM_CONDITION_ALE_APP_ID =
        new(0x3970e102, 0x18a8, 0x433e, 0xbd, 0xc7, 0xf0, 0xd6, 0xec, 0x83, 0xc2, 0x5d);

    // FWPM_CONDITION_IP_LOCAL_ADDRESS_TYPE
    public static readonly Guid FWPM_CONDITION_IP_LOCAL_ADDRESS_TYPE =
        new(0x8979b940, 0xb696, 0x4fc1, 0xb9, 0x20, 0xa1, 0x3d, 0x79, 0x86, 0x00, 0x6e);

    // DCSS WFP GUIDs
    public static readonly Guid DCSS_WFP_PROVIDER_GUID =
        new(0xd4e75a10, 0xb148, 0x4d02, 0x98, 0xc3, 0x28, 0xf8, 0x8e, 0x52, 0xa1, 0xc1);

    public static readonly Guid DCSS_WFP_SUBLAYER_GUID =
        new(0xa1b2c3d4, 0xe5f6, 0x4a5b, 0x8c, 0x9d, 0x0e, 0x1f, 0x2a, 0x3b, 0x4c, 0x5d);

    public static readonly Guid DCSS_WFP_CONNECT_REDIRECT_V4_CALLOUT_GUID =
        new(0x98b263f1, 0x5c72, 0x4b2e, 0x8e, 0x7d, 0x1f, 0x3a, 0x4b, 0x5c, 0x6d, 0x7e);

    public static readonly Guid DCSS_WFP_CONNECT_REDIRECT_V6_CALLOUT_GUID =
        new(0x87a152e0, 0x4b61, 0x3a1d, 0x7d, 0x6c, 0x0e, 0x2f, 0x3a, 0x4b, 0x5c, 0x6d);

    public const uint FWP_MATCH_EQUAL = 0;
    public const uint FWP_MATCH_NOT_EQUAL = 1;

    public const uint FWP_DATA_TYPE_BYTE_BLOB = 12;
    public const uint FWP_DATA_TYPE_UINT8 = 1;
    public const uint FWP_DATA_TYPE_UINT16 = 2;
    public const uint FWP_DATA_TYPE_UINT32 = 3;

    public const uint FWP_ACTION_CALLOUT_TERMINATING = 0x00000001 | 0x00004000;
    public const uint FWP_ACTION_PERMIT = 0x00000001;
    public const uint FWP_ACTION_BLOCK = 0x00000002;

    public const int SIO_QUERY_WFP_CONNECTION_REDIRECT_RECORDS = unchecked((int)0x480000DC);
    public const int SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT = unchecked((int)0x480000DD);
    public const int SIO_SET_WFP_CONNECTION_REDIRECT_RECORDS = unchecked((int)0x880000DE);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FWPM_SESSION0
    {
        public Guid sessionKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public uint txnWaitTimeoutInMSec;
        public uint processId;
        public IntPtr sid;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? username;
        [MarshalAs(UnmanagedType.Bool)]
        public bool kernelMode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FWPM_DISPLAY_DATA0
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string name;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string description;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_PROVIDER0
    {
        public Guid providerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public FWP_BYTE_BLOB providerData;
        public IntPtr serviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_SUBLAYER0
    {
        public Guid subLayerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey; // Nullable Guid*
        public FWP_BYTE_BLOB providerData;
        public ushort weight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_BYTE_BLOB
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_CALLOUT0
    {
        public Guid calloutKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey; // Nullable Guid*
        public FWP_BYTE_BLOB providerData;
        public Guid applicableLayer;
        public uint calloutId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_FILTER_CONDITION0
    {
        public Guid fieldKey;
        public uint matchType;
        public FWP_CONDITION_VALUE0 conditionValue;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct FWP_CONDITION_VALUE0
    {
        [FieldOffset(0)]
        public uint type;
        [FieldOffset(8)]
        public uint uint8Val;
        [FieldOffset(8)]
        public uint uint16Val;
        [FieldOffset(8)]
        public uint uint32Val;
        [FieldOffset(8)]
        public IntPtr byteBlob; // FWP_BYTE_BLOB*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_ACTION0
    {
        public uint type;
        public Guid calloutOrFilterKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_VALUE0
    {
        public uint type;
        public ulong uint64Val;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_FILTER0
    {
        public Guid filterKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public Guid layerKey;
        public Guid subLayerKey;
        public FWP_VALUE0 weight;
        public uint numFilterConditions;
        public IntPtr filterCondition; // FWPM_FILTER_CONDITION0*
        public FWPM_ACTION0 action;
        public ulong context;
        public IntPtr reserved;
        public ulong filterId;
        public FWP_VALUE0 effectiveWeight;
    }

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false)]
    public static extern uint FwpmEngineOpen0(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
        uint authnService,
        IntPtr authIdentity,
        ref FWPM_SESSION0 session,
        out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false)]
    public static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false)]
    public static extern uint FwpmProviderAdd0(
        IntPtr engineHandle,
        ref FWPM_PROVIDER0 provider,
        IntPtr sd);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false)]
    public static extern uint FwpmProviderDeleteByKey0(
        IntPtr engineHandle,
        ref Guid key);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false)]
    public static extern uint FwpmSubLayerAdd0(
        IntPtr engineHandle,
        ref FWPM_SUBLAYER0 subLayer,
        IntPtr sd);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false)]
    public static extern uint FwpmSubLayerDeleteByKey0(
        IntPtr engineHandle,
        ref Guid key);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false)]
    public static extern uint FwpmCalloutAdd0(
        IntPtr engineHandle,
        ref FWPM_CALLOUT0 callout,
        IntPtr sd,
        out uint id);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false)]
    public static extern uint FwpmCalloutDeleteByKey0(
        IntPtr engineHandle,
        ref Guid key);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    public static extern uint FwpmGetAppIdFromFileName0(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        out IntPtr appId);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false)]
    public static extern void FwpmFreeMemory0(ref IntPtr p);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false)]
    public static extern uint FwpmFilterAdd0(
        IntPtr engineHandle,
        ref FWPM_FILTER0 filter,
        IntPtr sd,
        out ulong id);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, SetLastError = false)]
    public static extern uint FwpmFilterDeleteById0(
        IntPtr engineHandle,
        ulong id);
}

