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
constexpr uint16_t kVersion = 1;
constexpr USHORT kVendor = 0x0B05;
constexpr USHORT kProduct = 0x1B4C;
constexpr size_t kMinReport = 50;
constexpr size_t kMaxReport = 64;
constexpr uint8_t kRearMappingCommand = 0xD1;
constexpr size_t kQueueCapacity = 256;
constexpr wchar_t kConfigSuffix[] = L".config";

enum class Api : uint8_t { HidDSetFeature = 1, KernelBaseWriteFile = 2, Overflow = 0xFF };
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
using WriteFileFn = BOOL(WINAPI*)(HANDLE, LPCVOID, DWORD, LPDWORD, LPOVERLAPPED);
HidDSetFeatureFn g_originalSetFeature = nullptr;
WriteFileFn g_originalWriteFile = nullptr;
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

class CallbackLease {
public:
    CallbackLease() { g_activeCallbacks.fetch_add(1, std::memory_order_acq_rel); }
    ~CallbackLease() { g_activeCallbacks.fetch_sub(1, std::memory_order_acq_rel); }
};

bool IsTargetHandle(HANDLE handle) {
    HIDD_ATTRIBUTES attributes{};
    attributes.Size = sizeof(attributes);
    return handle != nullptr && handle != INVALID_HANDLE_VALUE &&
        HidD_GetAttributes(handle, &attributes) &&
        attributes.VendorID == kVendor && attributes.ProductID == kProduct;
}

bool PrepareRecord(Api api, HANDLE handle, const void* buffer, size_t length, WireRecord& record) {
    if (g_stopping.load(std::memory_order_relaxed) || buffer == nullptr ||
        length < kMinReport || length > kMaxReport) return false;
    std::array<uint8_t, kMaxReport> copy{};
    SIZE_T bytesRead = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), buffer, copy.data(), length, &bytesRead) ||
        bytesRead != length || copy[0] != 0x5A || copy[1] != kRearMappingCommand ||
        !IsTargetHandle(handle)) return false;

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
        g_droppedRecords.fetch_add(1, std::memory_order_relaxed);
    }
    LeaveCriticalSection(&g_queueLock);
}

BOOLEAN __stdcall HookSetFeature(HANDLE handle, PVOID buffer, ULONG length) {
    CallbackLease lease;
    WireRecord record{};
    const DWORD incomingError = GetLastError();
    const bool retain = PrepareRecord(Api::HidDSetFeature, handle, buffer, length, record);
    SetLastError(incomingError);
    const BOOLEAN result = g_originalSetFeature(handle, buffer, length);
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
    auto writeFile = reinterpret_cast<LPVOID>(GetProcAddress(kernelBase, "WriteFile"));
    if (!setFeature) return fail(4, GetLastError());
    if (!writeFile) return fail(5, GetLastError());

    auto status = MH_CreateHook(setFeature, reinterpret_cast<void*>(&HookSetFeature),
        reinterpret_cast<void**>(&g_originalSetFeature));
    if (status != MH_OK) return fail(6, static_cast<DWORD>(status));
    status = MH_CreateHook(writeFile, reinterpret_cast<void*>(&HookWriteFile),
        reinterpret_cast<void**>(&g_originalWriteFile));
    if (status != MH_OK) return fail(7, static_cast<DWORD>(status));
    status = MH_EnableHook(setFeature);
    if (status != MH_OK) return fail(8, static_cast<DWORD>(status));
    status = MH_EnableHook(writeFile);
    if (status != MH_OK) return fail(9, static_cast<DWORD>(status));
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
    if (g_loadedHidModule) {
        if (!FreeLibrary(g_loadedHidModule)) return false;
        g_loadedHidModule = nullptr;
    }
    return true;
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
        DWORD failureWritten = 0;
        WriteFile(g_pipe, &failure, sizeof(failure), &failureWritten, nullptr);
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
    DWORD readyWritten = 0;
    if (!WriteFile(g_pipe, &ready, sizeof(ready), &readyWritten, nullptr) || readyWritten != sizeof(ready)) {
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
            DWORD written = 0;
            if (!WriteFile(g_pipe, &record, sizeof(record), &written, nullptr) || written != sizeof(record)) {
                transportFailure = true;
                SetEvent(g_stopEvent);
                break;
            }
        }
    }
    if (!DisableHooksAndDrain()) return 6;
    WireRecord record{};
    while (Pop(record)) {
        DWORD written = 0;
        if (!WriteFile(g_pipe, &record, sizeof(record), &written, nullptr) || written != sizeof(record)) {
            transportFailure = true;
            break;
        }
    }
    const uint32_t dropped = g_droppedRecords.load(std::memory_order_relaxed);
    if (dropped != 0) {
        WireRecord overflow{};
        overflow.magic = kMagic;
        overflow.version = kVersion;
        overflow.api = static_cast<uint8_t>(Api::Overflow);
        overflow.processId = GetCurrentProcessId();
        overflow.apiResult = dropped;
        memcpy(overflow.token, g_token.data(), g_token.size());
        DWORD written = 0;
        if (!WriteFile(g_pipe, &overflow, sizeof(overflow), &written, nullptr) || written != sizeof(overflow))
            transportFailure = true;
    }
    if (!FlushFileBuffers(g_pipe)) transportFailure = true;
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
