using System.Drawing;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace StabilityMatrix.Avalonia.Controls;

/// <summary>
/// Hosts WebView2 (Core) for HTML5 video with synced A/V. Windows only; no WinForms/LibVLC.
/// </summary>
public class WebView2Host : NativeControlHost
{
    private IntPtr hwnd;
    private CoreWebView2Controller? controller;
    private string? pendingHtml;
    private string? mappedFolder;
    private bool ready;

    public event EventHandler? Ready;

    public bool IsReady => ready;

    public async Task NavigateHtmlAsync(string html)
    {
        pendingHtml = html;
        await EnsureAsync();
        if (controller?.CoreWebView2 is null)
            return;

        controller.CoreWebView2.NavigateToString(html);
    }

    public async Task MapFolderAsync(string folderPath)
    {
        mappedFolder = folderPath;
        await EnsureAsync();
        ApplyFolderMap();
    }

    public async Task SetMutedAsync(bool muted)
    {
        if (controller?.CoreWebView2 is null)
            return;

        var flag = muted ? "true" : "false";
        await controller.CoreWebView2.ExecuteScriptAsync($"window.setMuted && window.setMuted({flag});");
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return base.CreateNativeControlCore(parent);

        hwnd = CreateWindowExW(
            0,
            "Static",
            "",
            WS_CHILD | WS_VISIBLE,
            0,
            0,
            1,
            1,
            parent.Handle,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero
        );

        LayoutUpdated += OnLayoutUpdated;
        _ = EnsureAsync();
        return new PlatformHandle(hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        LayoutUpdated -= OnLayoutUpdated;
        ready = false;

        if (controller is not null)
        {
            try
            {
                controller.Close();
            }
            catch
            {
                // ignore
            }

            controller = null;
        }

        if (hwnd != IntPtr.Zero)
        {
            DestroyWindow(hwnd);
            hwnd = IntPtr.Zero;
        }

        base.DestroyNativeControlCore(control);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => SyncBounds();

    private void SyncBounds()
    {
        if (controller is null || hwnd == IntPtr.Zero)
            return;

        if (!GetClientRect(hwnd, out var rect))
            return;

        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0)
            return;

        try
        {
            controller.Bounds = new Rectangle(0, 0, w, h);
        }
        catch
        {
            // ignore during teardown
        }
    }

    private async Task EnsureAsync()
    {
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            if (controller is null)
            {
                var env = await CoreWebView2Environment.CreateAsync();
                controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
                controller.DefaultBackgroundColor = Color.Black;
                SyncBounds();
            }

            if (controller.CoreWebView2 is null)
                return;

            controller.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            controller.CoreWebView2.Settings.AreDevToolsEnabled = false;
            controller.CoreWebView2.Settings.IsStatusBarEnabled = false;

            ApplyFolderMap();

            if (!ready)
            {
                ready = true;
                Ready?.Invoke(this, EventArgs.Empty);
            }

            if (pendingHtml is not null)
            {
                var html = pendingHtml;
                pendingHtml = null;
                controller.CoreWebView2.NavigateToString(html);
            }
        }
        catch
        {
            ready = false;
        }
    }

    private void ApplyFolderMap()
    {
        if (controller?.CoreWebView2 is null || string.IsNullOrWhiteSpace(mappedFolder))
            return;

        try
        {
            controller.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "sm.video",
                mappedFolder,
                CoreWebView2HostResourceAccessKind.Allow
            );
        }
        catch
        {
            // already mapped / invalid
        }
    }

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
