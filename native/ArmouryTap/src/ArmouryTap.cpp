#include <windows.h>
#include <hidsdi.h>
#include <array>
#include <atomic>
#include <cstdlib>
#include <cstdint>
#include <cwchar>
#include <string>
#include <string_view>
#include "MinHook.h"

namespace {
constexpr uint32_t kMagic = 0x31544241;
constexpr uint16_t kVersion = 2;
constexpr USHORT kVendor = 0x0B05;
constexpr USHORT kProduct = 0x1B4C;
constexpr size_t kMinReport = 50;
constexpr size_t kMaxReport = 64;
constexpr uint8_t kRearMappingCommand = 0xD1;
constexpr size_t kQueueCapacity = 256;
constexpr size_t kMaximumValidatedHandles = 16;
constexpr size_t kMaximumInspectedLength = 4096;
constexpr uint32_t kCounterMaximum = 1'000'000;
constexpr DWORD kIoctlHidSetFeature = 0x000B0191;
constexpr DWORD kIoctlHidSetOutputReport = 0x000B0195;
constexpr wchar_t kConfigSuffix[] = L".config";

enum class Api : uint8_t {
    HidDSetFeature = 1, KernelBaseWriteFile = 2, HidDSetOutputReport = 3,
    DeviceIoControlSetFeature = 4, DeviceIoControlSetOutputReport = 5,
    Summary = 0xFE
};
#pragma pack(push, 1)
struct WireRecord {
    uint32_t magic;
    uint16_t version;
    uint8_t api;
    uint8_t reportLength;
    uint32_t processId;
    int64_t qpc;
    uint32_t apiResult;
    uint32_t lastError;
    uint8_t token[32];
    uint8_t report[64];
};
#pragma pack(pop)
static_assert(sizeof(WireRecord) == 124);

using HidDSetFeatureFn = BOOLEAN(__stdcall*)(HANDLE, PVOID, ULONG);
using HidDSetOutputReportFn = BOOLEAN(__stdcall*)(HANDLE, PVOID, ULONG);
using WriteFileFn = BOOL(WINAPI*)(HANDLE, LPCVOID, DWORD, LPDWORD, LPOVERLAPPED);
using DeviceIoControlFn = BOOL(WINAPI*)(HANDLE, DWORD, LPVOID, DWORD, LPVOID, DWORD, LPDWORD, LPOVERLAPPED);
using CompareObjectHandlesFn = BOOL(WINAPI*)(HANDLE, HANDLE);
HidDSetFeatureFn g_originalSetFeature = nullptr;
HidDSetOutputReportFn g_originalSetOutputReport = nullptr;
WriteFileFn g_originalWriteFile = nullptr;
DeviceIoControlFn g_originalDeviceIoControl = nullptr;
CompareObjectHandlesFn g_compareObjectHandles = nullptr;
HANDLE g_stopEvent = nullptr;
HANDLE g_queueEvent = nullptr;
HANDLE g_worker = nullptr;
HANDLE g_pipe = INVALID_HANDLE_VALUE;
HANDLE g_helperProcess = nullptr;
HMODULE g_loadedHidModule = nullptr;
bool g_minHookInitialized = false;
uint32_t g_hookFailureStage = 0;
DWORD g_hookFailureDetail = 0;
CRITICAL_SECTION g_queueLock{};
std::array<WireRecord, kQueueCapacity> g_queue{};
size_t g_head = 0;
size_t g_tail = 0;
size_t g_count = 0;
std::array<uint8_t, 32> g_token{};
std::wstring g_pipeName;
std::atomic<bool> g_stopping{false};
std::atomic<uint32_t> g_activeCallbacks{0};
std::atomic<uint32_t> g_droppedRecords{0};
std::array<std::atomic<uint32_t>, 5> g_apiCalls{};
std::atomic<uint32_t> g_invalidHandle{0}, g_attributeReadFailure{0};
std::atomic<uint32_t> g_nonAsusDevice{0}, g_otherAsusProduct{0};
std::atomic<uint32_t> g_unvalidatedWriteHandle{0};
std::atomic<uint32_t> g_underLength{0}, g_boundedLength{0}, g_overLength{0};
std::atomic<uint32_t> g_unreadableBuffer{0}, g_reportId5A{0}, g_prefix5AD1{0}, g_retained{0};
std::atomic<bool> g_counterSaturated{false};
thread_local uint32_t g_hidWrapperDepth = 0;
thread_local uint32_t g_internalIoDepth = 0;
SRWLOCK g_validatedHandleLock = SRWLOCK_INIT;
std::array<HANDLE, kMaximumValidatedHandles> g_validatedHandles{};
size_t g_validatedHandleCount = 0;

class CallbackLease {
public:
    CallbackLease() { g_activeCallbacks.fetch_add(1, std::memory_order_acq_rel); }
    ~CallbackLease() { g_activeCallbacks.fetch_sub(1, std::memory_order_acq_rel); }
};

void SaturatingIncrement(std::atomic<uint32_t>& counter) {
    uint32_t current = counter.load(std::memory_order_relaxed);
    while (current < kCounterMaximum &&
        !counter.compare_exchange_weak(current, current + 1, std::memory_order_relaxed)) {}
    if (current >= kCounterMaximum) g_counterSaturated.store(true, std::memory_order_relaxed);
}

enum class HandleClassification { Target, Invalid, AttributeReadFailure, NonAsusDevice, OtherAsusProduct };

HandleClassification ClassifyHandle(HANDLE handle) {
    if (handle == nullptr || handle == INVALID_HANDLE_VALUE) return HandleClassification::Invalid;
    HIDD_ATTRIBUTES attributes{};
    attributes.Size = sizeof(attributes);
    if (!HidD_GetAttributes(handle, &attributes)) return HandleClassification::AttributeReadFailure;
    if (attributes.VendorID != kVendor) return HandleClassification::NonAsusDevice;
    if (attributes.ProductID != kProduct) return HandleClassification::OtherAsusProduct;
    return HandleClassification::Target;
}

bool IsKnownTargetHandle(HANDLE candidate) {
    if (!g_compareObjectHandles || candidate == nullptr || candidate == INVALID_HANDLE_VALUE) return false;
    AcquireSRWLockShared(&g_validatedHandleLock);
    bool found = false;
    for (size_t index = 0; index < g_validatedHandleCount && !found; ++index)
        found = g_compareObjectHandles(candidate, g_validatedHandles[index]) != FALSE;
    ReleaseSRWLockShared(&g_validatedHandleLock);
    return found;
}

void RememberTargetHandle(HANDLE candidate) {
    if (!g_compareObjectHandles || candidate == nullptr || candidate == INVALID_HANDLE_VALUE) return;
    AcquireSRWLockExclusive(&g_validatedHandleLock);
    for (size_t index = 0; index < g_validatedHandleCount; ++index) {
        if (g_compareObjectHandles(candidate, g_validatedHandles[index]) != FALSE) {
            ReleaseSRWLockExclusive(&g_validatedHandleLock);
            return;
        }
    }
    HANDLE duplicate = nullptr;
    if (g_validatedHandleCount < g_validatedHandles.size() &&
        DuplicateHandle(GetCurrentProcess(), candidate, GetCurrentProcess(), &duplicate,
            0, FALSE, DUPLICATE_SAME_ACCESS)) {
        g_validatedHandles[g_validatedHandleCount++] = duplicate;
    }
    ReleaseSRWLockExclusive(&g_validatedHandleLock);
}

bool ReleaseValidatedHandles() {
    AcquireSRWLockExclusive(&g_validatedHandleLock);
    size_t failures = 0;
    for (size_t index = 0; index < g_validatedHandleCount; ++index) {
        const HANDLE handle = g_validatedHandles[index];
        if (!CloseHandle(handle)) g_validatedHandles[failures++] = handle;
    }
    for (size_t index = failures; index < g_validatedHandleCount; ++index)
        g_validatedHandles[index] = nullptr;
    g_validatedHandleCount = failures;
    ReleaseSRWLockExclusive(&g_validatedHandleLock);
    return failures == 0;
}

bool PrepareRecord(Api api, HANDLE handle, const void* buffer, size_t length, WireRecord& record) {
    if (g_stopping.load(std::memory_order_relaxed)) return false;
    const auto apiIndex = static_cast<size_t>(api) - 1;
    if (apiIndex >= g_apiCalls.size()) return false;
    SaturatingIncrement(g_apiCalls[apiIndex]);
    if (length < kMinReport) { SaturatingIncrement(g_underLength); return false; }
    if (length > kMaxReport || length > kMaximumInspectedLength) { SaturatingIncrement(g_overLength); return false; }
    SaturatingIncrement(g_boundedLength);
    if (buffer == nullptr) { SaturatingIncrement(g_unreadableBuffer); return false; }
    std::array<uint8_t, kMaxReport> copy{};
    SIZE_T bytesRead = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), buffer, copy.data(), length, &bytesRead) || bytesRead != length) {
        SaturatingIncrement(g_unreadableBuffer);
        return false;
    }
    if (copy[0] != 0x5A) return false;
    SaturatingIncrement(g_reportId5A);
    if (api == Api::KernelBaseWriteFile) {
        if (!IsKnownTargetHandle(handle)) {
            SaturatingIncrement(g_unvalidatedWriteHandle);
            return false;
        }
    } else {
        switch (ClassifyHandle(handle)) {
            case HandleClassification::Invalid: SaturatingIncrement(g_invalidHandle); return false;
            case HandleClassification::AttributeReadFailure: SaturatingIncrement(g_attributeReadFailure); return false;
            case HandleClassification::NonAsusDevice: SaturatingIncrement(g_nonAsusDevice); return false;
            case HandleClassification::OtherAsusProduct: SaturatingIncrement(g_otherAsusProduct); return false;
            case HandleClassification::Target: RememberTargetHandle(handle); break;
        }
    }
    if (copy[1] != kRearMappingCommand) return false;
    SaturatingIncrement(g_prefix5AD1);

    record = {};
    record.magic = kMagic;
    record.version = kVersion;
    record.api = static_cast<uint8_t>(api);
    record.reportLength = static_cast<uint8_t>(length);
    record.processId = GetCurrentProcessId();
    LARGE_INTEGER qpc{};
    QueryPerformanceCounter(&qpc);
    record.qpc = qpc.QuadPart;
    memcpy(record.token, g_token.data(), g_token.size());
    memcpy(record.report, copy.data(), length);
    SaturatingIncrement(g_retained);
    return true;
}

void Enqueue(const WireRecord& record) {
    EnterCriticalSection(&g_queueLock);
    if (g_count < kQueueCapacity) {
        g_queue[g_tail] = record;
        g_tail = (g_tail + 1) % kQueueCapacity;
        ++g_count;
        SetEvent(g_queueEvent);
    } else {
        SaturatingIncrement(g_droppedRecords);
    }
    LeaveCriticalSection(&g_queueLock);
}

BOOLEAN __stdcall HookSetFeature(HANDLE handle, PVOID buffer, ULONG length) {
    CallbackLease lease;
    WireRecord record{};
    const DWORD incomingError = GetLastError();
    const bool retain = PrepareRecord(Api::HidDSetFeature, handle, buffer, length, record);
    SetLastError(incomingError);
    ++g_hidWrapperDepth;
    const BOOLEAN result = g_originalSetFeature(handle, buffer, length);
    --g_hidWrapperDepth;
    const DWORD error = GetLastError();
    if (retain) {
        record.apiResult = result != FALSE ? 1u : 0u;
        record.lastError = error;
        Enqueue(record);
    }
    SetLastError(error);
    return result;
}

BOOLEAN __stdcall HookSetOutputReport(HANDLE handle, PVOID buffer, ULONG length) {
    CallbackLease lease;
    WireRecord record{};
    const DWORD incomingError = GetLastError();
    const bool retain = PrepareRecord(Api::HidDSetOutputReport, handle, buffer, length, record);
    SetLastError(incomingError);
    ++g_hidWrapperDepth;
    const BOOLEAN result = g_originalSetOutputReport(handle, buffer, length);
    --g_hidWrapperDepth;
    const DWORD error = GetLastError();
    if (retain) {
        record.apiResult = result != FALSE ? 1u : 0u;
        record.lastError = error;
        Enqueue(record);
    }
    SetLastError(error);
    return result;
}

BOOL WINAPI HookWriteFile(HANDLE handle, LPCVOID buffer, DWORD bytesToWrite,
    LPDWORD bytesWritten, LPOVERLAPPED overlapped) {
    if (g_internalIoDepth != 0 || g_hidWrapperDepth != 0)
        return g_originalWriteFile(handle, buffer, bytesToWrite, bytesWritten, overlapped);
    CallbackLease lease;
    WireRecord record{};
    const DWORD incomingError = GetLastError();
    const bool retain = PrepareRecord(Api::KernelBaseWriteFile, handle, buffer, bytesToWrite, record);
    SetLastError(incomingError);
    const BOOL result = g_originalWriteFile(handle, buffer, bytesToWrite, bytesWritten, overlapped);
    const DWORD error = GetLastError();
    if (retain) {
        record.apiResult = result != FALSE ? 1u : 0u;
        record.lastError = error;
        Enqueue(record);
    }
    SetLastError(error);
    return result;
}

BOOL WINAPI HookDeviceIoControl(HANDLE handle, DWORD controlCode, LPVOID inputBuffer,
    DWORD inputLength, LPVOID outputBuffer, DWORD outputLength, LPDWORD bytesReturned,
    LPOVERLAPPED overlapped) {
    CallbackLease lease;
    const bool allowed = controlCode == kIoctlHidSetFeature || controlCode == kIoctlHidSetOutputReport;
    if (!allowed || g_hidWrapperDepth != 0)
        return g_originalDeviceIoControl(handle, controlCode, inputBuffer, inputLength,
            outputBuffer, outputLength, bytesReturned, overlapped);
    WireRecord record{};
    const DWORD incomingError = GetLastError();
    const Api api = controlCode == kIoctlHidSetFeature
        ? Api::DeviceIoControlSetFeature : Api::DeviceIoControlSetOutputReport;
    const bool retain = PrepareRecord(api, handle, inputBuffer, inputLength, record);
    SetLastError(incomingError);
    const BOOL result = g_originalDeviceIoControl(handle, controlCode, inputBuffer, inputLength,
        outputBuffer, outputLength, bytesReturned, overlapped);
    const DWORD error = GetLastError();
    if (retain) {
        record.apiResult = result != FALSE ? 1u : 0u;
        record.lastError = error;
        Enqueue(record);
    }
    SetLastError(error);
    return result;
}

bool WritePipeRecord(const WireRecord& record) {
    DWORD written = 0;
    ++g_internalIoDepth;
    const BOOL result = WriteFile(g_pipe, &record, sizeof(record), &written, nullptr);
    --g_internalIoDepth;
    return result != FALSE && written == sizeof(record);
}

bool ParseHexToken(const std::string& text) {
    if (text.size() != 64) return false;
    for (size_t index = 0; index < g_token.size(); ++index) {
        char pair[3]{text[index * 2], text[index * 2 + 1], 0};
        char* end = nullptr;
        const auto value = strtoul(pair, &end, 16);
        if (end != pair + 2 || value > 0xFF) return false;
        g_token[index] = static_cast<uint8_t>(value);
    }
    return true;
}

bool LoadConfig(HMODULE module) {
    wchar_t modulePath[MAX_PATH]{};
    if (GetModuleFileNameW(module, modulePath, MAX_PATH) == 0) return false;
    const std::wstring configPath = std::wstring(modulePath) + kConfigSuffix;
    HANDLE file = CreateFileW(configPath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > 1024) {
        CloseHandle(file);
        return false;
    }
    std::string text(static_cast<size_t>(size.QuadPart), '\0');
    size_t totalRead = 0;
    while (totalRead < text.size()) {
        DWORD chunkRead = 0;
        const DWORD requested = static_cast<DWORD>(text.size() - totalRead);
        if (!ReadFile(file, text.data() + totalRead, requested, &chunkRead, nullptr) || chunkRead == 0) {
            CloseHandle(file);
            return false;
        }
        totalRead += chunkRead;
    }
    CloseHandle(file);
    for (const unsigned char value : text)
        if (value != '\r' && value != '\n' && (value < 0x20 || value > 0x7E)) return false;
    const size_t firstEnd = text.find('\n');
    if (firstEnd == std::string::npos) return false;
    const size_t secondEnd = text.find('\n', firstEnd + 1);
    if (secondEnd == std::string::npos) return false;
    const size_t thirdEnd = text.find('\n', secondEnd + 1);
    std::string pipeLine = text.substr(0, firstEnd);
    std::string tokenLine = text.substr(firstEnd + 1, secondEnd - firstEnd - 1);
    std::string helperLine = text.substr(secondEnd + 1,
        thirdEnd == std::string::npos ? std::string::npos : thirdEnd - secondEnd - 1);
    if (!pipeLine.empty() && pipeLine.back() == '\r') pipeLine.pop_back();
    if (!tokenLine.empty() && tokenLine.back() == '\r') tokenLine.pop_back();
    if (!helperLine.empty() && helperLine.back() == '\r') helperLine.pop_back();
    if (thirdEnd != std::string::npos && text.find_first_not_of("\r\n", thirdEnd) != std::string::npos) return false;
    constexpr std::string_view pipePrefix = "pipe=";
    constexpr std::string_view tokenPrefix = "token=";
    constexpr std::string_view helperPrefix = "helper=";
    if (!pipeLine.starts_with(pipePrefix) || !tokenLine.starts_with(tokenPrefix) ||
        !helperLine.starts_with(helperPrefix)) return false;
    char* helperEnd = nullptr;
    const auto helperPid = strtoul(helperLine.c_str() + helperPrefix.size(), &helperEnd, 10);
    if (!helperEnd || *helperEnd != '\0' || helperPid == 0 || helperPid > MAXDWORD) return false;
    const std::string pipeName = pipeLine.substr(pipePrefix.size());
    g_pipeName.assign(pipeName.begin(), pipeName.end());
    g_helperProcess = OpenProcess(SYNCHRONIZE, FALSE, static_cast<DWORD>(helperPid));
    return !g_pipeName.empty() && g_helperProcess != nullptr &&
        ParseHexToken(tokenLine.substr(tokenPrefix.size()));
}

bool InstallHooks() {
    const auto fail = [&](uint32_t stage, DWORD detail) {
        g_hookFailureStage = stage;
        g_hookFailureDetail = detail;
        return false;
    };

    const auto initializeStatus = MH_Initialize();
    if (initializeStatus != MH_OK) return fail(1, static_cast<DWORD>(initializeStatus));
    g_minHookInitialized = true;

    HMODULE hid = GetModuleHandleW(L"hid.dll");
    if (!hid) {
        hid = LoadLibraryExW(L"hid.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!hid) return fail(2, GetLastError());
        g_loadedHidModule = hid;
    }
    HMODULE kernelBase = GetModuleHandleW(L"KernelBase.dll");
    if (!kernelBase) return fail(3, GetLastError());
    auto setFeature = reinterpret_cast<LPVOID>(GetProcAddress(hid, "HidD_SetFeature"));
    auto setOutputReport = reinterpret_cast<LPVOID>(GetProcAddress(hid, "HidD_SetOutputReport"));
    auto writeFile = reinterpret_cast<LPVOID>(GetProcAddress(kernelBase, "WriteFile"));
    auto deviceIoControl = reinterpret_cast<LPVOID>(GetProcAddress(kernelBase, "DeviceIoControl"));
    const auto compareObjectHandles = reinterpret_cast<LPVOID>(
        GetProcAddress(kernelBase, "CompareObjectHandles"));
    static_assert(sizeof(g_compareObjectHandles) == sizeof(compareObjectHandles));
    memcpy(&g_compareObjectHandles, &compareObjectHandles, sizeof(g_compareObjectHandles));
    if (!setFeature) return fail(4, GetLastError());
    if (!setOutputReport) return fail(5, GetLastError());
    if (!writeFile) return fail(6, GetLastError());
    if (!deviceIoControl) return fail(7, GetLastError());
    if (!g_compareObjectHandles) return fail(8, GetLastError());

    auto status = MH_CreateHook(setFeature, reinterpret_cast<void*>(&HookSetFeature),
        reinterpret_cast<void**>(&g_originalSetFeature));
    if (status != MH_OK) return fail(9, static_cast<DWORD>(status));
    status = MH_CreateHook(setOutputReport, reinterpret_cast<void*>(&HookSetOutputReport),
        reinterpret_cast<void**>(&g_originalSetOutputReport));
    if (status != MH_OK) return fail(10, static_cast<DWORD>(status));
    status = MH_CreateHook(writeFile, reinterpret_cast<void*>(&HookWriteFile),
        reinterpret_cast<void**>(&g_originalWriteFile));
    if (status != MH_OK) return fail(11, static_cast<DWORD>(status));
    status = MH_CreateHook(deviceIoControl, reinterpret_cast<void*>(&HookDeviceIoControl),
        reinterpret_cast<void**>(&g_originalDeviceIoControl));
    if (status != MH_OK) return fail(12, static_cast<DWORD>(status));
    for (const auto hook : {setFeature, setOutputReport, writeFile, deviceIoControl}) {
        status = MH_QueueEnableHook(hook);
        if (status != MH_OK) return fail(13, static_cast<DWORD>(status));
    }
    status = MH_ApplyQueued();
    if (status != MH_OK) return fail(14, static_cast<DWORD>(status));
    return true;
}

bool Pop(WireRecord& record) {
    EnterCriticalSection(&g_queueLock);
    const bool present = g_count != 0;
    if (present) {
        record = g_queue[g_head];
        g_head = (g_head + 1) % kQueueCapacity;
        --g_count;
    }
    if (g_count == 0) ResetEvent(g_queueEvent);
    LeaveCriticalSection(&g_queueLock);
    return present;
}

bool DisableHooksAndDrain() {
    g_stopping.store(true, std::memory_order_release);
    if (g_minHookInitialized) {
        if (MH_DisableHook(MH_ALL_HOOKS) != MH_OK) return false;
        for (int attempt = 0; attempt < 200 && g_activeCallbacks.load(std::memory_order_acquire) != 0; ++attempt) {
            Sleep(10);
        }
        if (g_activeCallbacks.load(std::memory_order_acquire) != 0) return false;
        if (MH_Uninitialize() != MH_OK) return false;
        g_minHookInitialized = false;
    }
    if (!ReleaseValidatedHandles()) return false;
    if (g_loadedHidModule) {
        if (!FreeLibrary(g_loadedHidModule)) return false;
        g_loadedHidModule = nullptr;
    }
    return true;
}

WireRecord BuildSummaryRecord() {
    WireRecord summary{};
    summary.magic = kMagic;
    summary.version = kVersion;
    summary.api = static_cast<uint8_t>(Api::Summary);
    summary.processId = GetCurrentProcessId();
    const uint64_t lowApiCounts = g_apiCalls[0].load(std::memory_order_relaxed);
    const uint64_t highApiCounts = g_apiCalls[1].load(std::memory_order_relaxed);
    summary.qpc = static_cast<int64_t>(lowApiCounts | (highApiCounts << 32));
    summary.apiResult = g_apiCalls[2].load(std::memory_order_relaxed);
    summary.lastError = static_cast<int32_t>(g_apiCalls[3].load(std::memory_order_relaxed));
    memcpy(summary.token, g_token.data(), g_token.size());
    summary.report[0] = 2; // Summary schema version.
    summary.report[1] = g_counterSaturated.load(std::memory_order_relaxed) ? 1 : 0;
    const std::array<uint32_t, 14> values{
        g_apiCalls[4].load(), g_invalidHandle.load(), g_attributeReadFailure.load(),
        g_nonAsusDevice.load(), g_otherAsusProduct.load(), g_unvalidatedWriteHandle.load(),
        g_underLength.load(), g_boundedLength.load(), g_overLength.load(), g_unreadableBuffer.load(),
        g_reportId5A.load(), g_prefix5AD1.load(), g_retained.load(), g_droppedRecords.load()
    };
    memcpy(summary.report + 4, values.data(), values.size() * sizeof(uint32_t));
    return summary;
}

DWORD WINAPI WorkerMain(void* parameter) {
    const auto module = static_cast<HMODULE>(parameter);
    if (!LoadConfig(module)) return 2;
    g_pipe = CreateFileW(g_pipeName.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (g_pipe == INVALID_HANDLE_VALUE) return 3;
    if (!InstallHooks()) {
        const bool hooksRemoved = DisableHooksAndDrain();
        WireRecord failure{};
        failure.magic = kMagic;
        failure.version = kVersion;
        failure.processId = GetCurrentProcessId();
        failure.apiResult = g_hookFailureStage;
        failure.lastError = g_hookFailureDetail;
        memcpy(failure.token, g_token.data(), g_token.size());
        WritePipeRecord(failure);
        FlushFileBuffers(g_pipe);
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
        return hooksRemoved ? 4 : 6;
    }
    WireRecord ready{};
    ready.magic = kMagic;
    ready.version = kVersion;
    ready.processId = GetCurrentProcessId();
    memcpy(ready.token, g_token.data(), g_token.size());
    if (!WritePipeRecord(ready)) {
        const bool hooksRemoved = DisableHooksAndDrain();
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
        return hooksRemoved ? 5 : 6;
    }

    bool transportFailure = false;
    bool helperExited = false;
    HANDLE waits[]{g_stopEvent, g_queueEvent, g_helperProcess};
    for (;;) {
        const DWORD wait = WaitForMultipleObjects(3, waits, FALSE, INFINITE);
        if (wait == WAIT_OBJECT_0) break;
        if (wait == WAIT_OBJECT_0 + 2) { helperExited = true; break; }
        if (wait != WAIT_OBJECT_0 + 1) { transportFailure = true; break; }
        WireRecord record{};
        while (Pop(record)) {
            if (!WritePipeRecord(record)) {
                transportFailure = true;
                SetEvent(g_stopEvent);
                break;
            }
        }
    }
    if (!DisableHooksAndDrain()) return 6;
    WireRecord record{};
    while (Pop(record)) {
        if (!WritePipeRecord(record)) {
            transportFailure = true;
            break;
        }
    }
    const auto summary = BuildSummaryRecord();
    if (!WritePipeRecord(summary)) transportFailure = true;

    CloseHandle(g_pipe);
    g_pipe = INVALID_HANDLE_VALUE;
    const DWORD exitCode = transportFailure ? 7 : 0;
    if (helperExited) FreeLibraryAndExitThread(module, exitCode);
    return exitCode;
}
}

extern "C" __declspec(dllexport) DWORD WINAPI ArmouryTapStop(void*) {
    if (!g_stopEvent || !g_worker) return 0;
    SetEvent(g_stopEvent);
    if (WaitForSingleObject(g_worker, 10'000) != WAIT_OBJECT_0) return 0;
    DWORD workerExitCode = STILL_ACTIVE;
    if (!GetExitCodeThread(g_worker, &workerExitCode)) return 0;
    switch (workerExitCode) {
        case 0: // Normal stop.
        case 2: // Config rejected before hook initialization.
        case 3: // Pipe rejected before hook initialization.
        case 4: // Hook startup failed but checked rollback succeeded.
        case 5: // Ready transport failed but checked rollback succeeded.
        case 7: // Runtime transport failed after checked rollback.
            return 1;
        default:
            // Exit 6 and every unknown status are unsafe to unload.
            return 0;
    }
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        InitializeCriticalSection(&g_queueLock);
        g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        g_queueEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!g_stopEvent || !g_queueEvent) return FALSE;
        g_worker = CreateThread(nullptr, 0, WorkerMain, instance, 0, nullptr);
        return g_worker != nullptr;
    }
    if (reason == DLL_PROCESS_DETACH) {
        g_stopping.store(true, std::memory_order_relaxed);
        if (g_stopEvent) SetEvent(g_stopEvent);
        if (g_worker) CloseHandle(g_worker);
        if (g_helperProcess) CloseHandle(g_helperProcess);
        if (g_queueEvent) CloseHandle(g_queueEvent);
        if (g_stopEvent) CloseHandle(g_stopEvent);
        DeleteCriticalSection(&g_queueLock);
    }
    return TRUE;
}
