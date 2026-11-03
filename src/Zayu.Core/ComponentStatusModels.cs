using System;
using System.Collections.Generic;

namespace Zayu.Core
{
    /// <summary>单个组件的健康状态。</summary>
    public enum ComponentHealth
    {
        Unknown = 0,
        NotInstalled,
        Installed,
        Ready,
        Running,
        Warning,
        Error
    }

    /// <summary>组件状态表中的一行；IsSection=true 时表示分组标题。</summary>
    public sealed class ComponentStatusItem
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public ComponentHealth Health { get; set; }
        public string StatusText { get; set; }
        public string Version { get; set; }
        public string Detail { get; set; }
        public string DeviceId { get; set; }
        public string Key { get; set; }
        public bool CanRepair { get; set; }
        public bool CanUninstall { get; set; }
        public bool IsSection { get; set; }

        public ComponentStatusItem()
        {
            Category = string.Empty;
            Name = string.Empty;
            StatusText = string.Empty;
            Version = string.Empty;
            Detail = string.Empty;
            DeviceId = string.Empty;
            Key = string.Empty;
        }
    }

    /// <summary>组件状态检测结果。</summary>
    public sealed class ComponentStatusReport
    {
        public List<ComponentStatusItem> Items { get; private set; }
        public string SunshineInstallPath { get; set; }
        public DateTime Timestamp { get; set; }
        public string Error { get; set; }

        public ComponentStatusReport()
        {
            Items = new List<ComponentStatusItem>();
            Timestamp = DateTime.Now;
            Error = string.Empty;
        }
    }
}
