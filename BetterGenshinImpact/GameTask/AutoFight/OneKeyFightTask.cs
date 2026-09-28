using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Service;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>一键战斗宏</summary>
public class OneKeyFightTask : Singleton<OneKeyFightTask>
{
    public static readonly string HoldOnMode = "按住时重复(新)";
    public static readonly string HoldFinishMode = "按住时重复(旧)";
    public static readonly string TickMode = "触发";

    private Dictionary<string, List<CombatCommand>>? _avatarMacros;
    private readonly MacroRunner _runner = new(ex => Logger.LogDebug(ex, "一键宏执行失败"));
    // 配置和角色识别串行准备，但绝不持有宏生命周期锁；强停可取消排队的准备工作。
    private readonly SemaphoreSlim _preparationGate = new(1, 1);
    private int _activeMacroPriority = -1;
    private DateTime _lastUpdateTime = DateTime.MinValue;
    private CombatScenes? _currentCombatScenes;

    public bool IsCloudSuspended => _runner.IsCloudSuspended;

    /// <summary>等待所有宏、技能检测及按键释放完成后，调用方才能开启云输入防护。</summary>
    public Task SuspendForCloudAsync() => _runner.SuspendForCloudAsync();
    public void ResumeAfterCloud() => _runner.ResumeAfterCloud();

    public void KeyDown()
    {
        if (IsCloudSuspended || TaskContext.Instance().IsCloudWeb || !IsEnabled()) return;
        MacroMode mode;
        if (IsHoldOnMode()) mode = MacroMode.Hold;
        else if (IsHoldFinishMode()) mode = MacroMode.HoldFinish;
        else if (IsTickMode()) mode = MacroMode.Toggle;
        else return;
        _runner.KeyDown(mode, FightAsync);
    }

    public void KeyUp() => _runner.KeyUp();

    private async Task FightAsync(MacroExecution execution)
    {
        Avatar? activeAvatar;
        List<CombatCommand>? commands;
        await _preparationGate.WaitAsync(execution.ForceToken).ConfigureAwait(false);
        try
        {
            MacroExecutionScope.Checkpoint();
            if (_activeMacroPriority != TaskContext.Instance().Config.MacroConfig.CombatMacroPriority || IsAvatarMacrosEdited())
            {
                _activeMacroPriority = TaskContext.Instance().Config.MacroConfig.CombatMacroPriority;
                _avatarMacros = LoadAvatarMacros();
                Logger.LogInformation("加载一键宏配置完成");
            }

            using var imageRegion = CaptureToRectArea();
            var combatScenes = new CombatScenes();
            try
            {
                combatScenes.InitializeTeam(imageRegion);
                MacroExecutionScope.Checkpoint();
                if (combatScenes.CheckTeamInitialized())
                {
                    _currentCombatScenes = combatScenes;
                }
                else if (_currentCombatScenes == null)
                {
                    Logger.LogError("首次队伍角色识别失败");
                    return;
                }
                else
                {
                    Logger.LogWarning("队伍角色识别失败，使用上次识别结果，队伍未切换时无影响");
                }
            }
            finally
            {
                if (!ReferenceEquals(combatScenes, _currentCombatScenes)) combatScenes.Dispose();
            }

            var avatarName = _currentCombatScenes.CurrentAvatar(true, imageRegion, execution.ForceToken);
            activeAvatar = avatarName == null ? null : _currentCombatScenes.SelectAvatar(avatarName);
            if (activeAvatar == null)
            {
                Logger.LogError("无法识别出战角色");
                return;
            }
            if (_avatarMacros == null || !_avatarMacros.TryGetValue(activeAvatar.Name, out commands))
            {
                Logger.LogWarning("→ {Name}配置[{Priority}]为空，请先配置一键宏", activeAvatar.Name, _activeMacroPriority);
                return;
            }
        }
        finally
        {
            _preparationGate.Release();
        }

        try
        {
            execution.RunRounds(commands, IsEnabled, (command, round) =>
            {
                if (command.ActivatingRound is { Count: > 0 } && !command.ActivatingRound.Contains(round)) return;
                ExecuteCommand(execution, activeAvatar, command);
            }, round => Logger.LogInformation("→ {Name}执行宏 (第{Round}轮)", activeAvatar.Name, round));
        }
        finally
        {
            Logger.LogInformation("→ {Name}停止宏", activeAvatar.Name);
        }
    }

    // 三种模式都拥有自己的输入账本；释放与发送按下串行，避免松键后再次记入残留输入。
    private static void ExecuteCommand(MacroExecution execution, Avatar avatar, CombatCommand command)
    {
        if (command.Method == Method.KeyDown)
        {
            var key = command.Args![0];
            execution.InputDown("key:" + key, () => command.Execute(avatar), () => avatar.KeyUp(key));
        }
        else if (command.Method == Method.KeyUp)
        {
            execution.InputUp("key:" + command.Args![0], () => command.Execute(avatar));
        }
        else if (command.Method == Method.MouseDown)
        {
            var key = command.Args is { Count: > 0 } ? command.Args[0] : "left";
            execution.InputDown("mouse:" + key, () => command.Execute(avatar), () => avatar.MouseUp(key));
        }
        else if (command.Method == Method.MouseUp)
        {
            var key = command.Args is { Count: > 0 } ? command.Args[0] : "left";
            execution.InputUp("mouse:" + key, () => command.Execute(avatar));
        }
        else
        {
            command.Execute(avatar);
        }
    }

    public Dictionary<string, List<CombatCommand>> LoadAvatarMacros()
    {
        var jsonPath = GetAvatarMacroJsonPath();
        var json = File.ReadAllText(jsonPath);
        _lastUpdateTime = File.GetLastWriteTime(jsonPath);
        var avatarMacros = JsonSerializer.Deserialize<List<AvatarMacro>>(json, ConfigService.JsonOptions);
        if (avatarMacros == null) return [];

        var result = new Dictionary<string, List<CombatCommand>>();
        foreach (var avatarMacro in avatarMacros)
        {
            var commands = avatarMacro.LoadCommands();
            if (commands != null) result.Add(avatarMacro.Name, commands);
        }
        return result;
    }

    public bool IsAvatarMacrosEdited()
    {
        var jsonPath = GetAvatarMacroJsonPath();
        return File.GetLastWriteTime(jsonPath) > _lastUpdateTime;
    }

    public static string GetAvatarMacroJsonPath()
    {
        var path = Global.Absolute("User/avatar_macro.json");
        if (!File.Exists(path)) File.Copy(Global.Absolute("User/avatar_macro_default.json"), path);
        return path;
    }

    public static bool IsEnabled() => TaskContext.Instance().Config.MacroConfig.CombatMacroEnabled;
    public static bool IsHoldOnMode() => TaskContext.Instance().Config.MacroConfig.CombatMacroHotkeyMode == HoldOnMode;
    public static bool IsHoldFinishMode() => TaskContext.Instance().Config.MacroConfig.CombatMacroHotkeyMode == HoldFinishMode;
    public static bool IsTickMode() => TaskContext.Instance().Config.MacroConfig.CombatMacroHotkeyMode == TickMode;
}
