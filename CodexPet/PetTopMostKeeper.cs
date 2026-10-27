using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Alife.Client;

namespace Marisa.CodexPetAdapter;

// Reassert our own windows in the topmost band after a foreground-window change,
// without activating them or moving keyboard focus away from the user's app.
public sealed class PetTopMostKeeper : IDisposable
{
    static readonly IntPtr HwndTopMost = new(-1);
    const uint Flags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    readonly Form[] forms;
    readonly System.Threading.Timer timer;
    IntPtr lastForeground;
    int queued;
    bool disposed;

    public PetTopMostKeeper(IAlifeWindow pet, IAlifeWindow? bubble)
    {
        forms = pet is Form main ? (bubble is Form secondary ? new[] { main, secondary } : new[] { main }) : Array.Empty<Form>();
        foreach (var form in forms) { form.Deactivate += OnWindowChanged; form.VisibleChanged += OnWindowChanged; }
        timer = new System.Threading.Timer(_ => CheckForeground(), null, 0, 350);
    }
    void OnWindowChanged(object? sender, EventArgs e) => Refresh();
    void CheckForeground()
    {
        if (disposed) return;
        var current = GetForegroundWindow();
        if (current == lastForeground) return;
        lastForeground = current;
        Refresh();
    }
    public void Refresh()
    {
        if (disposed || forms.Length == 0 || Interlocked.Exchange(ref queued, 1) != 0) return;
        var owner = forms[0];
        if (owner.IsDisposed || !owner.IsHandleCreated) { Interlocked.Exchange(ref queued, 0); return; }
        try
        {
            owner.BeginInvoke((Action)(() => {
                try
                {
                    if (disposed) return;
                    foreach (var form in forms)
                    {
                        if (!form.IsDisposed && form.IsHandleCreated && form.Visible)
                        {
                            // Form.TopMost's setter calls SetWindowPos without
                            // SWP_NOACTIVATE, even when it is already true. In a
                            // Deactivate handler this steals focus back before
                            // WebView2 can receive the user's mouse-down event.
                            // The creation options already set the managed flag;
                            // only reassert the native band without activation.
                            SetWindowPos(form.Handle, HwndTopMost, 0, 0, 0, 0, Flags);
                        }
                    }
                }
                finally { Interlocked.Exchange(ref queued, 0); }
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref queued, 0); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Dispose();
        foreach (var form in forms) { form.Deactivate -= OnWindowChanged; form.VisibleChanged -= OnWindowChanged; }
    }
}
