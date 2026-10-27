# Codex 桌宠 · Alife.CodexPet

面向 **Windows / Alife 5.1**，当前版本 **5.1.1**。插件 ID 为 `Marisa.CodexPetAdapter`。

让 Alife 使用 Codex 格式的精灵表宠物作为桌宠。

## 功能

- 自动扫描 `%USERPROFILE%\\.codex\\pets` 下的 Codex v2 宠物。
- 支持手动指定 pets 目录。
- 将 Codex 宠物的标准动作（idle、running、waving、jumping 等）暴露给 Alife AI。
- 支持 16 方向注视作为 Expression 调用。
- 复用 Alife 官方原生窗口与 WebView2 渲染 8x11 精灵表，桌宠和气泡使用独立窗口。

## 文件结构

```text
Marisa.CodexPetAdapter/
├── manifest.json
├── CodexPetRenderer.cs             # 插件入口 + IDeskPet 实现
├── CodexPetRendererConfig.cs       # （内嵌在 CodexPetRenderer.cs 中）
├── CodexPetAdapterUI.cs            # 配置界面
├── CodexPet/
│   ├── CodexPetModel.cs            # 宠物数据模型
│   ├── CodexPetScanner.cs          # 目录扫描器
│   ├── CodexPetBridge.cs           # 原生窗口消息桥
│   └── CodexPetWindow.cs           # 官方原生窗口
└── Resources/CodexPet/
    ├── index.html
    ├── style.css
    └── main.js
```

## 使用方式

1. 在 Alife 模块设置中启用 **Codex 桌宠**。
2. 在配置界面中选择或指定 pets 目录。
3. 选择要显示的宠物。
4. 启用 **桌宠控制**（DeskPetService）模块，AI 即可驱动 Codex 宠物。

## 动作映射

| Alife Motion | Codex 精灵表行 | 循环 |
|---|---|---|
| idle | 0 | 是 |
| running-right | 1 | 是 |
| running-left | 2 | 是 |
| waving | 3 | 否 |
| jumping | 4 | 否 |
| failed | 5 | 否 |
| waiting | 6 | 是 |
| running | 7 | 是 |
| review | 8 | 是 |

## 表情映射（注视方向）

| Alife Expression | 方向 |
|---|---|
| look-up | 0 |
| look-up-right | 1 |
| look-right | 2 |
| look-down-right | 3 |
| look-down | 4 |
| look-down-left | 5 |
| look-left | 6 |
| look-up-left | 7 |
| look-front | 清除注视，回到 idle |

## 注意事项

- 仅支持 `spriteVersionNumber: 2` 的 Codex 宠物。
- 路径解析使用 `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)`，不依赖硬编码用户名。
- 配置通过 `ConfigurationSystem` 持久化。
- 本插件只提供一个模块入口 **Codex 桌宠**，不再拆分为配置模块和渲染模块。


## 安装与更新

依赖 `Alife.Function.DeskPet`。将发布 ZIP 内容解压到 `Storage/Plugins/Marisa.CodexPetAdapter/`，使 manifest.json 位于该目录根部。退出客户端后升级，重新加载插件或重启 Alife，再启用 Codex 桌宠渲染器。

可选择用户 Codex pets 目录或自定义目录；关闭默认路径选项且自定义目录留空时使用插件内置 pet/ 模型。本仓库附带 Ikaros 模型。

本次适配官方窗口，修复右键原生菜单、启动闪窗、独立气泡定位和缩放，并增加不抢焦点的置顶维护。发布包不含客户端、临时数据或个人配置。更新说明见 CHANGELOG.md。
