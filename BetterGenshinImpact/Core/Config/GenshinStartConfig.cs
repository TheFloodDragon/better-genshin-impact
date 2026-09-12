using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
///     原神启动配置
/// </summary>
[Serializable]
public partial class GenshinStartConfig : ObservableObject
{
    /// <summary>使用独立浏览器启动网页云原神；旧配置保持本地模式。</summary>
    [ObservableProperty]
    private bool _cloudWebEnabled;

    /// <summary>留空自动查找 Microsoft Edge，也可指定 Chrome 可执行文件。</summary>
    [ObservableProperty]
    private string _cloudBrowserPath = "";

    [ObservableProperty]
    private int _cloudLoginTimeoutMinutes = 20;

    [ObservableProperty]
    private int _cloudQueueTimeoutMinutes = 60;

    [ObservableProperty]
    private int _cloudConnectTimeoutSeconds = 90;

    [ObservableProperty]
    private int _cloudEnterTimeoutMinutes = 5;

    // /// <summary>
    // ///     自动点击月卡
    // /// </summary>
    // [ObservableProperty]
    // private bool _autoClickBlessingOfTheWelkinMoonEnabled;

    /// <summary>
    ///     自动进入游戏（开门）
    /// </summary>
    [ObservableProperty]
    private bool _autoEnterGameEnabled = true;

    /// <summary>
    ///     原神启动参数
    /// </summary>
    [ObservableProperty]
    private string _genshinStartArgs = "";

    /// <summary>
    ///     原神安装路径
    /// </summary>
    [ObservableProperty]
    private string _installPath = "";

    /// <summary>
    ///     联动启动原神本体
    /// </summary>
    [ObservableProperty]
    private bool _linkedStartEnabled = true;

    /// <summary>
    ///     使用Starward同步记录时间
    /// </summary>
    [ObservableProperty]
    private bool _recordGameTimeEnabled = false;

    [ObservableProperty]
    private bool _startGameWithCmd = false;

    /// <summary>
    ///     启动前自动关闭原神 HDR（删除原神 HDR 对应注册表键）
    /// </summary>
    [ObservableProperty]
    private bool _autoDisableGenshinHdrEnabled = true;
}
