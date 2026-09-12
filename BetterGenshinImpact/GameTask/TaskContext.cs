using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Model;
using BetterGenshinImpact.Genshin.Settings;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BetterGenshinImpact.Core.Script.Group;

namespace BetterGenshinImpact.GameTask
{
    /// <summary>
    /// 任务上下文
    /// </summary>
    public class TaskContext
    {
        private static TaskContext? _uniqueInstance;
        private static object? InstanceLocker;
        public ScriptGroupProject? CurrentScriptProject { get; set; }

#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑声明为可以为 null。

        private TaskContext()
        {
        }

#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑声明为可以为 null。

        public static TaskContext Instance()
        {
            return LazyInitializer.EnsureInitialized(ref _uniqueInstance, ref InstanceLocker, () => new TaskContext());
        }

        public void Init(IntPtr hWnd)
        {
            IsCloudWeb = false;
            GameHandle = hWnd;
            PostMessageSimulator = Simulation.PostMessage(GameHandle);
            SystemInfo = new SystemInfo(hWnd);
            DpiScale = DpiHelper.ScaleY;
            //MaskWindowHandle = new WindowInteropHelper(MaskWindow.Instance()).Handle;
            IsInitialized = true;
        }

        public bool IsInitialized { get; set; }

        // 在浏览器启动/登录阶段也为 true，用于阻止旧键鼠任务进入桌面输入链路。
        private volatile bool _isCloudWeb;
        public bool IsCloudWeb { get => _isCloudWeb; set => _isCloudWeb = value; }

        public void InitCloudWeb(nint hWnd, ISystemInfo systemInfo)
        {
            IsCloudWeb = true;
            GameHandle = hWnd;
            SystemInfo = systemInfo;
            PostMessageSimulator = Simulation.PostMessage(hWnd);
            DpiScale = 1;
            IsInitialized = true;
        }

        public IntPtr GameHandle { get; set; }

        public PostMessageSimulator PostMessageSimulator { get; private set; }

        //public IntPtr MaskWindowHandle { get; set; }

        public float DpiScale { get; set; }

        public ISystemInfo SystemInfo { get; set; }

        public AllConfig Config
        {
            get
            {
                if (ConfigService.Config == null)
                {
                    throw new Exception("Config未初始化");
                }

                return ConfigService.Config;
            }
        }

        // public SettingsContainer? GameSettings { get; set; }

        /// <summary>
        /// 关联启动原神的时间
        /// 注意 IsInitialized = false 时，这个值就会被设置
        /// </summary>
        public DateTime LinkedStartGenshinTime { get; set; } = DateTime.MinValue;

        public List<string> GetGenshinGameProcessNameList()
        {
            // 通用浏览器进程名不能成为游戏白名单，尤其不能用于 CloseGame。
            if (IsCloudWeb) return [];
            if (IsInitialized)
            {
                return [SystemInfo.GameProcessName];
            }
            else
            {
                List<string> list = ["YuanShen", "GenshinImpact", "Genshin Impact Cloud Game", "Genshin Impact Cloud"];
                try
                {
                    var installPath = Config.GenshinStartConfig.InstallPath;
                    if (!string.IsNullOrEmpty(installPath))
                    {
                        var customName = Path.GetFileNameWithoutExtension(installPath);
                        if (!string.IsNullOrEmpty(customName) && !list.Contains(customName))
                        {
                            // list.Insert(0, customName); // 将用户自定义的进程名放在列表前面，优先匹配
                            list.Add(customName);
                        }
                    }
                }
                catch
                {
                    /* ignore */
                }

                return list;
            }
        }
    }
}