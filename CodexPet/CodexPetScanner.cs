using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Marisa.CodexPetAdapter;

/// <summary>
/// 扫描并加载本地 Codex 宠物包。
/// </summary>
public static class CodexPetScanner
{
    /// <summary>
    /// 默认的 Codex 宠物目录：%USERPROFILE%\.codex\pets
    /// </summary>
    public static string DefaultPetsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "pets");

    /// <summary>
    /// 插件自带宠物目录：Alife 程序目录下的 Storage\Plugins\Marisa.CodexPetAdapter\pet。
    /// </summary>
    public static string BuiltinPetsPath =>
        Path.Combine(Alife.Foundation.AlifePath.StorageFolderPath, "Plugins", "Marisa.CodexPetAdapter", "pet");

    /// <summary>
    /// 相对路径按 Alife 程序目录解析；绝对路径原样返回。
    /// </summary>
    public static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;
        // ElectronNET 后端运行在 <Alife>\resources\bin，需向上找含 Storage 的 Alife 根目录。
        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Storage")))
                return Path.GetFullPath(Path.Combine(directory.FullName, path));
            directory = directory.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    /// <summary>
    /// 根据配置获取实际使用的宠物目录。
    /// </summary>
    public static string GetEffectivePath(CodexPetRendererConfig config)
    {
        if (config.UseDefaultPetsPath)
            return DefaultPetsPath;

        if (string.IsNullOrWhiteSpace(config.CustomPetsPath))
            return BuiltinPetsPath;

        return ResolvePath(config.CustomPetsPath);
    }

    /// <summary>
    /// 扫描指定目录下的所有 Codex 宠物包。
    /// </summary>
    public static List<CodexPetModel> ScanPets(string path)
    {
        List<CodexPetModel> result = new();
        if (!Directory.Exists(path))
            return result;

        foreach (string directory in Directory.GetDirectories(path))
        {
            CodexPetModel? model = LoadFromDirectory(directory);
            if (model != null && model.IsSupported)
                result.Add(model);
        }

        return result;
    }

    /// <summary>
    /// 从单个目录加载宠物包。
    /// </summary>
    public static CodexPetModel? LoadFromDirectory(string directory)
    {
        string petJsonPath = Path.Combine(directory, "pet.json");
        if (!File.Exists(petJsonPath))
            return null;

        try
        {
            string json = File.ReadAllText(petJsonPath);
            CodexPetModel model = JsonConvert.DeserializeObject<CodexPetModel>(json)
                ?? new CodexPetModel();
            model.RootDirectory = directory;
            return model;
        }
        catch
        {
            return null;
        }
    }
}


