#pragma once

#include <ntddk.h>
#include <wdf.h>
#define NDIS_SUPPORT_NDIS6 1
#define NDIS60 1
#include <ndis.h>
#include <fwpsk.h>
#include <fwpmk.h>
#include <ws2ipdef.h>
#include <in6addr.h>

#include "WfpGuids.h"
#include "Trace.h"

// Context structure passed during redirection
typedef struct _DCSS_REDIRECT_CONTEXT {
    UINT32 OriginalDestinationIPv4;
    UINT8  OriginalDestinationIPv6[16];
    UINT16 OriginalDestinationPort;
    UINT8  Protocol;
    UINT8  AddressFamily;
    UINT32 ProcessId;
} DCSS_REDIRECT_CONTEXT, *PDCSS_REDIRECT_CONTEXT;

NTSTATUS RegisterWfpCallouts(PDEVICE_OBJECT DeviceObject);
VOID UnregisterWfpCallouts(VOID);

// Classify functions
void NTAPI DcssClassifyConnectRedirectV4(
    const FWPS_INCOMING_VALUES0* inFixedValues,
    const FWPS_INCOMING_METADATA_VALUES0* inMetaValues,
    void* layerData,
    const void* classifyContext,
    const FWPS_FILTER3* filter,
    UINT64 flowContext,
    FWPS_CLASSIFY_OUT0* classifyOut);

void NTAPI DcssClassifyConnectRedirectV6(
    const FWPS_INCOMING_VALUES0* inFixedValues,
    const FWPS_INCOMING_METADATA_VALUES0* inMetaValues,
    void* layerData,
    const void* classifyContext,
    const FWPS_FILTER3* filter,
    UINT64 flowContext,
    FWPS_CLASSIFY_OUT0* classifyOut);

NTSTATUS NTAPI DcssNotifyConnectRedirect(
    FWPS_CALLOUT_NOTIFY_TYPE notifyType,
    const GUID* filterKey,
    FWPS_FILTER3* filter);
