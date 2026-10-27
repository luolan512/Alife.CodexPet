using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.DeskPet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Marisa.CodexPetAdapter;

/// <summary>
/// Codex 精灵表宠物的 <see cref="IDeskPet"/> 实现。
/// 通过 Electron 透明窗口渲染 8x11 精灵表，并暴露标准动作与注视方向。
/// 同时负责扫描本地 Codex 宠物包与管理配置。
/// </summary>
[Module("Codex 桌宠",
    """
    使用 Codex pet 精灵宠物作为桌宠身体。
    支持 idle、running、waving、jumping 等标准动作，以及 16 方向注视。
    可读取 %USERPROFILE%\\.codex\\pets，也支持手动指定目录。
    获取更多宠物可以去第三方平台下载https://codex-pets.net/
    """,
    defaultCategory: "Alife 官方/模型接入/桌宠模型",
    EditorUI = typeof(CodexPetAdapterUI))]
public class CodexPetRenderer(
    IAlifeApplication alifeApplication,
    PluginSystem pluginSystem,
    StorageSystem storageSystem,
    ConfigurationSystem configurationSystem,
    ILogger<CodexPetRenderer> logger) :
    ChatBehaviour,
    IConfigurable<CodexPetRendererConfig>,
    IDeskPet
{
    public CodexPetRendererConfig Configuration { get; set; } = null!;

    public event Action<string>? OnInput;
    public event Action<string>? OnInteracted;

    CodexPetModel? currentModel;

    public string[] SupportedExpressions =>
        currentModel?.SupportsLookDirections == true ? expressionNames : lookFrontOnlyNames;
    public string[] SupportedMotions => motionNames;

    public string EffectivePetsPath => CodexPetScanner.GetEffectivePath(Configuration);
    public IReadOnlyList<CodexPetModel> AvailablePets => availablePets;
    public bool SupportsMouseLook => currentModel?.SupportsLookDirections == true;
    public bool CanLiveSwitch => bridge != null && window != null;

    public Task ShowUsing(bool isUsing)
    {
        bridge?.SendMessage("setUsing", new { isUsing });
        return Task.CompletedTask;
    }

    public Task ShowSubtitle(string? subtitle)
    {
        if (string.IsNullOrEmpty(subtitle))
            bridge?.SendMessage("hideSubtitle");
        else
            bridge?.SendMessage("showSubtitle", new { text = subtitle });
        return Task.CompletedTask;
    }

    public Task ShowExpression(string? expression)
    {
        expression = expression?.Trim();
        if (string.IsNullOrWhiteSpace(expression))
        {
            bridge?.SendMessage("clearLookDirection");
            return Task.CompletedTask;
        }

        if (expressionNameToFrame.TryGetValue(expression, out int frame))
        {
            if (frame < 0)
            {
                bridge?.SendMessage("clearLookDirection");
            }
            else if (currentModel?.SupportsLookDirections == true)
            {
                bridge?.SendMessage("setLookDirection", new { frame });
            }
            // v1 没有 rows 9/10；忽略方向注视，避免越界空白。
        }
        else
        {
            bridge?.SendMessage("playMotion", new { row = wavingRow, loop = false });
        }
        return Task.CompletedTask;
    }

    public Task ShowMotion(string? motion)
    {
        motion = motion?.Trim();
        if (string.IsNullOrWhiteSpace(motion))
            return Task.CompletedTask;

        if (!motionNameToRow.TryGetValue(motion, out int row))
            return Task.CompletedTask;

        bool loop = IsLoopingRow(row);
        bridge?.SendMessage("playMotion", new { row, loop });
        return Task.CompletedTask;
    }

    public Task<Vector2> GetPosition()
    {
        if (window == null)
            return Task.FromResult(Vector2.Zero);

        float dpi = (float)window.Dpi;
        Vector2 position = new(
            (window.PositionX + window.Width / 2.0f) * dpi,
            (window.PositionY + window.PetOffsetY + window.PetHeight / 2.0f) * dpi);
        return Task.FromResult(position);
    }

    public async Task Move(Vector2 offset, float seconds)
    {
        if (window == null)
            return;

        float dpi = (float)window.Dpi;
        int startX = window.PositionX;
        int startY = window.PositionY;
        int endX = startX + (int)Math.Round(offset.X / dpi);
        int endY = startY + (int)Math.Round(offset.Y / dpi);
        long startTick = Environment.TickCount64;
        int durationMs = Math.Max(1, (int)(seconds * 1000));

        if (offset.X > 0)
            await ShowMotion("running-right");
        else if (offset.X < 0)
            await ShowMotion("running-left");

        while (true)
        {
            long elapsed = Environment.TickCount64 - startTick;
            double t = Math.Min(1.0, (double)elapsed / durationMs);
            double ease = t * (2 - t);
            int x = (int)Math.Round(startX + (endX - startX) * ease);
            int y = (int)Math.Round(startY + (endY - startY) * ease);

            window.MoveTo(x, y);
            if (t >= 1.0)
                break;
            await Task.Delay(16);
        }

        await ShowMotion("idle");
    }

    public void RefreshPets()
    {
        availablePets = CodexPetScanner.ScanPets(EffectivePetsPath);
    }

    public async Task<bool> SelectPetAsync(string petId)
    {
        petId = petId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(petId))
            return false;

        string previousPetId = currentModel?.Id ?? Configuration.SelectedPetId;

        if (availablePets.Count == 0)
            RefreshPets();

        CodexPetModel? model = availablePets.FirstOrDefault(item => item.Id == petId);
        if (model == null)
        {
            RefreshPets();
            model = availablePets.FirstOrDefault(item => item.Id == petId);
        }

        if (model == null)
        {
            logger.LogWarning("动态切换宠物失败：找不到宠物 {PetId}", petId);
            Configuration.SelectedPetId = previousPetId;
            SaveConfiguration();
            return false;
        }

        // 桌宠还没创建 Electron 窗口时，只保存选择；下次激活时会使用新宠物。
        if (bridge == null || window == null)
        {
            Configuration.SelectedPetId = petId;
            SaveConfiguration();
            return true;
        }

        await switchPetSemaphore.WaitAsync(DestroyCancellationToken);
        try
        {
            if (currentModel?.Id == petId)
            {
                Configuration.SelectedPetId = petId;
                SaveConfiguration();
                return true;
            }

            // 前端加载成功后再更新 currentModel 和配置；失败时保持原宠物。
            await LoadModelAsync(model);
            currentModel = model;
            Configuration.SelectedPetId = petId;
            SaveConfiguration();

            // 新宠物可能是 v1/v2，重新同步视觉状态和鼠标跟随能力。
            ApplyVisualStyle();
            bridge.SendMessage("setAnimationSpeed", new { speed = Configuration.AnimationSpeed });
            StopMouseLookLoop();
            StartMouseLookLoop();
            return true;
        }
        catch
        {
            Configuration.SelectedPetId = currentModel?.Id ?? Configuration.SelectedPetId;
            SaveConfiguration();
            throw;
        }
        finally
        {
            switchPetSemaphore.Release();
        }
    }

    public void SetCustomPetsPath(string path)
    {
        Configuration.UseDefaultPetsPath = false;
        Configuration.CustomPetsPath = path;
        SaveConfiguration();
        RefreshPets();
    }

    public void UseDefaultPetsPath()
    {
        Configuration.UseDefaultPetsPath = true;
        Configuration.CustomPetsPath = string.Empty;
        SaveConfiguration();
        RefreshPets();
    }

    public void SetScale(double scale)
    {
        Configuration.Scale = Math.Clamp(scale, 0.5, 2.0);
        SaveConfiguration();
        ApplyScale();
    }

    public void SetAnimationSpeed(double speed)
    {
        Configuration.AnimationSpeed = Math.Clamp(speed, 0.25, 3.0);
        SaveConfiguration();
        bridge?.SendMessage("setAnimationSpeed", new { speed = Configuration.AnimationSpeed });
    }

    public void SetBubbleFontSize(double fontSize)
    {
        Configuration.BubbleFontSize = Math.Clamp(fontSize, 10, 20);
        SaveConfiguration();
        ApplyBubbleStyle();
    }

    public void SetBubbleScale(double bubbleScale)
    {
        Configuration.BubbleScale = Math.Clamp(bubbleScale, 0.6, 2.0);
        SaveConfiguration();
        ApplyBubbleStyle();
    }

    public void SetGlowEnabled(bool enabled)
    {
        Configuration.EnableGlow = enabled;
        SaveConfiguration();
        ApplyVisualStyle();
    }

    public void SetOutlineEnabled(bool enabled)
    {
        Configuration.EnableOutline = enabled;
        SaveConfiguration();
        ApplyVisualStyle();
    }

    public void SetOutlineColor(string color)
    {
        Configuration.OutlineColor = string.IsNullOrWhiteSpace(color) ? "#000000" : color.Trim();
        SaveConfiguration();
        ApplyVisualStyle();
    }

    public void SetMouseLookEnabled(bool enabled)
    {
        Configuration.MouseLookEnabled = enabled;
        SaveConfiguration();
        bridge?.SendMessage("setMouseLookEnabled", new { enabled });

        if (enabled)
            StartMouseLookLoop();
        else
            StopMouseLookLoop();
    }

    protected override async Task OnAwake()
    {
        LoadConfiguration();
        RefreshPets();

        CodexPetModel? model = ResolveSelectedModel();
        if (model == null)
        {
            logger.LogWarning("未找到可用的 Codex 宠物，请检查 pets 目录配置。");
            return;
        }

        currentModel = model;

        ServiceCollection services = new();
        services.AddSingleton(logger);
        services.AddSingleton(alifeApplication);
        services.AddSingleton(storageSystem);
        services.AddSingleton(model);
        services.AddSingleton<CodexPetWindow>();
        services.AddSingleton<CodexPetBridge>();
        services.AddSingleton<InputEventCallback>(text => OnInput?.Invoke(text));
        services.AddSingleton<InteractedEventCallback>(text => OnInteracted?.Invoke(text));

        provider = services.BuildServiceProvider();

        try
        {
            window = provider.GetRequiredService<CodexPetWindow>();
            bridge = provider.GetRequiredService<CodexPetBridge>();
            bridge.OnMessage += OnBridgeMessage;
            window.PositionChanged += OnWindowPositionChanged;
            ChatBot.ChatSent += OnChatSent;

            string wwwRoot = Path.Combine(
                pluginSystem.PluginContext.GetPluginDirectoryPath("Marisa.CodexPetAdapter"),
                "Resources",
                "CodexPet");

            await window.CreateAsync(
                wwwRoot,
                bridge,
                Configuration.Scale,
                Configuration.BubbleFontSize,
                Configuration.BubbleScale);
            bridge.SendMessage("setAnimationSpeed", new { speed = Configuration.AnimationSpeed });
            bridge.SendMessage("setBubbleStyle", new {
                fontSize = Configuration.BubbleFontSize,
                scale = Configuration.BubbleScale
            });
            bridge.SendMessage("setVisualStyle", new {
                glow = Configuration.EnableGlow,
                outline = Configuration.EnableOutline,
                outlineColor = Configuration.OutlineColor
            });
            bridge.SendMessage("setMouseLookEnabled", new {
                enabled = Configuration.MouseLookEnabled
            });
            await LoadModelAsync(model);
            StartMouseLookLoop();
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    protected override async Task OnDestroy()
    {
        isGrabbing = false;
        StopMouseLookLoop();
        ChatBot.ChatSent -= OnChatSent;
        currentModel = null;
        if (bridge != null)
            bridge.OnMessage -= OnBridgeMessage;
        if (window != null)
            window.PositionChanged -= OnWindowPositionChanged;
        if (provider != null)
            await provider.DisposeAsync();
    }

    // ---- 光标跟随拖拽（主进程内闭环，光标与窗口位置单位天然一致，无 DPI 漂移） ----

    void OnChatSent(string _)
    {
        // Alife 核心的新 ChatAsync 默认 breakLast=true 会打断旧流；
        // 桌宠侧同步清掉旧气泡队列和一次性动作，避免画面继续播旧回复。
        bridge?.SendMessage("interruptPlayback");
    }

    void OnBridgeMessage(string type, System.Text.Json.JsonElement payload)
    {
        switch (type)
        {
            case "grabStart":
                _ = StartGrabAsync();
                break;
            case "grabEnd":
                isGrabbing = false;
                break;
            case "petClick":
                HandlePetClick();
                break;
            case "bubbleShow":
                window?.ShowBubble();
                break;
            case "bubbleHide":
                window?.HideBubble();
                break;
            case "bubbleClick":
                bridge?.SendMessage("advanceSubtitle");
                break;
            case "petInterrupt":
                HandlePetInterrupt();
                break;
        }
    }

    System.Drawing.Point CursorInWindowCoordinates() {
        var point = alifeApplication.GetCursorPosition();
        var dpi = alifeApplication.GetDpiScale();
        return new System.Drawing.Point((int)(point.X / dpi), (int)(point.Y / dpi));
    }

    async Task StartGrabAsync()
    {
        if (isGrabbing || window == null)
            return;

        try
        {
            System.Drawing.Point cursor = CursorInWindowCoordinates();
            grabOffsetX = cursor.X - window.PositionX;
            grabOffsetY = cursor.Y - window.PositionY;
            isGrabbing = true;
            await GrabLoopAsync();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "拖拽跟随异常");
            isGrabbing = false;
        }
    }

    async Task GrabLoopAsync()
    {
        while (isGrabbing && window != null && DestroyCancellationToken.IsCancellationRequested == false)
        {
            System.Drawing.Point cursor = CursorInWindowCoordinates();
            window.MoveTo(cursor.X - grabOffsetX, cursor.Y - grabOffsetY);
            await Task.Delay(16);
        }
    }

    // ---- 双击打断当前对话；单击仍触发惊喜动作 ----

    void HandlePetInterrupt()
    {
        bridge?.SendMessage("interruptPlayback");

        if (ChatBot.IsChatOccupied)
        {
            ChatBot.ChatBreakTokenSource.Cancel();
            logger.LogInformation("用户双击桌宠，已打断当前对话");
        }
    }

    // ---- 点击随机动画（动画期间 + 结束后 1 秒内冷却） ----

    void HandlePetClick()
    {
        if (bridge == null)
            return;

        long now = Environment.TickCount64;
        if (now < clickCooldownUntil)
            return;

        int[] rows = { 3, 4, 6, 7, 8 };
        int row = rows[random.Next(rows.Length)];
        int durationMs = clickMotionDurationMs.GetValueOrDefault(row, 2500);

        clickCooldownUntil = now + durationMs + 1000;
        bridge.SendMessage("playRandomMotion", new { row, durationMs });
    }

    static bool IsLoopingRow(int row) => row switch
    {
        3 => false,
        4 => false,
        5 => false,
        _ => true
    };

    static readonly string[] motionNames = new[]
    {
        "idle",
        "running-right",
        "running-left",
        "waving",
        "jumping",
        "failed",
        "waiting",
        "running",
        "review"
    };

    static readonly Dictionary<string, int> motionNameToRow = new()
    {
        { "idle", 0 },
        { "running-right", 1 },
        { "running-left", 2 },
        { "waving", 3 },
        { "jumping", 4 },
        { "failed", 5 },
        { "waiting", 6 },
        { "running", 7 },
        { "review", 8 }
    };

    static readonly string[] lookFrontOnlyNames = new[]
    {
        "look-front"
    };

    static readonly string[] expressionNames = new[]
    {
        "look-up",
        "look-up-right",
        "look-right",
        "look-down-right",
        "look-down",
        "look-down-left",
        "look-left",
        "look-up-left",
        "look-front"
    };

    static readonly Dictionary<string, int> expressionNameToFrame = new()
    {
        // Codex v2 rows 9/10 have 16 clockwise directions (22.5 degrees each).
        // Expose the 8 common directions on even frames to avoid 45-degree mapping errors.
        { "look-up", 0 },
        { "look-up-right", 2 },
        { "look-right", 4 },
        { "look-down-right", 6 },
        { "look-down", 8 },
        { "look-down-left", 10 },
        { "look-left", 12 },
        { "look-up-left", 14 },
        { "look-front", -1 }
    };

    /// <summary>点击可随机触发的动作行 → 播放时长（ms）。</summary>
    static readonly Dictionary<int, int> clickMotionDurationMs = new()
    {
        { 3, 700 },   // waving
        { 4, 840 },   // jumping
        { 6, 2500 },  // waiting
        { 7, 2500 },  // running
        { 8, 2500 }   // review
    };

    const int wavingRow = 3;

    readonly Random random = new();
    long clickCooldownUntil;
    bool isGrabbing;
    int grabOffsetX;
    int grabOffsetY;

    ServiceProvider? provider;
    CodexPetWindow? window;
    CodexPetBridge? bridge;
    List<CodexPetModel> availablePets = new();
    readonly SemaphoreSlim switchPetSemaphore = new(1, 1);

    CancellationTokenSource? mouseLookCts;
    Task? mouseLookTask;
    int lastMouseLookFrame = -2;
    System.Drawing.Point? lastMouseLookCursor;
    long lastMouseLookMovementTick;

    /// <summary>窗口位置变化（含主进程拖拽/Move）→ 驱动移动动画方向。</summary>
    void OnWindowPositionChanged(int dx, int _)
    {
        if (Math.Abs(dx) <= 1)
            return;

        string direction = dx > 0 ? "right" : "left";
        bridge?.SendMessage("setDragMotion", new { direction });
    }

    void LoadConfiguration()
    {
        // 个人角色配置优先，与框架注入规则一致；无个人配置时回退全局。
        Configuration = (CodexPetRendererConfig)configurationSystem.GetConfiguration(typeof(CodexPetRenderer), Character?.StorageKey ?? "");
    }

    void SaveConfiguration()
    {
        configurationSystem.SetConfiguration(typeof(CodexPetRenderer), Configuration, Character?.StorageKey ?? "");
    }

    void ApplyScale()
    {
        window?.SetScale(Configuration.Scale);
    }

    void ApplyBubbleStyle()
    {
        bridge?.SendMessage("setBubbleStyle", new {
            fontSize = Configuration.BubbleFontSize,
            scale = Configuration.BubbleScale
        });
        window?.SetBubbleStyle(
            Configuration.BubbleFontSize,
            Configuration.BubbleScale,
            Configuration.Scale);
    }

    void ApplyVisualStyle()
    {
        bridge?.SendMessage("setVisualStyle", new {
            glow = Configuration.EnableGlow,
            outline = Configuration.EnableOutline,
            outlineColor = Configuration.OutlineColor
        });
    }

    // ---- 鼠标跟随注视（仅 v2 有 rows 9/10） ----

    void StartMouseLookLoop()
    {
        if (mouseLookCts != null)
            return;
        if (Configuration.MouseLookEnabled == false ||
            currentModel?.SupportsLookDirections != true ||
            window == null ||
            bridge == null)
        {
            return;
        }

        lastMouseLookFrame = -2;
        lastMouseLookCursor = null;
        lastMouseLookMovementTick = 0;
        mouseLookCts = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
        mouseLookTask = MouseLookLoopAsync(mouseLookCts.Token);
    }

    void StopMouseLookLoop()
    {
        mouseLookCts?.Cancel();
        mouseLookCts?.Dispose();
        mouseLookCts = null;
        mouseLookTask = null;
        lastMouseLookFrame = -2;
        lastMouseLookCursor = null;
        lastMouseLookMovementTick = 0;
        bridge?.SendMessage("setMouseLookFrame", new { frame = -1 });
    }

    async Task MouseLookLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (cancellationToken.IsCancellationRequested == false)
            {
                await UpdateMouseLookFrameAsync();
                await Task.Delay(80, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "鼠标跟随注视循环中断");
        }
    }

    async Task UpdateMouseLookFrameAsync()
    {
        if (window == null || bridge == null || Configuration.MouseLookEnabled == false)
            return;

        const double movementThreshold = 3.0;
        int lookHoldMilliseconds = Math.Clamp(Configuration.MouseLookHoldMilliseconds, 0, 5000);

        try
        {
            System.Drawing.Point cursor = CursorInWindowCoordinates();
            long now = Environment.TickCount64;

            // 第一次采样只记录位置，不立刻注视；后续鼠标真正移动才触发“看一眼”。
            if (lastMouseLookCursor == null)
            {
                lastMouseLookCursor = cursor;
                lastMouseLookMovementTick = now;
                return;
            }

            double movedX = cursor.X - lastMouseLookCursor.Value.X;
            double movedY = cursor.Y - lastMouseLookCursor.Value.Y;
            double movement = Math.Sqrt(movedX * movedX + movedY * movedY);

            if (movement < movementThreshold)
            {
                // 鼠标停止后保留注视一小会儿，再回到待机动画。
                if (lastMouseLookFrame != -2 && now - lastMouseLookMovementTick >= lookHoldMilliseconds)
                {
                    lastMouseLookFrame = -2;
                    bridge.SendMessage("setMouseLookFrame", new { frame = -1 });
                }
                return;
            }

            lastMouseLookCursor = cursor;
            lastMouseLookMovementTick = now;

            double centerX = window.PositionX + window.Width / 2.0;
            double centerY = window.PositionY + window.PetOffsetY + window.PetHeight / 2.0;
            double dx = cursor.X - centerX;
            double dy = cursor.Y - centerY;
            double distance = Math.Sqrt(dx * dx + dy * dy);

            // 太靠近身体时清除注视，避免视角来回抖动。
            if (distance < 80)
            {
                if (lastMouseLookFrame != -2)
                {
                    lastMouseLookFrame = -2;
                    bridge.SendMessage("setMouseLookFrame", new { frame = -1 });
                }
                return;
            }

            // Codex v2 顺序是从正上开始顺时针，每帧 22.5 度。
            double degrees = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
            if (degrees < 0)
                degrees += 360.0;

            int frame = (int)Math.Floor((degrees + 11.25) / 22.5) % 16;
            if (frame != lastMouseLookFrame)
            {
                lastMouseLookFrame = frame;
                bridge.SendMessage("setMouseLookFrame", new { frame });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "读取鼠标位置失败");
        }
    }

    CodexPetModel? ResolveSelectedModel()
    {
        if (availablePets.Count == 0)
            return null;

        CodexPetModel? selected = availablePets
            .FirstOrDefault(p => p.Id == Configuration.SelectedPetId);

        if (selected != null)
            return selected;

        // 未配置或配置丢失时优先回退插件自带的 ikaros，避免首次运行空白。
        return availablePets.FirstOrDefault(p =>
            string.Equals(System.IO.Path.GetFileName(p.RootDirectory), "ikaros", StringComparison.OrdinalIgnoreCase))
            ?? availablePets[0];
    }

    async Task LoadModelAsync(CodexPetModel model)
    {
        TaskCompletionSource loadedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(string type, System.Text.Json.JsonElement payload)
        {
            if (type == "loaded")
            {
                loadedTcs.TrySetResult();
            }
            else if (type == "error")
            {
                string message = payload.TryGetProperty("message", out System.Text.Json.JsonElement value)
                    ? value.GetString() ?? "未知加载错误"
                    : "未知加载错误";
                loadedTcs.TrySetException(new Exception("宠物精灵表加载失败：" + message));
            }
        }

        bridge.OnMessage += Handler;
        try
        {
            FileInfo spriteFile = new(model.SpritesheetAbsolutePath);
            string spriteUrl = new Uri(model.SpritesheetAbsolutePath).AbsoluteUri;
            spriteUrl += "?v=" + spriteFile.LastWriteTimeUtc.Ticks.ToString();

            bridge.SendMessage("load", new {
                url = spriteUrl,
                version = model.FormatVersion,
                rows = model.AtlasRowCount
            });

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await loadedTcs.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            bridge.OnMessage -= Handler;
        }
    }
}

public class CodexPetRendererConfig
{
    public bool UseDefaultPetsPath { get; set; } = false;
    public string CustomPetsPath { get; set; } = @"Storage\Plugins\Marisa.CodexPetAdapter\pet";
    public string SelectedPetId { get; set; } = string.Empty;
    public bool ShowSubtitle { get; set; } = true;
    public double Scale { get; set; } = 0.6;
    public double AnimationSpeed { get; set; } = 1.0;
    public double BubbleFontSize { get; set; } = 13;
    public double BubbleScale { get; set; } = 0.6;
    public bool EnableGlow { get; set; } = false;
    public bool EnableOutline { get; set; } = false;
    public string OutlineColor { get; set; } = "#000000";
    public bool MouseLookEnabled { get; set; } = false;
    public int MouseLookHoldMilliseconds { get; set; } = 600;
}






