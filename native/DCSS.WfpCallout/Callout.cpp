#include "Callout.h"

static UINT32 g_V4CalloutId = 0;
static UINT32 g_V6CalloutId = 0;
static HANDLE g_RedirectHandle = NULL;

NTSTATUS RegisterWfpCallouts(PDEVICE_OBJECT DeviceObject)
{
    NTSTATUS status = STATUS_SUCCESS;
    FWPS_CALLOUT3 calloutV4 = { 0 };
    FWPS_CALLOUT3 calloutV6 = { 0 };

    // 1. Create redirect handle
    status = FwpsRedirectHandleCreate0(&DCSS_WFP_PROVIDER_GUID, 0, &g_RedirectHandle);
    if (!NT_SUCCESS(status)) {
        DCSS_LOG_ERROR("FwpsRedirectHandleCreate0 failed: 0x%08X", status);
        return status;
    }

    // 2. Register V4 Connect Redirect Callout
    calloutV4.calloutKey = DCSS_WFP_CONNECT_REDIRECT_V4_CALLOUT_GUID;
    calloutV4.flags = 0;
    calloutV4.classifyFn = DcssClassifyConnectRedirectV4;
    calloutV4.notifyFn = DcssNotifyConnectRedirect;
    calloutV4.flowDeleteFn = NULL;

    status = FwpsCalloutRegister3(DeviceObject, &calloutV4, &g_V4CalloutId);
    if (!NT_SUCCESS(status)) {
        DCSS_LOG_ERROR("FwpsCalloutRegister3 (V4) failed: 0x%08X", status);
        FwpsRedirectHandleDestroy0(g_RedirectHandle);
        g_RedirectHandle = NULL;
        return status;
    }

    // 3. Register V6 Connect Redirect Callout
    calloutV6.calloutKey = DCSS_WFP_CONNECT_REDIRECT_V6_CALLOUT_GUID;
    calloutV6.flags = 0;
    calloutV6.classifyFn = DcssClassifyConnectRedirectV6;
    calloutV6.notifyFn = DcssNotifyConnectRedirect;
    calloutV6.flowDeleteFn = NULL;

    status = FwpsCalloutRegister3(DeviceObject, &calloutV6, &g_V6CalloutId);
    if (!NT_SUCCESS(status)) {
        DCSS_LOG_ERROR("FwpsCalloutRegister3 (V6) failed: 0x%08X", status);
        FwpsCalloutUnregisterById0(g_V4CalloutId);
        g_V4CalloutId = 0;
        FwpsRedirectHandleDestroy0(g_RedirectHandle);
        g_RedirectHandle = NULL;
        return status;
    }

    DCSS_LOG_INFO("WFP callouts registered successfully (V4 Id: %u, V6 Id: %u)", g_V4CalloutId, g_V6CalloutId);
    return STATUS_SUCCESS;
}

VOID UnregisterWfpCallouts(VOID)
{
    if (g_V6CalloutId != 0) {
        FwpsCalloutUnregisterById0(g_V6CalloutId);
        g_V6CalloutId = 0;
    }

    if (g_V4CalloutId != 0) {
        FwpsCalloutUnregisterById0(g_V4CalloutId);
        g_V4CalloutId = 0;
    }

    if (g_RedirectHandle != NULL) {
        FwpsRedirectHandleDestroy0(g_RedirectHandle);
        g_RedirectHandle = NULL;
    }

    DCSS_LOG_INFO("WFP callouts unregistered.");
}

void NTAPI DcssClassifyConnectRedirectV4(
    const FWPS_INCOMING_VALUES0* inFixedValues,
    const FWPS_INCOMING_METADATA_VALUES0* inMetaValues,
    void* layerData,
    const void* classifyContext,
    const FWPS_FILTER3* filter,
    UINT64 flowContext,
    FWPS_CLASSIFY_OUT0* classifyOut)
{
    UNREFERENCED_PARAMETER(classifyContext);
    UNREFERENCED_PARAMETER(filter);
    UNREFERENCED_PARAMETER(flowContext);

    // Default action: permit
    classifyOut->actionType = FWP_ACTION_PERMIT;

    if (layerData == NULL) return;

    FWPS_CONNECT_REQUEST0* connectRequest = (FWPS_CONNECT_REQUEST0*)layerData;

    // Check redirection state to prevent recursion
    FWPS_CONNECTION_REDIRECT_STATE redirectState = FwpsQueryConnectionRedirectState0(
        g_RedirectHandle,
        connectRequest,
        NULL);

    if (redirectState == FWPS_CONNECTION_REDIRECTED_BY_SELF ||
        redirectState == FWPS_CONNECTION_PREVIOUSLY_REDIRECTED_BY_SELF) {
        // Already redirected by DCSS - permit cleanly without loop
        return;
    }

    UINT32 remoteIp = inFixedValues->incomingValue[FWPS_FIELD_ALE_CONNECT_REDIRECT_V4_IP_REMOTE_ADDRESS].value.uint32;
    UINT16 remotePort = inFixedValues->incomingValue[FWPS_FIELD_ALE_CONNECT_REDIRECT_V4_IP_REMOTE_PORT].value.uint16;
    UINT8 protocol = inFixedValues->incomingValue[FWPS_FIELD_ALE_CONNECT_REDIRECT_V4_IP_PROTOCOL].value.uint8;

    // Ignore localhost connections (127.0.0.0/8)
    if ((remoteIp & 0xFF) == 127) return;

    // Allocate redirect context to store original destination
    PDCSS_REDIRECT_CONTEXT redirContext = (PDCSS_REDIRECT_CONTEXT)ExAllocatePool2(
        POOL_FLAG_PAGED,
        sizeof(DCSS_REDIRECT_CONTEXT),
        DCSS_WFP_POOL_TAG);

    if (redirContext == NULL) return;

    redirContext->OriginalDestinationIPv4 = remoteIp;
    RtlZeroMemory(redirContext->OriginalDestinationIPv6, sizeof(redirContext->OriginalDestinationIPv6));
    redirContext->OriginalDestinationPort = remotePort;
    redirContext->Protocol = protocol;
    redirContext->AddressFamily = AF_INET;
    redirContext->ProcessId = (UINT32)inMetaValues->processId;

    // Redirect to local loopback proxy (127.0.0.1 : DCSS_DEFAULT_REDIRECT_PORT)
    SOCKADDR_IN* localTarget = (SOCKADDR_IN*)&connectRequest->remoteAddressAndPort;
    localTarget->sin_family = AF_INET;
    localTarget->sin_port = RtlUshortByteSwap(DCSS_DEFAULT_REDIRECT_PORT);
    localTarget->sin_addr.s_addr = RtlUlongByteSwap(0x7F000001); // 127.0.0.1

    connectRequest->localRedirectHandle = g_RedirectHandle;
    connectRequest->localRedirectContext = redirContext;
    connectRequest->localRedirectContextSize = sizeof(DCSS_REDIRECT_CONTEXT);

    classifyOut->actionType = FWP_ACTION_PERMIT;
    if (filter->flags & FWPS_FILTER_FLAG_CLEAR_ACTION_RIGHT) {
        classifyOut->rights &= ~FWPS_RIGHT_ACTION_WRITE;
    }

    DCSS_LOG_INFO("Redirected V4 flow: Process %I64u -> 127.0.0.1:%u (Original: 0x%08X:%u, Proto: %u)",
        inMetaValues->processId, DCSS_DEFAULT_REDIRECT_PORT, remoteIp, remotePort, protocol);
}

void NTAPI DcssClassifyConnectRedirectV6(
    const FWPS_INCOMING_VALUES0* inFixedValues,
    const FWPS_INCOMING_METADATA_VALUES0* inMetaValues,
    void* layerData,
    const void* classifyContext,
    const FWPS_FILTER3* filter,
    UINT64 flowContext,
    FWPS_CLASSIFY_OUT0* classifyOut)
{
    UNREFERENCED_PARAMETER(classifyContext);
    UNREFERENCED_PARAMETER(filter);
    UNREFERENCED_PARAMETER(flowContext);

    classifyOut->actionType = FWP_ACTION_PERMIT;
    if (layerData == NULL) return;

    FWPS_CONNECT_REQUEST0* connectRequest = (FWPS_CONNECT_REQUEST0*)layerData;

    FWPS_CONNECTION_REDIRECT_STATE redirectState = FwpsQueryConnectionRedirectState0(
        g_RedirectHandle,
        connectRequest,
        NULL);

    if (redirectState == FWPS_CONNECTION_REDIRECTED_BY_SELF ||
        redirectState == FWPS_CONNECTION_PREVIOUSLY_REDIRECTED_BY_SELF) {
        return;
    }

    FWP_BYTE_ARRAY16* remoteIp6 = inFixedValues->incomingValue[FWPS_FIELD_ALE_CONNECT_REDIRECT_V6_IP_REMOTE_ADDRESS].value.byteArray16;
    UINT16 remotePort = inFixedValues->incomingValue[FWPS_FIELD_ALE_CONNECT_REDIRECT_V6_IP_REMOTE_PORT].value.uint16;
    UINT8 protocol = inFixedValues->incomingValue[FWPS_FIELD_ALE_CONNECT_REDIRECT_V6_IP_PROTOCOL].value.uint8;

    PDCSS_REDIRECT_CONTEXT redirContext = (PDCSS_REDIRECT_CONTEXT)ExAllocatePool2(
        POOL_FLAG_PAGED,
        sizeof(DCSS_REDIRECT_CONTEXT),
        DCSS_WFP_POOL_TAG);

    if (redirContext == NULL) return;

    redirContext->OriginalDestinationIPv4 = 0;
    if (remoteIp6 != NULL) {
        RtlCopyMemory(redirContext->OriginalDestinationIPv6, remoteIp6->byteArray16, 16);
    }
    redirContext->OriginalDestinationPort = remotePort;
    redirContext->Protocol = protocol;
    redirContext->AddressFamily = AF_INET6;
    redirContext->ProcessId = (UINT32)inMetaValues->processId;

    SOCKADDR_IN6* localTarget = (SOCKADDR_IN6*)&connectRequest->remoteAddressAndPort;
    localTarget->sin6_family = AF_INET6;
    localTarget->sin6_port = RtlUshortByteSwap(DCSS_DEFAULT_REDIRECT_PORT);
    // ::1 loopback
    RtlZeroMemory(&localTarget->sin6_addr, sizeof(localTarget->sin6_addr));
    localTarget->sin6_addr.s6_addr[15] = 1;

    connectRequest->localRedirectHandle = g_RedirectHandle;
    connectRequest->localRedirectContext = redirContext;
    connectRequest->localRedirectContextSize = sizeof(DCSS_REDIRECT_CONTEXT);

    classifyOut->actionType = FWP_ACTION_PERMIT;
    if (filter->flags & FWPS_FILTER_FLAG_CLEAR_ACTION_RIGHT) {
        classifyOut->rights &= ~FWPS_RIGHT_ACTION_WRITE;
    }

    DCSS_LOG_INFO("Redirected V6 flow: Process %I64u -> [::1]:%u (Port: %u, Proto: %u)",
        inMetaValues->processId, DCSS_DEFAULT_REDIRECT_PORT, remotePort, protocol);
}

NTSTATUS NTAPI DcssNotifyConnectRedirect(
    FWPS_CALLOUT_NOTIFY_TYPE notifyType,
    const GUID* filterKey,
    FWPS_FILTER3* filter)
{
    UNREFERENCED_PARAMETER(notifyType);
    UNREFERENCED_PARAMETER(filterKey);
    UNREFERENCED_PARAMETER(filter);
    return STATUS_SUCCESS;
}
