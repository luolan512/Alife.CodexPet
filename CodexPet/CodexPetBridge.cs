
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Client;
using System.Threading;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Marisa.CodexPetAdapter;

/// <summary>
/// Codex 桌宠主进程与渲染进程之间的 Electron IPC 桥。
/// 同一条消息会广播到宠物本体窗口和消息气泡窗口。
/// </summary>
public sealed class CodexPetBridge : IDisposable
{
    public event Action<string, JsonElement>? OnMessage;

    public void SendMessage(string type, object? payload = null)
    {
        try
        {
            JObject envelope = new() { ["type"] = type };
            if (payload != null)
            {
                JObject payloadObj = JObject.FromObject(payload);
                foreach (KeyValuePair<string, JToken?> kvp in payloadObj)
                    envelope[kvp.Key] = kvp.Value;
            }

            string message = envelope.ToString(Newtonsoft.Json.Formatting.Indented);
            _ = window.Window.PostMessageAsync(message);
            if (window.BubbleWindow != null)
                _ = window.BubbleWindow.PostMessageAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发送 Codex 桌宠消息失败");
        }
    }

    public async Task<string?> ExecuteJavaScriptAsync(string script)
    {
        try
        {
            return await window.Window.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "执行 Codex 桌宠脚本失败");
            return null;
        }
    }

    public CodexPetBridge(CodexPetWindow window, ILogger<CodexPetRenderer> logger)
    {
        this.window = window;
        this.logger = logger;

    }

    public void Dispose()
    {
        if (mainWindow != null) mainWindow.MessageReceived -= OnMainMessage;
        if (bubbleWindow != null) bubbleWindow.MessageReceived -= OnBubbleMessage;
    }

    IAlifeWindow? mainWindow, bubbleWindow;
    readonly TaskCompletionSource mainReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource bubbleReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Attach(IAlifeWindow main, IAlifeWindow bubble)
    {
        mainWindow = main; bubbleWindow = bubble;
        main.MessageReceived += OnMainMessage;
        bubble.MessageReceived += OnBubbleMessage;
    }
    void OnMainMessage(string message) { MarkReady(message, mainReady); OnIpcMessage(message); }
    void OnBubbleMessage(string message) { MarkReady(message, bubbleReady); OnIpcMessage(message); }
    static void MarkReady(string message, TaskCompletionSource ready)
    {
        try { using var doc = JsonDocument.Parse(message); if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "ready") ready.TrySetResult(); } catch (JsonException) { }
    }
    public async Task WaitReadyAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Task.WhenAll(mainReady.Task, bubbleReady.Task).WaitAsync(timeout.Token);
    }

    readonly CodexPetWindow window;
    readonly ILogger<CodexPetRenderer> logger;

    void OnIpcMessage(object? payload)
    {
        if (payload is not string json || string.IsNullOrEmpty(json))
            return;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeProp))
                return;

            string? type = typeProp.GetString();
            if (type == null)
                return;

            try
            {
                OnMessage?.Invoke(type, root);
            }
            catch (Exception e)
            {
                logger.LogError(e, "执行 Codex 桌宠前端消息回调失败");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "解析 Codex 桌宠前端消息失败");
        }
    }
}
