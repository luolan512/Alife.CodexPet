using System;
using System.IO;
using Newtonsoft.Json;

namespace Marisa.CodexPetAdapter;

/// <summary>
/// Codex v2 宠物包的数据模型。
/// </summary>
public class CodexPetModel
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("spritesheetPath")]
    public string SpritesheetPath { get; set; } = string.Empty;

    [JsonProperty("spriteVersionNumber")]
    public int SpriteVersionNumber { get; set; }

    [JsonProperty("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>宠物包根目录。</summary>
    [JsonIgnore]
    public string RootDirectory { get; set; } = string.Empty;

    /// <summary>精灵表绝对路径。</summary>
    [JsonIgnore]
    public string SpritesheetAbsolutePath =>
        Path.Combine(RootDirectory, SpritesheetPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// 该模型是否受支持。
    /// 版本 2 是 8x11 精灵表；版本 1/未标记旧表是 8x9 精灵表。
    /// 缺少 spriteVersionNumber 字段时按旧版 v1 兼容处理。
    /// </summary>
    [JsonIgnore]
    public bool IsSupported =>
        (SpriteVersionNumber is 1 or 2 or 0) &&
        !string.IsNullOrWhiteSpace(SpritesheetPath) &&
        File.Exists(SpritesheetAbsolutePath);

    /// <summary>规范化后的精灵表格式版本。</summary>
    [JsonIgnore]
    public int FormatVersion => SpriteVersionNumber == 2 ? 2 : 1;

    /// <summary>精灵表标准动画行数：v1 为 0-8，v2 额外包含 9-10 注视行。</summary>
    [JsonIgnore]
    public int AtlasRowCount => FormatVersion == 2 ? 11 : 9;

    /// <summary>是否支持 16 方向注视帧（rows 9-10）。</summary>
    [JsonIgnore]
    public bool SupportsLookDirections => FormatVersion == 2;
}

