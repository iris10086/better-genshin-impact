﻿using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recorder;
using BetterGenshinImpact.Core.Script.Project;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Shell;
using BetterGenshinImpact.ViewModel.Pages;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.FarmingPlan;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Script.Group;

public partial class ScriptGroupProject : ObservableObject
{
    [ObservableProperty]
    private int _index;

    /// <summary>
    /// 理论上是文件名
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 理论上是当前类型脚本根目录到脚本文件所在目录的相对路径
    /// 但是：
    /// 1. JS 脚本的文件名是内部的名称，文件夹名脚本所在文件夹，这个也是唯一标识
    /// 2. KeyMouse 脚本的文件名和文件夹名相同，文件夹名暂时无意义
    /// 3. Pathing 文件名就是实际脚本的文件名，文件夹名是脚本所在的相对目录
    /// </summary>
    public string FolderName { get; set; } = string.Empty;

    [ObservableProperty]
    private string _type = string.Empty;

    [JsonIgnore]
    public string TypeDesc => ScriptGroupProjectExtensions.TypeDescriptions[Type];

    [ObservableProperty]
    private string _status = string.Empty;

    [JsonIgnore]
    public string StatusDesc => ScriptGroupProjectExtensions.StatusDescriptions[Status];

    /// <summary>
    /// 执行周期
    /// 不在 ScheduleDescriptions 中则会被视为自定义Cron表达式
    /// </summary>
    [ObservableProperty]
    private string _schedule = string.Empty;

    [JsonIgnore]
    public string ScheduleDesc => ScriptGroupProjectExtensions.ScheduleDescriptions.GetValueOrDefault(Schedule, "自定义周期");

    [ObservableProperty]
    private int _runNum = 1;

    [JsonIgnore]
    public ScriptProject? Project { get; set; }

    public ExpandoObject? JsScriptSettingsObject { get; set; }

    /// <summary>
    /// 所属配置组
    /// </summary>
    [JsonIgnore]
    public ScriptGroup? GroupInfo { get; set; }

    private bool? _nextFlag = false;
    private bool? _skipFlag = false;

    /// <summary>
    /// 下一个从此执行标志
    /// </summary>
    [JsonIgnore]
    public bool? NextFlag
    {
        get => _nextFlag;
        set => SetProperty(ref _nextFlag, value);
    }

    /// <summary>
    /// 直接跳过标志
    /// </summary>
    [JsonIgnore]
    public bool? SkipFlag
    {
        get => _skipFlag;
        set => SetProperty(ref _skipFlag, value);
    }
    
    
    [ObservableProperty]
    private bool? _allowJsNotification = true;

    public ScriptGroupProject()
    {
    }

    public ScriptGroupProject(ScriptProject project)
    {
        Name = project.Manifest.Name;
        FolderName = project.FolderName;
        Status = "Enabled";
        Schedule = "Daily";
        Project = project;
        Type = "Javascript";
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="name"></param>
    /// <param name="folder"></param>
    /// <param name="type">KeyMouse|Pathing</param>
    public ScriptGroupProject(string name, string folder, string type)
    {
        Name = name;
        FolderName = folder;
        Status = "Enabled";
        Schedule = "Daily";
        Project = null; // 不是JS脚本
        Type = type;
    }

    public static ScriptGroupProject BuildKeyMouseProject(string name)
    {
        return new ScriptGroupProject(name, name, "KeyMouse");
    }

    public static ScriptGroupProject BuildShellProject(string command)
    {
        return new ScriptGroupProject(command, "", "Shell");
    }

    public static ScriptGroupProject BuildPathingProject(string name, string folder)
    {
        return new ScriptGroupProject(name, folder, "Pathing");
    }

    /// <summary>
    /// 通过 FolderName 查找 ScriptProject
    /// </summary>
    public void BuildScriptProjectRelation()
    {
        if (string.IsNullOrEmpty(FolderName))
        {
            throw new Exception("FolderName 为空");
        }
        Project = new ScriptProject(FolderName);
    }

    public async Task Run()
    {
        if (Type == "Javascript")
        {
            if (Project == null)
            {
                throw new Exception("JS脚本未初始化");
            }
            JsScriptSettingsObject ??= new ExpandoObject();

            var pathingPartyConfig = GroupInfo?.Config.PathingConfig;
            await Project.ExecuteAsync(JsScriptSettingsObject, pathingPartyConfig);
        }
        else if (Type == "KeyMouse")
        {
            // 加载并执行
            var json = await File.ReadAllTextAsync(Global.Absolute(@$"User\KeyMouseScript\{Name}"));
            await KeyMouseMacroPlayer.PlayMacro(json, CancellationContext.Instance.Cts.Token, false);
        }
        else if (Type == "Pathing")
        {
            // 加载并执行
            var task = PathingTask.BuildFromFilePath(Path.Combine(MapPathingViewModel.PathJsonPath, FolderName, Name));
            if (task == null)
            {
                return;
            }
            var pathingTask = new PathExecutor(CancellationContext.Instance.Cts.Token);
            pathingTask.PartyConfig = GroupInfo?.Config.PathingConfig;
            if (pathingTask.PartyConfig is null || pathingTask.PartyConfig.AutoPickEnabled)
            {
                TaskTriggerDispatcher.Instance().AddTrigger("AutoPick", null);
            }
            await pathingTask.Pathing(task);

            
            
            OtherConfig.AutoRestart autoRestart = TaskContext.Instance().Config.OtherConfig.AutoRestartConfig;
            if (!pathingTask.SuccessEnd)
            {
                TaskControl.Logger.LogWarning($"此追踪脚本未正常走完！");
                if (autoRestart.Enabled && autoRestart.IsPathingFailureExceptional && !pathingTask.SuccessEnd)
                {
                    throw new Exception($"路径追踪任务未完全走完，判定失败，触发异常！");
                }
            }

            if (task.FarmingInfo.AllowFarmingCount)
            {
                var successFight = pathingTask.SuccessEnd;
                var fightCount = 0;
                
                //未走完完整路径下，才校验打架次数
                if (!successFight)
                {
                    fightCount = task.Positions.Count(pos => pos.Action == ActionEnum.Fight.Code);
                    successFight = pathingTask.SuccessFight >= fightCount;
                    //判断为锄地脚本
                    if (task.FarmingInfo.PrimaryTarget!="disable")
                    {

                        if (autoRestart.Enabled
                            &&autoRestart.IsFightFailureExceptional
                            &&!successFight)
                        {
                            throw new Exception($"实际战斗次数({pathingTask.SuccessFight})<预期战斗次数（{fightCount}），判定失败，触发异常！");
                        }
                    }
                }

                if (successFight)
                {
                    //每日锄地记录
                    FarmingStatsRecorder.RecordFarmingSession(task.FarmingInfo, new FarmingRouteInfo
                    {
                        GroupName = GroupInfo?.Name ?? "",
                        FolderName = FolderName,
                        ProjectName = Name
                    });
                }
                else
                {
                    TaskControl.Logger.LogWarning($"实际战斗次数({pathingTask.SuccessFight})<预期战斗次数（{fightCount}），判定失败，此次不纳入成功锄地规划的统计上限！");
                }

            }
        

            

        }
        else if (Type == "Shell")
        {
            ShellConfig? shellConfig = null;
            if (GroupInfo?.Config.EnableShellConfig ?? false)
            {
                shellConfig = GroupInfo?.Config.ShellConfig;
            }

            var task = new ShellTask(ShellTaskParam.BuildFromConfig(Name, shellConfig ?? new ShellConfig()));
            await task.Start(CancellationContext.Instance.Cts.Token);
        }
    }

    partial void OnTypeChanged(string value)
    {
        OnPropertyChanged(nameof(TypeDesc));
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StatusDesc));
    }

    partial void OnScheduleChanged(string value)
    {
        OnPropertyChanged(nameof(ScheduleDesc));
    }
}

public class ScriptGroupProjectExtensions
{
    public static readonly Dictionary<string, string> TypeDescriptions = new()
    {
        { "Javascript", "JS脚本" },
        { "KeyMouse", "键鼠脚本" },
        { "Pathing", "地图追踪" },
        { "Shell", "Shell" }
    };

    public static readonly Dictionary<string, string> StatusDescriptions = new()
    {
        { "Enabled", "启用" },
        { "Disabled", "禁用" }
    };

    public Dictionary<string, string> GetStatusDescriptions()
    {
        return StatusDescriptions;
    }

    public static readonly Dictionary<string, string> ScheduleDescriptions = new()
    {
        { "Daily", "每日" },
        { "EveryTwoDays", "间隔两天" },
        { "Monday", "每周一" },
        { "Tuesday", "每周二" },
        { "Wednesday", "每周三" },
        { "Thursday", "每周四" },
        { "Friday", "每周五" },
        { "Saturday", "每周六" },
        { "Sunday", "每周日" }
    };

    public Dictionary<string, string> GetScheduleDescriptions()
    {
        return ScheduleDescriptions;
    }
}
