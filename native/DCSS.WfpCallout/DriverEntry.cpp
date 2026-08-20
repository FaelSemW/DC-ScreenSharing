#include "Callout.h"

extern "C" DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_UNLOAD DcssDriverUnload;

static WDFDEVICE g_ControlDevice = NULL;

extern "C" NTSTATUS DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath)
{
    NTSTATUS status = STATUS_SUCCESS;
    WDF_DRIVER_CONFIG config;
    WDFDRIVER driver;
    PWDFDEVICE_INIT deviceInit = NULL;
    PDEVICE_OBJECT deviceObject = NULL;

    DCSS_LOG_INFO("Initializing DCSS.WfpCallout driver...");

    WDF_DRIVER_CONFIG_INIT(&config, WDF_NO_EVENT_CALLBACK);
    config.DriverInitFlags |= WdfDriverInitNonPnpDriver;
    config.EvtDriverUnload = DcssDriverUnload;

    status = WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, &driver);
    if (!NT_SUCCESS(status)) {
        DCSS_LOG_ERROR("WdfDriverCreate failed: 0x%08X", status);
        return status;
    }

    DECLARE_CONST_UNICODE_STRING(sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
    deviceInit = WdfControlDeviceInitAllocate(driver, &sddl);
    if (deviceInit == NULL) {
        DCSS_LOG_ERROR("WdfControlDeviceInitAllocate failed.");
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    DECLARE_CONST_UNICODE_STRING(ntDeviceName, L"\\Device\\DCSS.WfpCallout");
    status = WdfDeviceInitAssignName(deviceInit, &ntDeviceName);
    if (!NT_SUCCESS(status)) {
        DCSS_LOG_ERROR("WdfDeviceInitAssignName failed: 0x%08X", status);
        WdfDeviceInitFree(deviceInit);
        return status;
    }

    status = WdfDeviceCreate(&deviceInit, WDF_NO_OBJECT_ATTRIBUTES, &g_ControlDevice);
    if (!NT_SUCCESS(status)) {
        DCSS_LOG_ERROR("WdfDeviceCreate failed: 0x%08X", status);
        return status;
    }

    WdfControlFinishInitializing(g_ControlDevice);

    deviceObject = WdfDeviceWdmGetDeviceObject(g_ControlDevice);

    // Register WFP Callouts
    status = RegisterWfpCallouts(deviceObject);
    if (!NT_SUCCESS(status)) {
        DCSS_LOG_ERROR("RegisterWfpCallouts failed: 0x%08X", status);
        WdfObjectDelete(g_ControlDevice);
        g_ControlDevice = NULL;
        return status;
    }

    DCSS_LOG_INFO("DCSS.WfpCallout loaded and initialized successfully.");
    return STATUS_SUCCESS;
}

VOID DcssDriverUnload(_In_ WDFDRIVER Driver)
{
    UNREFERENCED_PARAMETER(Driver);
    DCSS_LOG_INFO("Unloading DCSS.WfpCallout driver...");
    UnregisterWfpCallouts();
    if (g_ControlDevice != NULL) {
        WdfObjectDelete(g_ControlDevice);
        g_ControlDevice = NULL;
    }
    DCSS_LOG_INFO("DCSS.WfpCallout unloaded successfully.");
}

