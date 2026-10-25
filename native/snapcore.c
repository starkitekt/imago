/* snapcore.c: native capture core for Imago.
 *
 * Plain C on Win32. The host process is per-monitor DPI aware (v2, set at startup
 * by ApplicationHighDpiMode), so every coordinate here is a physical pixel.
 * Exports are called from C# through P/Invoke; see Native.cs.
 */
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <dwmapi.h>
#include <shellscalingapi.h>
#include <stdint.h>
#include <string.h>

#define SC_API __declspec(dllexport)

enum {
    SC_OK = 0,
    SC_E_ARG = -1,
    SC_E_SIZE = -2,
    SC_E_GDI = -3,
    SC_E_BUF = -4,
    SC_E_WINDOW = -5
};

#define SC_MAX_DIM 32768

/* Window flags reported back to C#. */
#define SC_WF_FOREGROUND 1 /* the window the user was working in */
#define SC_WF_PROTECTED  2 /* display affinity set: it captures as black */
#define SC_WF_ELEVATED   4 /* runs at a higher integrity level than us */

typedef struct {
    int64_t hwnd;
    int32_t x, y, w, h; /* visible frame, without the invisible resize border */
    uint32_t pid;
    uint32_t affinity;  /* WDA_NONE, WDA_MONITOR or WDA_EXCLUDEFROMCAPTURE */
    int32_t flags;
    int32_t reserved;
} sc_window;

SC_API int32_t sc_version(void) { return 2; }

/* Process mitigations. Returns a bit mask of what was applied. */
SC_API int32_t sc_harden_process(void)
{
    int32_t applied = 0;

    /* DLL planting: never search the current directory or PATH. */
    if (SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)) applied |= 1;
    SetDllDirectoryW(L"");

    /* Legacy injection points such as AppInit_DLLs and global hooks' DLLs. */
    PROCESS_MITIGATION_EXTENSION_POINT_DISABLE_POLICY ext;
    memset(&ext, 0, sizeof ext);
    ext.DisableExtensionPoints = 1;
    if (SetProcessMitigationPolicy(ProcessExtensionPointDisablePolicy, &ext, sizeof ext)) applied |= 2;

    /* No DLLs from network shares or low-integrity locations. */
    PROCESS_MITIGATION_IMAGE_LOAD_POLICY img;
    memset(&img, 0, sizeof img);
    img.NoRemoteImages = 1;
    img.NoLowMandatoryLabelImages = 1;
    img.PreferSystem32Images = 1;
    if (SetProcessMitigationPolicy(ProcessImageLoadPolicy, &img, sizeof img)) applied |= 4;

    /* Only system fonts: removes a classic parsing attack surface. */
    PROCESS_MITIGATION_FONT_DISABLE_POLICY font;
    memset(&font, 0, sizeof font);
    font.DisableNonSystemFonts = 1;
    if (SetProcessMitigationPolicy(ProcessFontDisablePolicy, &font, sizeof font)) applied |= 8;

    return applied;
}

SC_API int32_t sc_virtual_screen(int32_t *out4)
{
    if (!out4) return SC_E_ARG;
    out4[0] = GetSystemMetrics(SM_XVIRTUALSCREEN);
    out4[1] = GetSystemMetrics(SM_YVIRTUALSCREEN);
    out4[2] = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    out4[3] = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    return SC_OK;
}

/* Bounds of the monitor nearest to a point, and its effective DPI. */
SC_API int32_t sc_monitor_at(int32_t x, int32_t y, int32_t *out4)
{
    if (!out4) return SC_E_ARG;
    POINT pt = { x, y };
    HMONITOR m = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi;
    memset(&mi, 0, sizeof mi);
    mi.cbSize = sizeof mi;
    if (!GetMonitorInfoW(m, &mi)) return SC_E_WINDOW;
    out4[0] = mi.rcMonitor.left;
    out4[1] = mi.rcMonitor.top;
    out4[2] = mi.rcMonitor.right - mi.rcMonitor.left;
    out4[3] = mi.rcMonitor.bottom - mi.rcMonitor.top;
    UINT dx = 96, dy = 96;
    if (FAILED(GetDpiForMonitor(m, MDT_EFFECTIVE_DPI, &dx, &dy))) dx = 96;
    return (int32_t)dx;
}

/* Copies a screen rectangle into dst as top-down BGRA, alpha forced to 255. */
SC_API int32_t sc_capture(int32_t x, int32_t y, int32_t w, int32_t h,
                          uint8_t *dst, int32_t stride, int64_t dst_len)
{
    if (!dst || w <= 0 || h <= 0) return SC_E_ARG;
    if (w > SC_MAX_DIM || h > SC_MAX_DIM) return SC_E_SIZE;
    if (stride < w * 4) return SC_E_BUF; /* w * 4 <= 131072, cannot overflow */
    if ((int64_t)stride * (int64_t)h > dst_len) return SC_E_BUF;

    HDC screen = GetDC(NULL);
    if (!screen) return SC_E_GDI;
    HDC mem = CreateCompatibleDC(screen);

    BITMAPINFO bi;
    memset(&bi, 0, sizeof bi);
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h; /* top-down rows */
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    void *bits = NULL;
    HBITMAP dib = mem ? CreateDIBSection(screen, &bi, DIB_RGB_COLORS, &bits, NULL, 0) : NULL;
    int32_t rc = SC_E_GDI;
    if (dib && bits) {
        HGDIOBJ old = SelectObject(mem, dib);
        /* CAPTUREBLT includes layered windows (menus, tooltips, toasts). */
        if (BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT)) {
            GdiFlush();
            const uint8_t *src = (const uint8_t *)bits;
            const size_t row_bytes = (size_t)w * 4;
            for (int32_t row = 0; row < h; row++) {
                uint8_t *d = dst + (int64_t)row * stride;
                memcpy(d, src + (int64_t)row * (int64_t)row_bytes, row_bytes);
                for (size_t i = 3; i < row_bytes; i += 4) d[i] = 0xFF;
            }
            rc = SC_OK;
        }
        SelectObject(mem, old);
    }
    if (dib) DeleteObject(dib);
    if (mem) DeleteDC(mem);
    ReleaseDC(NULL, screen);
    return rc;
}

static int is_shell_backdrop(HWND hwnd)
{
    wchar_t cls[32];
    if (!GetClassNameW(hwnd, cls, 32)) return 0;
    return wcscmp(cls, L"Progman") == 0 || wcscmp(cls, L"WorkerW") == 0;
}

static HWND foreground_root(void)
{
    HWND f = GetForegroundWindow();
    if (!f) return NULL;
    HWND r = GetAncestor(f, GA_ROOT);
    return r ? r : f;
}

static int fill_window(HWND hwnd, sc_window *w, HWND fg)
{
    RECT r;
    if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &r, sizeof r)))
        if (!GetWindowRect(hwnd, &r)) return 0;
    w->hwnd = (int64_t)(intptr_t)hwnd;
    w->x = r.left;
    w->y = r.top;
    w->w = r.right - r.left;
    w->h = r.bottom - r.top;
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    w->pid = pid;
    DWORD aff = WDA_NONE;
    if (GetWindowDisplayAffinity(hwnd, &aff)) w->affinity = aff;
    w->flags = 0;
    if (hwnd == fg) w->flags |= SC_WF_FOREGROUND;
    if (aff != WDA_NONE) w->flags |= SC_WF_PROTECTED;
    return 1;
}

typedef struct {
    sc_window *out;
    int32_t cap;
    int32_t n;
    DWORD self_pid;
    HWND fg;
} enum_ctx;

static BOOL CALLBACK enum_proc(HWND hwnd, LPARAM lp)
{
    enum_ctx *c = (enum_ctx *)lp;
    if (c->n >= c->cap) return FALSE;
    if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || is_shell_backdrop(hwnd)) return TRUE;

    DWORD cloaked = 0; /* hidden UWP frames and windows on other virtual desktops */
    if (SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof cloaked)) && cloaked)
        return TRUE;

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == c->self_pid) return TRUE;

    LONG ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
    if ((ex & WS_EX_LAYERED) && (ex & WS_EX_TRANSPARENT)) return TRUE; /* click-through overlays */

    sc_window *w = &c->out[c->n];
    memset(w, 0, sizeof *w);
    if (!fill_window(hwnd, w, c->fg)) return TRUE;
    if (w->w <= 1 || w->h <= 1) return TRUE;
    c->n++;
    return TRUE;
}

/* Visible top-level windows, topmost first. Returns the count. */
SC_API int32_t sc_enum_windows(sc_window *out, int32_t cap)
{
    if (!out || cap <= 0) return SC_E_ARG;
    enum_ctx c;
    c.out = out;
    c.cap = cap;
    c.n = 0;
    c.self_pid = GetCurrentProcessId();
    c.fg = foreground_root();
    EnumWindows(enum_proc, (LPARAM)&c);
    return c.n;
}

SC_API int64_t sc_foreground(void)
{
    return (int64_t)(intptr_t)foreground_root();
}

/* Title and executable path of a window, used to name captures. */
SC_API int32_t sc_window_info(int64_t handle, sc_window *out,
                              wchar_t *exe, int32_t exe_cap,
                              wchar_t *title, int32_t title_cap)
{
    HWND hwnd = (HWND)(intptr_t)handle;
    if (!out || !IsWindow(hwnd)) return SC_E_WINDOW;
    memset(out, 0, sizeof *out);
    fill_window(hwnd, out, foreground_root());

    if (title && title_cap > 0) {
        title[0] = 0;
        GetWindowTextW(hwnd, title, title_cap);
        title[title_cap - 1] = 0;
    }
    if (exe && exe_cap > 0) {
        exe[0] = 0;
        HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, out->pid);
        if (p) {
            DWORD n = (DWORD)exe_cap;
            if (!QueryFullProcessImageNameW(p, 0, exe, &n)) exe[0] = 0;
            HANDLE tok = NULL;
            if (OpenProcessToken(p, TOKEN_QUERY, &tok)) {
                TOKEN_ELEVATION e;
                DWORD len = 0;
                if (GetTokenInformation(tok, TokenElevation, &e, sizeof e, &len) && e.TokenIsElevated)
                    out->flags |= SC_WF_ELEVATED;
                CloseHandle(tok);
            } else if (GetLastError() == ERROR_ACCESS_DENIED) {
                out->flags |= SC_WF_ELEVATED;
            }
            CloseHandle(p);
        } else {
            out->flags |= SC_WF_ELEVATED;
        }
    }
    return SC_OK;
}
