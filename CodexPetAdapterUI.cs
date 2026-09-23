using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Rendering;

namespace Marisa.CodexPetAdapter;

/// <summary>
/// Codex 桌宠的配置界面。
/// </summary>
public class CodexPetAdapterUI : ModuleUIBase<CodexPetRenderer, CodexPetRendererConfig>
{
    [Inject]
    public ConfigurationSystem ConfigurationSystem { get; set; } = null!;

    List<CodexPetModel> availablePets = new();
    bool switchingPets;
    string? petSwitchMessage;
    string lastScannedPath = string.Empty;

    protected override void OnInitialized()
    {
        RefreshPetList();
    }

    protected override void OnParametersSet()
    {
        // 配置页每次打开/切角色时都会重新传入 Configuration；这里保证列表不依赖桌宠是否激活。
        RefreshPetList();
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        int seq = 0;
        string effectivePath = GetEffectivePath();

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "padding: 20px; line-height: 1.6; font-size: 14px; color: #333;");

        builder.OpenElement(seq++, "h3");
        builder.AddAttribute(seq++, "style", "margin: 0 0 12px 0; font-size: 18px; font-weight: 600;");
        builder.AddContent(seq++, "Codex 桌宠");
        builder.CloseElement();

        builder.OpenElement(seq++, "p");
        builder.AddAttribute(seq++, "style", "margin: 0 0 16px 0; color: #666;");
        builder.AddContent(seq++, "默认读取插件自带宠物目录（ikaros）；勾选下方复选框可切回 %USERPROFILE%\\.codex\\pets。角色工作区里的修改保存到当前角色，全局设置页修改的是全局默认。");
        builder.CloseElement();

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "margin-bottom: 16px;");
        builder.OpenElement(seq++, "label");
        builder.AddAttribute(seq++, "style", "display: flex; align-items: center; gap: 8px; cursor: pointer;");
        builder.OpenElement(seq++, "input");
        builder.AddAttribute(seq++, "type", "checkbox");
        builder.AddAttribute(seq++, "checked", Configuration.UseDefaultPetsPath);
        builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnUseDefaultChanged));
        builder.CloseElement();
        builder.AddContent(seq++, "使用默认 Codex 宠物目录");
        builder.CloseElement();
        builder.CloseElement();

        if (!Configuration.UseDefaultPetsPath)
        {
            builder.OpenElement(seq++, "div");
            builder.AddAttribute(seq++, "style", "margin-bottom: 16px;");
            builder.OpenElement(seq++, "label");
            builder.AddAttribute(seq++, "style", "display: block; margin-bottom: 6px; font-weight: 500;");
            builder.AddContent(seq++, "自定义 pets 目录：");
            builder.CloseElement();
            builder.OpenElement(seq++, "input");
            builder.AddAttribute(seq++, "type", "text");
            builder.AddAttribute(seq++, "value", Configuration.CustomPetsPath);
            builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnCustomPathChanged));
            builder.AddAttribute(seq++, "placeholder", @"例如：E:\Download\Chat-WenDa\Alife\pets");
            builder.AddAttribute(seq++, "style", "width: 100%; padding: 8px 10px; border: 1px solid #d9d9d9; border-radius: 6px; box-sizing: border-box;");
            builder.CloseElement();
            builder.CloseElement();
        }

        builder.OpenElement(seq++, "p");
        builder.AddAttribute(seq++, "style", "margin: 0 0 16px 0; padding: 10px; background: #f5f5f5; border-radius: 6px; word-break: break-all;");
        builder.AddContent(seq++, $"当前目录：{effectivePath}");
        builder.AddContent(seq++, Directory.Exists(effectivePath)
            ? $"（已找到目录，检测到 {availablePets.Count} 只宠物）"
            : "（目录不存在）");
        builder.CloseElement();

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "margin-bottom: 16px;");
        builder.OpenElement(seq++, "label");
        builder.AddAttribute(seq++, "style", "display: block; margin-bottom: 6px; font-weight: 500;");
        builder.AddContent(seq++, Character == null ? "选择宠物（全局默认，作用于所有未单独设置的角色）：" : "选择宠物（仅当前角色）：");
        builder.CloseElement();
        builder.OpenElement(seq++, "select");
        builder.AddAttribute(seq++, "value", Configuration.SelectedPetId);
        builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnPetSelected));
        builder.AddAttribute(seq++, "style", "width: 100%; padding: 8px 10px; border: 1px solid #d9d9d9; border-radius: 6px; box-sizing: border-box;");

        builder.OpenElement(seq++, "option");
        builder.AddAttribute(seq++, "value", string.Empty);
        builder.AddContent(seq++, availablePets.Count == 0 ? "未检测到宠物" : "未选择宠物");
        builder.CloseElement();

        foreach (CodexPetModel pet in availablePets)
        {
            builder.OpenElement(seq++, "option");
            builder.AddAttribute(seq++, "value", pet.Id);
            builder.AddContent(seq++, $"{pet.DisplayName} ({pet.Id}, v{pet.FormatVersion})");
            builder.CloseElement();
        }

        builder.CloseElement();

        if (availablePets.Count == 0)
        {
            builder.OpenElement(seq++, "div");
            builder.AddAttribute(seq++, "style", "margin: -8px 0 16px 0; color: #d46b08;");
            builder.AddContent(seq++, "没有找到可用的 Codex 宠物。请确认目录包含 pet.json 完整的 .codex-pet 宠物文件夹，然后点击刷新。");
            builder.CloseElement();
        }

        builder.CloseElement();

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "margin-bottom: 16px;");
        builder.OpenElement(seq++, "label");
        builder.AddAttribute(seq++, "style", "display: block; margin-bottom: 6px; font-weight: 500;");
        builder.AddContent(seq++, $"缩放：{Configuration.Scale:F2}x");
        builder.CloseElement();
        builder.OpenElement(seq++, "input");
        builder.AddAttribute(seq++, "type", "range");
        builder.AddAttribute(seq++, "min", "0.5");
        builder.AddAttribute(seq++, "max", "2.0");
        builder.AddAttribute(seq++, "step", "0.05");
        builder.AddAttribute(seq++, "value", Configuration.Scale);
        builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnScaleChanged));
        builder.AddAttribute(seq++, "style", "width: 100%;");
        builder.CloseElement();
        builder.CloseElement();

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "margin-bottom: 16px;");
        builder.OpenElement(seq++, "label");
        builder.AddAttribute(seq++, "style", "display: block; margin-bottom: 6px; font-weight: 500;");
        builder.AddContent(seq++, $"动画速度：{Configuration.AnimationSpeed:F2}x");
        builder.CloseElement();
        builder.OpenElement(seq++, "input");
        builder.AddAttribute(seq++, "type", "range");
        builder.AddAttribute(seq++, "min", "0.25");
        builder.AddAttribute(seq++, "max", "3.0");
        builder.AddAttribute(seq++, "step", "0.05");
        builder.AddAttribute(seq++, "value", Configuration.AnimationSpeed);
        builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnAnimationSpeedChanged));
        builder.AddAttribute(seq++, "style", "width: 100%;");
        builder.CloseElement();
        builder.CloseElement();

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "margin-bottom: 16px;");
        builder.OpenElement(seq++, "label");
        builder.AddAttribute(seq++, "style", "display: block; margin-bottom: 6px; font-weight: 500;");
        builder.AddContent(seq++, $"气泡字号：{Configuration.BubbleFontSize:F1}px");
        builder.CloseElement();
        builder.OpenElement(seq++, "input");
        builder.AddAttribute(seq++, "type", "range");
        builder.AddAttribute(seq++, "min", "10");
        builder.AddAttribute(seq++, "max", "20");
        builder.AddAttribute(seq++, "step", "0.5");
        builder.AddAttribute(seq++, "value", Configuration.BubbleFontSize);
        builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnBubbleFontSizeChanged));
        builder.AddAttribute(seq++, "style", "width: 100%;");
        builder.CloseElement();
        builder.CloseElement();

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "margin-bottom: 16px;");
        builder.OpenElement(seq++, "label");
        builder.AddAttribute(seq++, "style", "display: block; margin-bottom: 6px; font-weight: 500;");
        builder.AddContent(seq++, $"气泡大小：{Configuration.BubbleScale:F2}x");
        builder.CloseElement();
        builder.OpenElement(seq++, "input");
        builder.AddAttribute(seq++, "type", "range");
        builder.AddAttribute(seq++, "min", "0.5");
        builder.AddAttribute(seq++, "max", "2.0");
        builder.AddAttribute(seq++, "step", "0.05");
        builder.AddAttribute(seq++, "value", Configuration.BubbleScale);
        builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnBubbleScaleChanged));
        builder.AddAttribute(seq++, "style", "width: 100%;");
        builder.CloseElement();
        builder.CloseElement();

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "margin-bottom: 16px;");
        builder.OpenElement(seq++, "label");
        builder.AddAttribute(seq++, "style", "display: flex; align-items: center; gap: 8px; cursor: pointer;");
        builder.OpenElement(seq++, "input");
        builder.AddAttribute(seq++, "type", "checkbox");
        builder.AddAttribute(seq++, "checked", Configuration.EnableGlow);
        builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnGlowEnabledChanged));
        builder.CloseElement();
        builder.AddContent(seq++, "对话时轻微增亮（默认关闭）");
        builder.CloseElement();
        builder.CloseElement();

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "margin-bottom: 16px;");
        builder.OpenElement(seq++, "label");
        builder.AddAttribute(seq++, "style", "display: flex; align-items: center; gap: 8px; cursor: pointer;");
        builder.OpenElement(seq++, "input");
        builder.AddAttribute(seq++, "type", "checkbox");
        builder.AddAttribute(seq++, "checked", Configuration.EnableOutline);
        builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnOutlineEnabledChanged));
        builder.CloseElement();
        builder.AddContent(seq++, "启用精灵描边");
        builder.CloseElement();
        builder.OpenElement(seq++, "input");
        builder.AddAttribute(seq++, "type", "color");
        builder.AddAttribute(seq++, "value", Configuration.OutlineColor);
        builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnOutlineColorChanged));
        builder.AddAttribute(seq++, "style", "width: 48px; height: 32px; padding: 0; border: 1px solid #d9d9d9; border-radius: 6px; background: transparent; margin-left: 8px;");
        builder.CloseElement();
        builder.CloseElement();

        CodexPetModel? selectedPet = availablePets.FirstOrDefault(pet => pet.Id == Configuration.SelectedPetId);
        bool supportsMouseLook = selectedPet?.SupportsLookDirections == true;

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "margin-bottom: 16px;");
        builder.OpenElement(seq++, "label");
        builder.AddAttribute(seq++, "style", "display: flex; align-items: center; gap: 8px; cursor: pointer;");
        builder.OpenElement(seq++, "input");
        builder.AddAttribute(seq++, "type", "checkbox");
        builder.AddAttribute(seq++, "checked", Configuration.MouseLookEnabled);
        if (!supportsMouseLook)
            builder.AddAttribute(seq++, "disabled", "disabled");
        builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnMouseLookEnabledChanged));
        builder.CloseElement();
        builder.AddContent(seq++, supportsMouseLook
            ? "开启鼠标跟随注视（v2）"
            : "鼠标跟随注视（当前宠物需为 v2）");
        builder.CloseElement();
        builder.CloseElement();

        if (supportsMouseLook)
        {
            builder.OpenElement(seq++, "div");
            builder.AddAttribute(seq++, "style", "margin-bottom: 16px;");
            builder.OpenElement(seq++, "label");
            builder.AddAttribute(seq++, "style", "display: block; margin-bottom: 6px; font-weight: 500;");
            builder.AddContent(seq++, $"鼠标停后回正：{Configuration.MouseLookHoldMilliseconds}ms");
            builder.CloseElement();
            builder.OpenElement(seq++, "input");
            builder.AddAttribute(seq++, "type", "number");
            builder.AddAttribute(seq++, "min", "0");
            builder.AddAttribute(seq++, "max", "5000");
            builder.AddAttribute(seq++, "step", "100");
            builder.AddAttribute(seq++, "value", Configuration.MouseLookHoldMilliseconds);
            builder.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, OnMouseLookHoldChanged));
            builder.AddAttribute(seq++, "style", "width: 120px;");
            builder.CloseElement();
            builder.CloseElement();
        }

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "style", "display: flex; gap: 10px; flex-wrap: wrap; align-items: center; margin-bottom: 12px;");

        builder.OpenElement(seq++, "button");
        builder.AddAttribute(seq++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, OnSwitchPetClicked));
        if (switchingPets || string.IsNullOrWhiteSpace(Configuration.SelectedPetId))
            builder.AddAttribute(seq++, "disabled", "disabled");
        builder.AddAttribute(seq++, "style", "padding: 8px 16px; background: #52c41a; color: white; border: none; border-radius: 6px; cursor: pointer;");
        builder.AddContent(seq++, switchingPets ? "切换中..." : "切换宠物");
        builder.CloseElement();

        builder.OpenElement(seq++, "button");
        builder.AddAttribute(seq++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, OnRefreshClicked));
        builder.AddAttribute(seq++, "style", "padding: 8px 16px; background: #1890ff; color: white; border: none; border-radius: 6px; cursor: pointer;");
        builder.AddContent(seq++, "刷新宠物列表");
        builder.CloseElement();

        builder.CloseElement();

        if (!string.IsNullOrWhiteSpace(petSwitchMessage))
        {
            builder.OpenElement(seq++, "p");
            builder.AddAttribute(seq++, "style", "margin: 0 0 16px 0; color: " + (petSwitchMessage.StartsWith("切换失败") ? "#d46b08" : "#389e0d") + ";");
            builder.AddContent(seq++, petSwitchMessage);
            builder.CloseElement();
        }

        builder.CloseElement();
    }

    void OnUseDefaultChanged(ChangeEventArgs e)
    {
        if (e.Value is bool value)
        {
            Configuration.UseDefaultPetsPath = value;

            // 这里刻意不清空 CustomPetsPath，方便用户以后切回自定义目录。
            if (Module != null)
            {
                Module.Configuration = Configuration;
                Module.RefreshPets();
            }

            PersistAndRefresh();
        }
    }

    void OnCustomPathChanged(ChangeEventArgs e)
    {
        if (e.Value is string value)
        {
            Configuration.UseDefaultPetsPath = false;
            Configuration.CustomPetsPath = value.Trim();

            if (Module != null)
            {
                Module.Configuration = Configuration;
                Module.RefreshPets();
            }

            PersistAndRefresh();
        }
    }

    async Task OnPetSelected(ChangeEventArgs e)
    {
        if (e.Value is string value)
            await SwitchSelectedPetAsync(value);
    }

    void OnScaleChanged(ChangeEventArgs e)
    {
        if (e.Value is string value && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale))
        {
            Configuration.Scale = Math.Clamp(scale, 0.5, 2.0);

            if (Module != null)
            {
                Module.Configuration = Configuration;
                Module.SetScale(scale);
            }

            PersistConfiguration();
            StateHasChanged();
        }
    }

    void OnAnimationSpeedChanged(ChangeEventArgs e)
    {
        if (e.Value is string value && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double speed))
        {
            Configuration.AnimationSpeed = Math.Clamp(speed, 0.25, 3.0);

            if (Module != null)
            {
                Module.Configuration = Configuration;
                Module.SetAnimationSpeed(speed);
            }

            PersistConfiguration();
            StateHasChanged();
        }
    }

    void OnBubbleFontSizeChanged(ChangeEventArgs e)
    {
        if (e.Value is string value && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fontSize))
        {
            Configuration.BubbleFontSize = Math.Clamp(fontSize, 10, 20);

            if (Module != null)
            {
                Module.Configuration = Configuration;
                Module.SetBubbleFontSize(fontSize);
            }

            PersistConfiguration();
            StateHasChanged();
        }
    }

    void OnBubbleScaleChanged(ChangeEventArgs e)
    {
        if (e.Value is string value && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bubbleScale))
        {
            Configuration.BubbleScale = Math.Clamp(bubbleScale, 0.5, 2.0);

            if (Module != null)
            {
                Module.Configuration = Configuration;
                Module.SetBubbleScale(bubbleScale);
            }

            PersistConfiguration();
            StateHasChanged();
        }
    }

    void OnGlowEnabledChanged(ChangeEventArgs e)
    {
        if (e.Value is bool enabled)
        {
            Configuration.EnableGlow = enabled;

            if (Module != null)
            {
                Module.Configuration = Configuration;
                Module.SetGlowEnabled(enabled);
            }

            PersistConfiguration();
            StateHasChanged();
        }
    }

    void OnOutlineEnabledChanged(ChangeEventArgs e)
    {
        if (e.Value is bool enabled)
        {
            Configuration.EnableOutline = enabled;

            if (Module != null)
            {
                Module.Configuration = Configuration;
                Module.SetOutlineEnabled(enabled);
            }

            PersistConfiguration();
            StateHasChanged();
        }
    }

    void OnOutlineColorChanged(ChangeEventArgs e)
    {
        if (e.Value is string color)
        {
            Configuration.OutlineColor = color;

            if (Module != null)
            {
                Module.Configuration = Configuration;
                Module.SetOutlineColor(color);
            }

            PersistConfiguration();
            StateHasChanged();
        }
    }

    void OnMouseLookHoldChanged(ChangeEventArgs e)
    {
        if (e.Value is string value && int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int holdMs))
        {
            Configuration.MouseLookHoldMilliseconds = Math.Clamp(holdMs, 0, 5000);

            if (Module != null)
                Module.Configuration = Configuration;

            PersistConfiguration();
            StateHasChanged();
        }
    }
    void OnMouseLookEnabledChanged(ChangeEventArgs e)
    {
        if (e.Value is bool enabled)
        {
            CodexPetModel? selectedPet = availablePets.FirstOrDefault(pet => pet.Id == Configuration.SelectedPetId);
            if (selectedPet?.SupportsLookDirections != true)
                return;

            Configuration.MouseLookEnabled = enabled;

            if (Module != null)
            {
                Module.Configuration = Configuration;
                Module.SetMouseLookEnabled(enabled);
            }

            PersistConfiguration();
            StateHasChanged();
        }
    }

    void OnRefreshClicked(MouseEventArgs _)
    {
        RefreshPetList(force: true);
        StateHasChanged();
    }

    async Task OnSwitchPetClicked(MouseEventArgs _)
    {
        await SwitchSelectedPetAsync(Configuration.SelectedPetId);
    }

    async Task SwitchSelectedPetAsync(string petId)
    {
        if (string.IsNullOrWhiteSpace(petId))
            return;

        if (Module == null)
        {
            Configuration.SelectedPetId = petId;
            PersistConfiguration();
            petSwitchMessage = "已保存选择；桌宠激活后将使用该宠物。";
            StateHasChanged();
            return;
        }

        switchingPets = true;
        petSwitchMessage = null;
        StateHasChanged();

        try
        {
            Module.Configuration = Configuration;
            bool switched = await Module.SelectPetAsync(petId);
            Configuration = Module.Configuration;
            PersistConfiguration();
            petSwitchMessage = switched
                ? (Module.CanLiveSwitch ? "宠物已动态切换。" : "已保存选择；桌宠激活后将使用该宠物。")
                : "切换失败：找不到指定宠物。";
        }
        catch (Exception e)
        {
            petSwitchMessage = "切换失败：" + e.Message;
        }
        finally
        {
            switchingPets = false;
            PersistConfiguration();
            StateHasChanged();
        }
    }

    string GetEffectivePath()
    {
        return CodexPetScanner.GetEffectivePath(Configuration);
    }

    void RefreshPetList(bool force = false)
    {
        string path = GetEffectivePath();
        if (force || availablePets.Count == 0 || !string.Equals(path, lastScannedPath, StringComparison.OrdinalIgnoreCase))
        {
            availablePets = CodexPetScanner.ScanPets(path);
            lastScannedPath = path;
        }
    }

    void PersistConfiguration()
    {
        ConfigurationSystem.SetConfiguration(typeof(CodexPetRenderer), Configuration, Character?.StorageKey ?? "");
    }

    void PersistAndRefresh()
    {
        PersistConfiguration();
        RefreshPetList(force: true);
        StateHasChanged();
    }
}



