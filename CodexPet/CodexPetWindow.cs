
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Alife.Client;
using Alife.Framework;
using System.Drawing;

namespace Marisa.CodexPetAdapter;

/// <summary>
/// Codex 桌宠的 Electron 透明窗口。
/// 宠物本体与消息气泡分别使用两个窗口，避免气泡区域撑大宠物本体窗口。
/// </summary>
public class CodexPetWindow : IDisposable
{
    public event Action<int, int>? PositionChanged;
    PetTopMostKeeper? topMostKeeper;
    readonly IAlifeApplication app;
    public CodexPetWindow(IAlifeApplication app) => this.app = app;

    public IAlifeWindow Window => window;
    public IAlifeWindow? BubbleWindow => bubbleWindow;
    public string ChannelId { get; } = "codex-pet-" + Guid.NewGuid().ToString("N");
    public double Dpi => dpi;

    const int baseWidth = 192;
    const int baseHeight = 208;

    double bubbleFontSize = 13;
    double bubbleScale = 0.6;
    int bubbleWindowWidth = 224;
    int bubbleWindowHeight = CalculateBubbleSpaceHeight(13, 0.6);
    Rectangle workArea;

    public int Width => currentWidth;
    public int Height => currentHeight;
    public int PositionX => positionX;
    public int PositionY => positionY;
    public int PetWidth => currentPetWidth;
    public int PetHeight => currentPetHeight;
    public int PetOffsetY => 0;

    public async Task CreateAsync(string wwwRoot, CodexPetBridge bridge, double scale,
        double fontSize = 13, double bubbleScale = 0.6)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("不支持的平台。");

        ApplyScaleValues(scale);
        ApplyBubbleStyleValues(fontSize, bubbleScale);

        dpi = app.GetDpiScale();
        var screen = app.GetScreenSize();
        workArea = new Rectangle(0, 0, (int)(screen.Width / dpi), (int)(screen.Height / dpi));

        defaultX = workArea.X + workArea.Width - currentWidth - 32;
        defaultY = workArea.Y + workArea.Height - currentHeight - 32;
        positionX = defaultX;
        positionY = defaultY;
        lastX = defaultX;
        lastY = defaultY;

        string petUrl = $"{new Uri(Path.Combine(wwwRoot, "index.html")).AbsoluteUri}?petChannel={ChannelId}&v={DateTime.UtcNow.Ticks}";
        string bubbleUrl = $"{new Uri(Path.Combine(wwwRoot, "bubble.html")).AbsoluteUri}?petChannel={ChannelId}&v={DateTime.UtcNow.Ticks}";

        window = await app.CreateWindowAsync(new AlifeWindowOptions {
            Title = "Codex Pet", Bounds = new Rectangle(positionX, positionY, currentWidth, currentHeight),
            TopMost = true, Resizable = false, Transparent = true, Url = petUrl
        });
        bubbleWindow = await app.CreateWindowAsync(new AlifeWindowOptions {
            Title = "Codex Pet Bubble", Bounds = new Rectangle(positionX, positionY, bubbleWindowWidth, bubbleWindowHeight),
            TopMost = true, Resizable = false, Transparent = true, Url = bubbleUrl
        });
        SetOpacity(window, 0);
        SetOpacity(bubbleWindow, 0);
        window.BoundsChanged += OnBoundsChanged;
        bridge.Attach(window, bubbleWindow);
        await window.Launch();
        await bubbleWindow.Launch();
        await bridge.WaitReadyAsync();
        await bubbleWindow.HideAsync();
        SetOpacity(bubbleWindow, 1);
        await window.ShowAsync();
        SetOpacity(window, 1);
        topMostKeeper = new PetTopMostKeeper(window, bubbleWindow);
        topMostKeeper.Refresh();
        UpdateBubbleBounds();

        bridge.SendMessage("setPetArea", new { height = 0 });
    }

    static void SetOpacity(IAlifeWindow target, double opacity)
    {
        if (target is not System.Windows.Forms.Form form) return;
        if (form.IsHandleCreated && form.InvokeRequired) form.Invoke(() => form.Opacity = opacity);
        else form.Opacity = opacity;
    }

    public void Dispose()
    {
        topMostKeeper?.Dispose();
        topMostKeeper = null;
        if (window == null) return;
        window.BoundsChanged -= OnBoundsChanged;
        if (bubbleWindow != null) _ = bubbleWindow.DisposeAsync();
        _ = window.DisposeAsync();
    }

    public void ResetPosition()
    {
        positionX = defaultX;
        positionY = defaultY;
        lastX = defaultX;
        lastY = defaultY;
        _ = window.SetBoundsAsync(new Rectangle {
            X = positionX,
            Y = positionY,
            Width = currentWidth,
            Height = currentHeight
        });
        UpdateBubbleBounds();
    }

    public void MoveTo(int x, int y)
    {
        positionX = x;
        positionY = y;
        _ = window.SetBoundsAsync(new Rectangle {
            X = positionX,
            Y = positionY,
            Width = currentWidth,
            Height = currentHeight
        });
        UpdateBubbleBounds();
    }

    public void SetScale(double newScale)
    {
        ApplyScaleValues(newScale);
        _ = window.SetBoundsAsync(new Rectangle {
            X = positionX,
            Y = positionY,
            Width = currentWidth,
            Height = currentHeight
        });
        UpdateBubbleBounds();
    }

    public void SetBubbleStyle(double fontSize, double newBubbleScale, double newScale)
    {
        ApplyScaleValues(newScale);
        ApplyBubbleStyleValues(fontSize, newBubbleScale);
        UpdateBubbleBounds();
    }

    public void ShowBubble()
    {
        if (bubbleWindow == null)
            return;
        _ = bubbleWindow.SetTopMostAsync(true);
        _ = ShowBubbleAsync();
    }

    async Task ShowBubbleAsync()
    {
        if (bubbleWindow == null) return;
        await bubbleWindow.ShowAsync();
        topMostKeeper?.Refresh();
    }

    public void HideBubble()
    {
        if (bubbleWindow != null) _ = bubbleWindow.HideAsync();
    }

    void UpdateBubbleBounds()
    {
        if (bubbleWindow == null)
            return;

        int x = positionX + (currentWidth - bubbleWindowWidth) / 2;
        int y = positionY - bubbleWindowHeight - 8;
        if (y < workArea.Y)
            y = positionY + currentHeight + 8;

        if (x < workArea.X)
            x = workArea.X;
        if (x + bubbleWindowWidth > workArea.X + workArea.Width)
            x = workArea.X + workArea.Width - bubbleWindowWidth;

        _ = bubbleWindow.SetBoundsAsync(new Rectangle {
            X = x,
            Y = y,
            Width = bubbleWindowWidth,
            Height = bubbleWindowHeight
        });
    }

    void ApplyScaleValues(double newScale)
    {
        scale = newScale;
        currentPetWidth = (int)Math.Round(baseWidth * scale);
        currentPetHeight = (int)Math.Round(baseHeight * scale);
        currentWidth = currentPetWidth;
        currentHeight = currentPetHeight;
    }

    void ApplyBubbleStyleValues(double fontSize, double frameScale)
    {
        bubbleFontSize = Math.Clamp(fontSize, 10, 20);
        bubbleScale = Math.Clamp(frameScale, 0.6, 2.0);
        bubbleWindowWidth = Math.Max(224, (int)Math.Ceiling(8 * bubbleFontSize + 28 * bubbleScale + 32));
        bubbleWindowHeight = CalculateBubbleSpaceHeight(bubbleFontSize, bubbleScale);
    }

    static int CalculateBubbleSpaceHeight(double fontSize, double frameScale)
    {
        return (int)Math.Ceiling(6 + 4 + 6.0 * fontSize + 20 * frameScale + 2);
    }

    void OnBoundsChanged(Rectangle newBounds)
    {
        int dx = newBounds.X - lastX;
        int dy = newBounds.Y - lastY;
        lastX = newBounds.X;
        lastY = newBounds.Y;
        positionX = newBounds.X;
        positionY = newBounds.Y;
        UpdateBubbleBounds();

        if (dx != 0 || dy != 0)
            PositionChanged?.Invoke(dx, dy);
    }

    IAlifeWindow window = null!;
    IAlifeWindow? bubbleWindow;
    double dpi = 1.0;
    double scale = 1.0;
    int currentWidth;
    int currentHeight;
    int currentPetWidth;
    int currentPetHeight;
    int positionX;
    int positionY;
    int lastX;
    int lastY;
    int defaultX;
    int defaultY;
}
