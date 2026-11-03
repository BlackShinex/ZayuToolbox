using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using IpInfo.Core;

namespace IpInfo.App
{
    /// <summary>组件状态表的一行。</summary>
    public sealed class ComponentStatusRow
    {
        public string Name { get; set; }
        public string StatusText { get; set; }
        public string Version { get; set; }
        public string Detail { get; set; }
        public string DeviceId { get; set; }
        public string Key { get; set; }
        public bool CanRepair { get; set; }
        public bool CanUninstall { get; set; }
        public bool HasActions { get; set; }
        public string RepairLabel { get; set; }
        public bool IsSection { get; set; }
        public Brush StatusBackground { get; set; }
        public Brush StatusForeground { get; set; }

        public ComponentStatusRow()
        {
            Name = string.Empty;
            StatusText = string.Empty;
            Version = string.Empty;
            Detail = string.Empty;
            DeviceId = string.Empty;
            Key = string.Empty;
            RepairLabel = string.Empty;
        }
    }

    /// <summary>组件状态页中按大类分组的卡片。</summary>
    public sealed class ComponentStatusGroup
    {
        public string Title { get; set; }
        public string Summary { get; set; }
        public List<ComponentStatusRow> Items { get; set; }

        public ComponentStatusGroup()
        {
            Title = string.Empty;
            Summary = string.Empty;
            Items = new List<ComponentStatusRow>();
        }
    }

    /// <summary>一键自检表格的一行。</summary>
    public sealed class SelfCheckRow
    {
        public string Label { get; set; }
        public string Value { get; set; }
        public bool IsSection { get; set; }
        public bool IsAlt { get; set; }

        public SelfCheckRow()
        {
            Label = string.Empty;
            Value = string.Empty;
        }
    }

    /// <summary>网络连通性测试矩阵的一行。</summary>
    public sealed class ConnectivityRow
    {
        public string Title { get; set; }
        public string Detail { get; set; }
        public string StatusText { get; set; }
        public Brush StatusBackground { get; set; }
        public Brush StatusForeground { get; set; }

        public ConnectivityRow()
        {
            Title = string.Empty;
            Detail = string.Empty;
            StatusText = string.Empty;
        }
    }

    /// <summary>界面使用的 IP 行数据。</summary>
    public sealed class IpRow
    {
        public string DisplayAddress { get; set; }
        public string CopyAddress { get; set; }
        public string AdapterText { get; set; }
        public string ScopeLabel { get; set; }
        public Brush TagBackground { get; set; }
        public Brush TagForeground { get; set; }
        public double AddressFontSize { get; set; }
        public Visibility PrimaryVisibility { get; set; }
    }

    /// <summary>界面配色工具。</summary>
    public static class UiPalette
    {
        public static readonly Brush SuccessBg = Brush("#DCFCE7");
        public static readonly Brush SuccessFg = Brush("#15803D");
        public static readonly Brush WarnBg = Brush("#FEF3C7");
        public static readonly Brush WarnFg = Brush("#B45309");
        public static readonly Brush OrangeBg = Brush("#FFEDD5");
        public static readonly Brush OrangeFg = Brush("#C2410C");
        public static readonly Brush GrayBg = Brush("#F1F5F9");
        public static readonly Brush GrayFg = Brush("#58536A");
        public static readonly Brush IndigoBg = Brush("#E0E7FF");
        public static readonly Brush IndigoFg = Brush("#4338CA");
        public static readonly Brush BlueFg = Brush("#2563EB");
        public static readonly Brush AmberFg = Brush("#B45309");
        public static readonly Brush MutedFg = Brush("#6E687F");

        public static Brush Brush(string hex)
        {
            return (Brush)new BrushConverter().ConvertFromString(hex);
        }

        public static void GetScopeColors(IpAddressScope scope, out Brush background, out Brush foreground)
        {
            switch (scope)
            {
                case IpAddressScope.Public:
                    background = SuccessBg;
                    foreground = SuccessFg;
                    break;
                case IpAddressScope.Private:
                    background = WarnBg;
                    foreground = WarnFg;
                    break;
                case IpAddressScope.SharedCgnat:
                    background = OrangeBg;
                    foreground = OrangeFg;
                    break;
                case IpAddressScope.UniqueLocal:
                    background = IndigoBg;
                    foreground = IndigoFg;
                    break;
                default:
                    background = GrayBg;
                    foreground = GrayFg;
                    break;
            }
        }

        public static Brush GetNatTypeBrush(NatType type)
        {
            switch (type)
            {
                case NatType.NoNat:
                case NatType.FullCone:
                case NatType.RestrictedCone:
                case NatType.PortRestrictedCone:
                    return SuccessFg;
                case NatType.ConeUnknown:
                    return BlueFg;
                case NatType.Symmetric:
                    return OrangeFg;
                default:
                    return GrayFg;
            }
        }

        public static string GetPublicStatusText(PublicIpStatus status)
        {
            switch (status)
            {
                case PublicIpStatus.NativePublic: return "公网直连";
                case PublicIpStatus.BehindNat: return "NAT 后";
                case PublicIpStatus.NotAvailable: return "不可用";
                default: return "未知";
            }
        }

        public static void GetPublicStatusColors(PublicIpStatus status, out Brush background, out Brush foreground)
        {
            switch (status)
            {
                case PublicIpStatus.NativePublic:
                    background = SuccessBg;
                    foreground = SuccessFg;
                    break;
                case PublicIpStatus.BehindNat:
                    background = WarnBg;
                    foreground = WarnFg;
                    break;
                default:
                    background = GrayBg;
                    foreground = GrayFg;
                    break;
            }
        }
    }
}
