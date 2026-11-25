// version.dll (full) — RiotClientServices.exe 的本地劫持 DLL
//
// 1) 转发 System32\version.dll 的 15 个导出
// 2) IAT hook CreateFileW / CreateFile2
// 3) inline hook ntdll!NtCreateFile（客户端读 product_settings.yaml 走的是这条）
// 命中 product_settings.yaml 的读请求时，返回一份 vanguard: true -> false 的临时副本。

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <wchar.h>

#ifndef FILE_OPEN
#define FILE_OPEN 0x00000001
#endif

// ===== 1. version.dll 转发 =====
static HMODULE g_realVersion = NULL;
static HMODULE RealVersion(void)
{
    if (!g_realVersion)
        g_realVersion = LoadLibraryW(L"C:\\Windows\\System32\\version.dll");
    return g_realVersion;
}

#define FWD_BOOL(name, argdecl, callargs)                       \
    BOOL WINAPI name argdecl                                    \
    {                                                           \
        typedef BOOL (WINAPI * FN) argdecl;                     \
        static FN fn = NULL;                                    \
        if (!fn) fn = (FN)GetProcAddress(RealVersion(), #name);  \
        return fn ? fn callargs : FALSE;                        \
    }

FWD_BOOL(GetFileVersionInfoA, (LPCSTR a, DWORD b, DWORD c, LPVOID d), (a, b, c, d))
FWD_BOOL(GetFileVersionInfoW, (LPCWSTR a, DWORD b, DWORD c, LPVOID d), (a, b, c, d))
FWD_BOOL(GetFileVersionInfoExA, (DWORD f, LPCSTR a, DWORD b, DWORD c, LPVOID d), (f, a, b, c, d))
FWD_BOOL(GetFileVersionInfoExW, (DWORD f, LPCWSTR a, DWORD b, DWORD c, LPVOID d), (f, a, b, c, d))
FWD_BOOL(GetFileVersionInfoSizeA, (LPCSTR a, LPDWORD b), (a, b))
FWD_BOOL(GetFileVersionInfoSizeW, (LPCWSTR a, LPDWORD b), (a, b))
FWD_BOOL(GetFileVersionInfoSizeExA, (DWORD f, LPCSTR a, LPDWORD b), (f, a, b))
FWD_BOOL(GetFileVersionInfoSizeExW, (DWORD f, LPCWSTR a, LPDWORD b), (f, a, b))
FWD_BOOL(GetFileVersionInfoByHandle, (DWORD a, HANDLE b, DWORD c, LPVOID d), (a, b, c, d))
FWD_BOOL(VerQueryValueA, (LPCVOID a, LPCSTR b, LPVOID* c, PUINT d), (a, b, c, d))
FWD_BOOL(VerQueryValueW, (LPCVOID a, LPCWSTR b, LPVOID* c, PUINT d), (a, b, c, d))

DWORD WINAPI VerFindFileA(DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPSTR e, PUINT f, LPSTR g, PUINT h)
{
    typedef DWORD (WINAPI * FN)(DWORD, LPCSTR, LPCSTR, LPCSTR, LPSTR, PUINT, LPSTR, PUINT);
    static FN fn = NULL;
    if (!fn) fn = (FN)GetProcAddress(RealVersion(), "VerFindFileA");
    return fn ? fn(a, b, c, d, e, f, g, h) : 0;
}
DWORD WINAPI VerFindFileW(DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPWSTR e, PUINT f, LPWSTR g, PUINT h)
{
    typedef DWORD (WINAPI * FN)(DWORD, LPCWSTR, LPCWSTR, LPCWSTR, LPWSTR, PUINT, LPWSTR, PUINT);
    static FN fn = NULL;
    if (!fn) fn = (FN)GetProcAddress(RealVersion(), "VerFindFileW");
    return fn ? fn(a, b, c, d, e, f, g, h) : 0;
}
DWORD WINAPI VerInstallFileA(DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPCSTR e, LPSTR f, PUINT g, PUINT h)
{
    typedef DWORD (WINAPI * FN)(DWORD, LPCSTR, LPCSTR, LPCSTR, LPCSTR, LPSTR, PUINT, PUINT);
    static FN fn = NULL;
    if (!fn) fn = (FN)GetProcAddress(RealVersion(), "VerInstallFileA");
    return fn ? fn(a, b, c, d, e, f, g, h) : 0;
}
DWORD WINAPI VerInstallFileW(DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPCWSTR e, LPWSTR f, PUINT g, PUINT h)
{
    typedef DWORD (WINAPI * FN)(DWORD, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPWSTR, PUINT, PUINT);
    static FN fn = NULL;
    if (!fn) fn = (FN)GetProcAddress(RealVersion(), "VerInstallFileW");
    return fn ? fn(a, b, c, d, e, f, g, h) : 0;
}

// ===== 2. 公共：路径判断 / 补丁内容 =====
typedef LONG NTSTATUS;
typedef struct _IO_STATUS_BLOCK_NT {
    union { LONG Status; PVOID Pointer; } u;
    ULONG_PTR Information;
} IO_STATUS_BLOCK_NT;
typedef struct _UNICODE_STRING_NT {
    USHORT Length;
    USHORT MaximumLength;
    PWSTR Buffer;
} UNICODE_STRING_NT;
typedef struct _OBJECT_ATTRIBUTES_NT {
    ULONG Length;
    HANDLE RootDirectory;
    UNICODE_STRING_NT* ObjectName;
    ULONG Attributes;
    PVOID SecurityDescriptor;
    PVOID SecurityQualityOfService;
} OBJECT_ATTRIBUTES_NT;

typedef NTSTATUS (NTAPI *pNtCreateFile)(
    PHANDLE, ACCESS_MASK, OBJECT_ATTRIBUTES_NT*, IO_STATUS_BLOCK_NT*, PLARGE_INTEGER,
    ULONG, ULONG, ULONG, ULONG, PVOID, ULONG);

static void log_line(const char* fmt, ...);

typedef BOOL (WINAPI *pCreateProcessW)(LPCWSTR, LPWSTR, LPSECURITY_ATTRIBUTES, LPSECURITY_ATTRIBUTES,
                                        BOOL, DWORD, LPVOID, LPCWSTR, LPSTARTUPINFOW, LPPROCESS_INFORMATION);
typedef BOOL (WINAPI *pCreateProcessA)(LPCSTR, LPSTR, LPSECURITY_ATTRIBUTES, LPSECURITY_ATTRIBUTES,
                                       BOOL, DWORD, LPVOID, LPCSTR, LPSTARTUPINFOA, LPPROCESS_INFORMATION);

static pCreateProcessW g_origCreateProcessW = NULL;
static pCreateProcessA g_origCreateProcessA = NULL;

// 判断命令行是否在装 vanguard
static BOOL looks_like_vanguard_install(const char* cmd)
{
    if (!cmd) return FALSE;
    if (_strnicmp(cmd, "", 1) == 0 && cmd[0] == 0) return FALSE;
    if (strstr(cmd, "--vanguard") || strstr(cmd, "-vanguard")) return TRUE;
    if (strstr(cmd, "vanguard-install") || strstr(cmd, "install-vanguard")) return TRUE;
    if (strstr(cmd, "vgc-install") || strstr(cmd, "install-vgc")) return TRUE;
    if (strstr(cmd, "Riot Vanguard")) return TRUE;
    return FALSE;
}

BOOL WINAPI HookedCreateProcessW(LPCWSTR lpApplicationName, LPWSTR lpCommandLine,
                                 LPSECURITY_ATTRIBUTES lpProcessAttributes, LPSECURITY_ATTRIBUTES lpThreadAttributes,
                                 BOOL bInheritHandles, DWORD dwCreationFlags, LPVOID lpEnvironment,
                                 LPCWSTR lpCurrentDirectory, LPSTARTUPINFOW lpStartupInfo,
                                 LPPROCESS_INFORMATION lpProcessInformation)
{
    char cmdA[2048];
    char appA[1024];
    cmdA[0] = 0;
    appA[0] = 0;
    if (lpCommandLine) WideCharToMultiByte(CP_ACP, 0, lpCommandLine, -1, cmdA, sizeof(cmdA) - 1, NULL, NULL);
    if (lpApplicationName) WideCharToMultiByte(CP_ACP, 0, lpApplicationName, -1, appA, sizeof(appA) - 1, NULL, NULL);

    log_line("CreateProcessW app=[%s] cmd=[%s] flags=%lx", appA, cmdA, dwCreationFlags);

    if (looks_like_vanguard_install(cmdA) || looks_like_vanguard_install(appA))
    {
        log_line("  -> BLOCKED (vanguard install attempt)");
        SetLastError(ERROR_ACCESS_DENIED);
        return FALSE;
    }

    return g_origCreateProcessW(lpApplicationName, lpCommandLine, lpProcessAttributes, lpThreadAttributes,
                                bInheritHandles, dwCreationFlags, lpEnvironment, lpCurrentDirectory,
                                lpStartupInfo, lpProcessInformation);
}

BOOL WINAPI HookedCreateProcessA(LPCSTR lpApplicationName, LPSTR lpCommandLine,
                                 LPSECURITY_ATTRIBUTES lpProcessAttributes, LPSECURITY_ATTRIBUTES lpThreadAttributes,
                                 BOOL bInheritHandles, DWORD dwCreationFlags, LPVOID lpEnvironment,
                                 LPCSTR lpCurrentDirectory, LPSTARTUPINFOA lpStartupInfo,
                                 LPPROCESS_INFORMATION lpProcessInformation)
{
    log_line("CreateProcessA app=[%s] cmd=[%s] flags=%lx",
             lpApplicationName ? lpApplicationName : "",
             lpCommandLine ? lpCommandLine : "", dwCreationFlags);

    if (looks_like_vanguard_install(lpCommandLine) || looks_like_vanguard_install(lpApplicationName))
    {
        log_line("  -> BLOCKED (vanguard install attempt, A)");
        SetLastError(ERROR_ACCESS_DENIED);
        return FALSE;
    }

    return g_origCreateProcessA(lpApplicationName, lpCommandLine, lpProcessAttributes, lpThreadAttributes,
                                bInheritHandles, dwCreationFlags, lpEnvironment, lpCurrentDirectory,
                                lpStartupInfo, lpProcessInformation);
}

static HANDLE (WINAPI *g_origCreateFileW)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES,
                                          DWORD, DWORD, HANDLE) = NULL;
static HANDLE (WINAPI *g_origCreateFile2)(LPCWSTR, DWORD, DWORD, DWORD, LPVOID) = NULL;
static pNtCreateFile g_origNtCreateFile = NULL;
static BYTE* g_trampoline = NULL;
static BYTE g_saved[8];

static WCHAR g_tempDir[MAX_PATH] = {0};
static WCHAR g_cacheLol[MAX_PATH] = {0};
static WCHAR g_cacheTft[MAX_PATH] = {0};
static volatile LONG g_lockLol = 0;
static volatile LONG g_lockTft = 0;
static const char* LOG_PATH = "C:\\vghook\\vghook.log";

static void log_line(const char* fmt, ...)
{
    char buf[2048];
    va_list ap;
    FILE* f = NULL;
    SYSTEMTIME st;
    va_start(ap, fmt);
    _vsnprintf_s(buf, sizeof(buf), _TRUNCATE, fmt, ap);
    va_end(ap);
    if (fopen_s(&f, LOG_PATH, "a") == 0 && f)
    {
        GetLocalTime(&st);
        fprintf(f, "%02d:%02d:%02d.%03d %s\n", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, buf);
        fclose(f);
    }
}

static char* read_all_plain(const WCHAR* path, DWORD* out_len)
{
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    LARGE_INTEGER size;
    DWORD len, got;
    char* buf;

    if (h == INVALID_HANDLE_VALUE) return NULL;
    size.QuadPart = 0;
    if (!GetFileSizeEx(h, &size) || size.QuadPart <= 0 || size.QuadPart > 8 * 1024 * 1024)
    {
        CloseHandle(h);
        return NULL;
    }
    len = (DWORD)size.QuadPart;
    buf = (char*)malloc(len + 1);
    if (!buf) { CloseHandle(h); return NULL; }
    got = 0;
    if (!ReadFile(h, buf, len, &got, NULL)) { free(buf); CloseHandle(h); return NULL; }
    CloseHandle(h);
    buf[got] = 0;
    *out_len = got;
    return buf;
}

static DWORD patch_vanguard(char* buf, DWORD len)
{
    const char* needle = "vanguard: true";
    const size_t nlen = strlen(needle);
    const char* repl = "vanguard: false";
    const size_t rlen = strlen(repl);
    DWORD count = 0, i, newLen, si, di;
    char* out;

    for (i = 0; i + nlen <= len; i++)
        if (memcmp(buf + i, needle, nlen) == 0) count++;
    if (count == 0) return len;

    newLen = len + (DWORD)(count * (rlen - nlen));
    out = (char*)malloc(newLen + 1);
    if (!out) return len;

    si = di = 0;
    while (si < len)
    {
        if (si + nlen <= len && memcmp(buf + si, needle, nlen) == 0)
        {
            memcpy(out + di, repl, rlen);
            di += (DWORD)rlen;
            si += (DWORD)nlen;
        }
        else out[di++] = buf[si++];
    }
    out[di] = 0;
    memcpy(buf, out, di);
    free(out);
    return di;
}

static const WCHAR* build_patched_copy(const WCHAR* src)
{
    WCHAR* cache;
    volatile LONG* plock;
    DWORD len = 0;
    char* data;
    DWORD newLen;
    const WCHAR* base;
    HANDLE h;
    DWORD wrote;

    if (wcsstr(src, L"teamfighttactics")) { cache = g_cacheTft; plock = &g_lockTft; }
    else { cache = g_cacheLol; plock = &g_lockLol; }

    if (InterlockedCompareExchange(plock, 1, 0) != 0)
    {
        int i;
        for (i = 0; i < 500; i++)
        {
            Sleep(5);
            if (InterlockedCompareExchange(plock, 0, 0) == 0) break;
        }
        return cache[0] ? cache : NULL;
    }

    if (cache[0] && GetFileAttributesW(cache) != INVALID_FILE_ATTRIBUTES)
    {
        InterlockedExchange(plock, 0);
        return cache;
    }

    data = read_all_plain(src, &len);
    if (!data) { InterlockedExchange(plock, 0); return NULL; }

    newLen = patch_vanguard(data, len);
    log_line("patched copy: %u -> %u bytes", len, newLen);

    base = wcsrchr(src, L'\\');
    base = base ? base + 1 : src;
    // 注意：GetTempPathW 返回的路径末尾已经带 '\'，这里不能再补一个，
    // 否则会得到 "Temp\\vghook_xxx" 并被 NtCreateFile 判为 STATUS_OBJECT_PATH_SYNTAX_BAD。
    swprintf_s(cache, MAX_PATH, L"%svghook_%s", g_tempDir, base);

    h = CreateFileW(cache, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE)
    {
        wrote = 0;
        WriteFile(h, data, newLen, &wrote, NULL);
        CloseHandle(h);
        log_line("wrote %u bytes", wrote);
    }
    else
    {
        log_line("write temp failed err=%lu", GetLastError());
        cache[0] = 0;
    }

    free(data);
    InterlockedExchange(plock, 0);
    return cache[0] ? cache : NULL;
}

static BOOL is_product_settings(const WCHAR* path)
{
    const WCHAR* name;
    if (!path) return FALSE;
    name = wcsrchr(path, L'\\');
    name = name ? name + 1 : path;
    return wcsstr(name, L"product_settings.yaml") != NULL;
}

// ClientConfiguration.json —— agent 模块的依赖清单来自这里
static BOOL is_client_config(const WCHAR* path)
{
    const WCHAR* name;
    if (!path) return FALSE;
    name = wcsrchr(path, L'\\');
    name = name ? name + 1 : path;
    return _wcsicmp(name, L"ClientConfiguration.json") == 0;
}

static const WCHAR* build_patched_copy(const WCHAR* src);
static const WCHAR* build_patched_client_config(const WCHAR* src);

// 判断是不是往 VG 安装目录写入（阻止 agent 重新安装 Vanguard）
static BOOL is_vanguard_install_write(const WCHAR* path, ACCESS_MASK access, ULONG disposition)
{
    if (!path) return FALSE;
    if (!wcsstr(path, L"Riot Vanguard")) return FALSE;

    // 读操作放行
    if ((access & (GENERIC_WRITE | FILE_WRITE_DATA | FILE_APPEND_DATA | DELETE)) == 0 &&
        disposition == FILE_OPEN)
        return FALSE;

    return TRUE;
}

// 统一入口：路径命中就返回打过补丁的临时副本
static const WCHAR* patched_copy_for(const WCHAR* path)
{
    if (is_product_settings(path)) return build_patched_copy(path);
    if (is_client_config(path))    return build_patched_client_config(path);
    return NULL;
}

// 把 JSON 里配置出现的 "key": true 换成 false（或反向），原地改写并返回新长度
static DWORD patch_json_bool(char* buf, DWORD len, const char* from, const char* to)
{
    size_t flen = strlen(from);
    size_t tlen = strlen(to);
    DWORD i, count = 0, newLen, si, di;
    char* out;

    if (flen == 0 || tlen == 0) return len;

    for (i = 0; i + flen <= len; i++)
        if (memcmp(buf + i, from, flen) == 0) count++;
    if (count == 0) return len;

    newLen = len + (DWORD)(count * ((INT64)tlen - (INT64)flen));
    out = (char*)malloc(newLen + 1);
    if (!out) return len;

    si = di = 0;
    while (si < len)
    {
        if (si + flen <= len && memcmp(buf + si, from, flen) == 0)
        {
            memcpy(out + di, to, tlen);
            di += (DWORD)tlen;
            si += (DWORD)flen;
        }
        else out[di++] = buf[si++];
    }
    out[di] = 0;
    memcpy(buf, out, di);
    free(out);
    return di;
}

static const WCHAR* build_patched_client_config(const WCHAR* src)
{
    static WCHAR cache[MAX_PATH] = {0};
    static volatile LONG lock = 0;
    DWORD len = 0;
    char* data;
    DWORD newLen;
    HANDLE h;
    DWORD wrote;

    if (InterlockedCompareExchange(&lock, 1, 0) != 0)
    {
        int i;
        // 这里在 NtCreateFile 的调用链上，绝不能长时间阻塞：
        // 拿不到锁就直接用旧缓存（或放行原始读取），不要 Sleep 等待。
        for (i = 0; i < 20; i++)
        {
            Sleep(2);
            if (InterlockedCompareExchange(&lock, 0, 0) == 0) break;
        }
        return cache[0] ? cache : NULL;
    }

    if (cache[0] && GetFileAttributesW(cache) != INVALID_FILE_ATTRIBUTES)
    {
        InterlockedExchange(&lock, 0);
        return cache;
    }

    data = read_all_plain(src, &len);
    if (!data) { InterlockedExchange(&lock, 0); return NULL; }

    // 把会触发 VG 安装/检查的布尔值逐个改掉
    newLen = patch_vanguard(data, len);
    newLen = patch_json_bool(data, newLen,
        "\"anticheat.vanguard.enabled\": true",
        "\"anticheat.vanguard.enabled\": false");
    newLen = patch_json_bool(data, newLen,
        "\"lol.client_settings.vanguard.enabled\": true",
        "\"lol.client_settings.vanguard.enabled\": false");
    newLen = patch_json_bool(data, newLen,
        "\"keystone.client.feature_flags.vanguardLaunch.disabled\": false",
        "\"keystone.client.feature_flags.vanguardLaunch.disabled\": true");
    newLen = patch_json_bool(data, newLen,
        "\"anticheat.vanguard.backgroundInstall\": true",
        "\"anticheat.vanguard.backgroundInstall\": false");
    log_line("client-config patched: %u -> %u bytes", len, newLen);

    swprintf_s(cache, MAX_PATH, L"%svghook_ClientConfiguration.json", g_tempDir);

    h = CreateFileW(cache, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE)
    {
        wrote = 0;
        WriteFile(h, data, newLen, &wrote, NULL);
        CloseHandle(h);
        log_line("client-config temp written: %u bytes", wrote);
    }
    else
    {
        log_line("client-config temp write failed err=%lu", GetLastError());
        cache[0] = 0;
    }

    free(data);
    InterlockedExchange(&lock, 0);
    return cache[0] ? cache : NULL;
}

static const WCHAR* normalize_nt_path(const UNICODE_STRING_NT* us, WCHAR* out, size_t outCount)
{
    const WCHAR* p;
    size_t len;
    if (!us || !us->Buffer || us->Length == 0) return NULL;
    p = us->Buffer;
    len = us->Length / sizeof(WCHAR);
    if (len >= 4 && p[0] == L'\\' && p[1] == L'?' && p[2] == L'?' && p[3] == L'\\')
    {
        p += 4;
        len -= 4;
    }
    if (len > outCount - 1) len = outCount - 1;
    wmemcpy(out, p, len);
    out[len] = 0;
    return out;
}

// ===== 3. IAT hook =====
HANDLE WINAPI HookedCreateFileW(LPCWSTR lpFileName, DWORD dwDesiredAccess, DWORD dwShareMode,
                                LPSECURITY_ATTRIBUTES lpSecurityAttributes, DWORD dwCreationDisposition,
                                DWORD dwFlagsAndAttributes, HANDLE hTemplateFile)
{
    // 只有写操作才需要检查（读路径完全直通：磁盘上的 yaml 已经是 vanguard:false）
    if ((dwDesiredAccess & (GENERIC_WRITE | FILE_WRITE_DATA | FILE_APPEND_DATA | DELETE)) != 0 &&
        lpFileName && wcsstr(lpFileName, L"Riot Vanguard"))
    {
        log_line("BLOCKED vg write via CreateFileW: %ls", lpFileName);
        SetLastError(ERROR_ACCESS_DENIED);
        return INVALID_HANDLE_VALUE;
    }

    return g_origCreateFileW(lpFileName, dwDesiredAccess, dwShareMode, lpSecurityAttributes,
                             dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
}

HANDLE WINAPI HookedCreateFile2(LPCWSTR lpFileName, DWORD dwDesiredAccess, DWORD dwShareMode,
                                DWORD dwCreationDisposition, LPVOID pCreateExParams)
{
    if ((dwDesiredAccess & (GENERIC_WRITE | FILE_WRITE_DATA | FILE_APPEND_DATA | DELETE)) != 0 &&
        lpFileName && wcsstr(lpFileName, L"Riot Vanguard"))
    {
        log_line("BLOCKED vg write via CreateFile2: %ls", lpFileName);
        SetLastError(ERROR_ACCESS_DENIED);
        return INVALID_HANDLE_VALUE;
    }
    return g_origCreateFile2(lpFileName, dwDesiredAccess, dwShareMode, dwCreationDisposition, pCreateExParams);
}

static void patch_iat(void)
{
    HMODULE base = GetModuleHandleW(NULL);
    IMAGE_DOS_HEADER* dos;
    IMAGE_NT_HEADERS* nt;
    IMAGE_DATA_DIRECTORY dir;
    IMAGE_IMPORT_DESCRIPTOR* imp;

    if (!base) return;
    dos = (IMAGE_DOS_HEADER*)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
    nt = (IMAGE_NT_HEADERS*)((BYTE*)base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return;
    dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return;

    imp = (IMAGE_IMPORT_DESCRIPTOR*)((BYTE*)base + dir.VirtualAddress);
    for (; imp->Name; imp++)
    {
        const char* dll = (const char*)((BYTE*)base + imp->Name);
        IMAGE_THUNK_DATA* origThunk;
        IMAGE_THUNK_DATA* thunk;
        if (_stricmp(dll, "kernel32.dll") != 0 && _stricmp(dll, "KERNELBASE.dll") != 0) continue;
        if (!imp->OriginalFirstThunk) continue;

        origThunk = (IMAGE_THUNK_DATA*)((BYTE*)base + imp->OriginalFirstThunk);
        thunk = (IMAGE_THUNK_DATA*)((BYTE*)base + imp->FirstThunk);
        for (; origThunk->u1.Function; origThunk++, thunk++)
        {
            IMAGE_IMPORT_BY_NAME* ibn;
            const char* name;
            void* replacement = NULL;
            DWORD oldProt = 0;
            if (IMAGE_SNAP_BY_ORDINAL(origThunk->u1.Ordinal)) continue;
            ibn = (IMAGE_IMPORT_BY_NAME*)((BYTE*)base + origThunk->u1.AddressOfData);
            name = (const char*)ibn->Name;

            if (strcmp(name, "CreateFileW") == 0)
            {
                g_origCreateFileW = (HANDLE (WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES,
                                                      DWORD, DWORD, HANDLE))thunk->u1.Function;
                replacement = (void*)HookedCreateFileW;
            }
            else if (strcmp(name, "CreateFile2") == 0 && g_origCreateFile2)
            {
                replacement = (void*)HookedCreateFile2;
            }
            else if (strcmp(name, "CreateProcessW") == 0 && g_origCreateProcessW)
            {
                replacement = (void*)HookedCreateProcessW;
            }
            else if (strcmp(name, "CreateProcessA") == 0 && g_origCreateProcessA)
            {
                replacement = (void*)HookedCreateProcessA;
            }
            if (!replacement) continue;
            if (VirtualProtect(&thunk->u1.Function, sizeof(void*), PAGE_READWRITE, &oldProt))
            {
                thunk->u1.Function = (ULONG_PTR)replacement;
                VirtualProtect(&thunk->u1.Function, sizeof(void*), oldProt, &oldProt);
                log_line("IAT hooked %s", name);
            }
        }
    }
}

// ===== 4. NtCreateFile inline hook =====
NTSTATUS NTAPI HookedNtCreateFile(PHANDLE FileHandle, ACCESS_MASK DesiredAccess,
                                  OBJECT_ATTRIBUTES_NT* ObjectAttributes, IO_STATUS_BLOCK_NT* IoStatusBlock,
                                  PLARGE_INTEGER AllocationSize, ULONG FileAttributes, ULONG ShareAccess,
                                  ULONG CreateDisposition, ULONG CreateOptions, PVOID EaBuffer, ULONG EaLength)
{
    WCHAR path[MAX_PATH];
    const WCHAR* norm = NULL;
    int interesting = 0;
    int wantsWrite = 0;

    // ---- 快速放行（性能关键）----
    // 这是 inline hook，客户端启动期间会承受上万次调用。任何一次多余的
    // wmemcpy / wcsstr 都会被放大成几十秒的启动延迟，所以先做最廉价的前置判断：
    //   1) 没有文件名 -> 直接放行
    //   2) 路径太短 -> 不可能是目标文件
    //   3) 只读打开时，结尾不是 .yaml / .json 的，一律放行
    // 写操作仍然走完整检查（要拦 Vanguard 安装目录的写入）。
    if (ObjectAttributes == NULL || ObjectAttributes->ObjectName == NULL ||
        ObjectAttributes->ObjectName->Buffer == NULL || ObjectAttributes->ObjectName->Length < 24)
    {
        return g_origNtCreateFile(FileHandle, DesiredAccess, ObjectAttributes, IoStatusBlock, AllocationSize,
                                  FileAttributes, ShareAccess, CreateDisposition, CreateOptions, EaBuffer, EaLength);
    }

    wantsWrite = (DesiredAccess & (GENERIC_WRITE | FILE_WRITE_DATA | FILE_APPEND_DATA | DELETE)) != 0;

    // 读路径完全直通。
    // 原因：product_settings.yaml 已经由 VanguardDependencyPatcher 在磁盘上改成
    // dependencies.vanguard: false 并锁定只读，运行时再做读重定向是纯冗余；
    // 而客户端启动期要承受上万次 NtCreateFile，任何一次多余处理都会放大成十几秒延迟。
    if (!wantsWrite)
    {
        return g_origNtCreateFile(FileHandle, DesiredAccess, ObjectAttributes, IoStatusBlock, AllocationSize,
                                  FileAttributes, ShareAccess, CreateDisposition, CreateOptions, EaBuffer, EaLength);
    }

    norm = normalize_nt_path(ObjectAttributes->ObjectName, path, MAX_PATH);
    interesting = 1;

    // 阻止向 VG 安装目录写入（agent 重新安装 Vanguard 时用任何 API 起进程都逃不过这里）
    if (interesting && norm && wantsWrite && is_vanguard_install_write(norm, DesiredAccess, CreateDisposition))
    {
        static volatile LONG vgLogCount = 0;
        if (InterlockedIncrement(&vgLogCount) <= 20)
            log_line("BLOCKED vg install write: %ls access=%lx disp=%lu", norm, DesiredAccess, CreateDisposition);
        return (NTSTATUS)0xC0000022L; // STATUS_ACCESS_DENIED
    }

    if (norm && (is_product_settings(norm) || is_client_config(norm)))
    {
        log_line("NtCreateFile hit access=%lx disp=%lu", DesiredAccess, CreateDisposition);
        if ((DesiredAccess & (GENERIC_READ | FILE_READ_DATA)) && CreateDisposition == FILE_OPEN)
        {
            const WCHAR* patched = patched_copy_for(norm);
            if (patched)
            {
                UNICODE_STRING_NT us;
                OBJECT_ATTRIBUTES_NT oa;
                NTSTATUS st;
                // NtCreateFile 的 ObjectName 必须是 NT 路径（\??\C:\...），
                // 直接传 C:\... 会返回 STATUS_OBJECT_PATH_SYNTAX_BAD (0xC000003B)。
                WCHAR ntPath[MAX_PATH + 8];
                swprintf_s(ntPath, MAX_PATH + 8, L"\\??\\%s", patched);

                us.Buffer = ntPath;
                us.Length = (USHORT)(wcslen(ntPath) * sizeof(WCHAR));
                us.MaximumLength = us.Length + sizeof(WCHAR);
                oa = *ObjectAttributes;
                oa.ObjectName = &us;
                oa.RootDirectory = NULL;
                st = g_origNtCreateFile(FileHandle, DesiredAccess, &oa, IoStatusBlock, AllocationSize,
                                        FileAttributes, ShareAccess, CreateDisposition, CreateOptions, EaBuffer, EaLength);
                log_line("  -> redirected status=%08lx", (unsigned long)st);
                return st;
            }
        }
    }
    return g_origNtCreateFile(FileHandle, DesiredAccess, ObjectAttributes, IoStatusBlock, AllocationSize,
                              FileAttributes, ShareAccess, CreateDisposition, CreateOptions, EaBuffer, EaLength);
}

// 在目标地址附近（±2GB）找一块可执行内存，用来放 “jmp [rip+0] + 绝对地址” 的桥
static BYTE* alloc_near(void* target, size_t size)
{
    SYSTEM_INFO si;
    BYTE* base = (BYTE*)target;
    ULONGLONG span = 0x40000000ULL; // 1GB，留够余量
    BYTE* lo = (base > (BYTE*)span) ? base - span : (BYTE*)(uintptr_t)0x10000;
    BYTE* hi = base + span;
    BYTE* p;

    GetSystemInfo(&si);

    for (p = lo; p < hi; p += si.dwAllocationGranularity)
    {
        MEMORY_BASIC_INFORMATION mbi;
        if (VirtualQuery(p, &mbi, sizeof(mbi)) != sizeof(mbi))
            break;
        if (mbi.State != MEM_FREE)
        {
            p = (BYTE*)mbi.BaseAddress + mbi.RegionSize;
            p -= si.dwAllocationGranularity;
            continue;
        }
        {
            BYTE* mem = (BYTE*)VirtualAlloc(p, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (mem)
                return mem;
        }
    }
    return NULL;
}

static BOOL install_inline_hook(void)
{
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    BYTE* target;
    BYTE* bridge;
    DWORD oldProt;
    BYTE jmpPatch[8];
    LONGLONG rel;

    if (!ntdll) return FALSE;
    target = (BYTE*)GetProcAddress(ntdll, "NtCreateFile");
    if (!target) return FALSE;

    memcpy(g_saved, target, 8);
    log_line("NtCreateFile @%p bytes %02x %02x %02x %02x %02x %02x %02x %02x",
             target, g_saved[0], g_saved[1], g_saved[2], g_saved[3],
             g_saved[4], g_saved[5], g_saved[6], g_saved[7]);

    // trampoline：原始 8 字节 + 绝对跳回 target+8
    g_trampoline = (BYTE*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!g_trampoline)
    {
        log_line("trampoline alloc failed err=%lu", GetLastError());
        return FALSE;
    }
    memcpy(g_trampoline, g_saved, 8);
    g_trampoline[8] = 0xFF;
    g_trampoline[9] = 0x25;
    *(DWORD*)(g_trampoline + 10) = 0;
    *(ULONGLONG*)(g_trampoline + 14) = (ULONGLONG)(target + 8);
    g_origNtCreateFile = (pNtCreateFile)g_trampoline;

    // 桥：在 target 附近分配，内容是 “jmp [rip+0] ; dq HookedNtCreateFile”
    bridge = alloc_near(target, 64);
    if (!bridge)
    {
        log_line("bridge alloc failed");
        return FALSE;
    }
    bridge[0] = 0xFF;
    bridge[1] = 0x25;
    *(DWORD*)(bridge + 2) = 0;
    *(ULONGLONG*)(bridge + 6) = (ULONGLONG)HookedNtCreateFile;
    FlushInstructionCache(GetCurrentProcess(), bridge, 14);
    log_line("bridge @%p -> %p", bridge, HookedNtCreateFile);

    rel = bridge - (target + 5);
    if (rel < -0x70000000LL || rel > 0x70000000LL)
    {
        log_line("bridge still out of range (%lld)", rel);
        return FALSE;
    }
    jmpPatch[0] = 0xE9;
    *(INT*)(jmpPatch + 1) = (INT)rel;
    jmpPatch[5] = 0x90;
    jmpPatch[6] = 0x90;
    jmpPatch[7] = 0x90;

    if (!VirtualProtect(target, 16, PAGE_EXECUTE_READWRITE, &oldProt))
    {
        log_line("VirtualProtect failed err=%lu", GetLastError());
        return FALSE;
    }
    memcpy(target, jmpPatch, 8);
    FlushInstructionCache(GetCurrentProcess(), target, 8);
    VirtualProtect(target, 16, oldProt, &oldProt);
    log_line("inline hook installed on NtCreateFile");
    return TRUE;
}

// ---- patch RiotClientFoundation!VanguardDependency::IsInstalled ----
// .pdata 里该函数 begin=RVA 0x546890 (size 100)。入口是：
//   mov [rsp+0x10],rdx ; mov [rsp+0x8],rcx ; sub rsp,0x28
//   mov r8,[rsp+0x30] ; mov rax,[r8+8]
//   cmp byte [rax+0xE58],0 ; jne +7 ; mov al,1 ; add rsp,0x28 ; ret
// 在入口直写 "mov al,1 ; ret" (B0 01 C3) 即可永远返回 true。
#define VG_ISINSTALLED_RVA 0x546890

static DWORD WINAPI vg_patch_thread(LPVOID param)
{
    int i;
    (void)param;
    for (i = 0; i < 900; i++)   // 最多等 90 秒
    {
        HMODULE f = GetModuleHandleW(L"RiotClientFoundation.dll");
        if (f)
        {
            BYTE* fn = (BYTE*)f + VG_ISINSTALLED_RVA;
            DWORD oldProt = 0;
            if (VirtualProtect(fn, 16, PAGE_EXECUTE_READWRITE, &oldProt))
            {
                log_line("patching IsInstalled @%p (before: %02x %02x %02x)",
                         fn, fn[0], fn[1], fn[2]);
                fn[0] = 0xB0;   // mov al, 1
                fn[1] = 0x01;
                fn[2] = 0xC3;   // ret
                FlushInstructionCache(GetCurrentProcess(), fn, 3);
                VirtualProtect(fn, 16, oldProt, &oldProt);
                log_line("patched IsInstalled -> return true");
            }
            else
            {
                log_line("VirtualProtect(IsInstalled) failed err=%lu", GetLastError());
            }
            return 0;
        }
        Sleep(100);
    }
    log_line("vg patch thread: RiotClientFoundation.dll never appeared");
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved)
{
    HMODULE k32, kb;

    if (reason != DLL_PROCESS_ATTACH)
        return TRUE;

    DisableThreadLibraryCalls(hinst);
    GetTempPathW(MAX_PATH, g_tempDir);

    k32 = GetModuleHandleW(L"kernel32.dll");
    kb = GetModuleHandleW(L"kernelbase.dll");
    if (k32)
    {
        g_origCreateFileW = (HANDLE (WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES,
                                              DWORD, DWORD, HANDLE))GetProcAddress(k32, "CreateFileW");
        g_origCreateFile2 = (HANDLE (WINAPI*)(LPCWSTR, DWORD, DWORD, DWORD, LPVOID))GetProcAddress(k32, "CreateFile2");
        g_origCreateProcessW = (pCreateProcessW)GetProcAddress(k32, "CreateProcessW");
        g_origCreateProcessA = (pCreateProcessA)GetProcAddress(k32, "CreateProcessA");
    }
    if (!g_origCreateFileW && kb)
        g_origCreateFileW = (HANDLE (WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES,
                                              DWORD, DWORD, HANDLE))GetProcAddress(kb, "CreateFileW");
    if (!g_origCreateFile2 && kb)
        g_origCreateFile2 = (HANDLE (WINAPI*)(LPCWSTR, DWORD, DWORD, DWORD, LPVOID))GetProcAddress(kb, "CreateFile2");

    log_line("=== attach pid=%lu W=%p 2=%p", GetCurrentProcessId(), g_origCreateFileW, g_origCreateFile2);

    patch_iat();
    install_inline_hook();

    // foundation 还没加载，起线程等它出现后 patch IsInstalled
    CreateThread(NULL, 0, vg_patch_thread, NULL, 0, NULL);

    log_line("=== ready");
    return TRUE;
}
