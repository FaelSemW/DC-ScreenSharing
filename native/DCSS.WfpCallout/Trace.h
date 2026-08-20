#pragma once

#include <ntddk.h>

#define DCSS_LOG(Level, Format, ...) \
    DbgPrintEx(DPFLTR_IHVNETWORK_ID, Level, "[DCSS.WfpCallout] " Format "\n", ##__VA_ARGS__)

#define DCSS_LOG_INFO(Format, ...)  DCSS_LOG(DPFLTR_INFO_LEVEL, Format, ##__VA_ARGS__)
#define DCSS_LOG_WARN(Format, ...)  DCSS_LOG(DPFLTR_WARNING_LEVEL, Format, ##__VA_ARGS__)
#define DCSS_LOG_ERROR(Format, ...) DCSS_LOG(DPFLTR_ERROR_LEVEL, Format, ##__VA_ARGS__)
