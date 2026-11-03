using System;
using System.Collections.Generic;

namespace Zayu.Core
{
    /// <summary>一键自检报告中的一行；IsSection=true 时表示分组标题。</summary>
    public sealed class SelfCheckItem
    {
        public string Label { get; set; }
        public string Value { get; set; }
        public bool IsSection { get; set; }

        /// <summary>该行的值是否包含 IP 等隐私信息，界面默认需要打码。</summary>
        public bool Sensitive { get; set; }

        public SelfCheckItem()
        {
            Label = string.Empty;
            Value = string.Empty;
        }
    }

    /// <summary>一键自检的汇总报告。</summary>
    public sealed class SelfCheckReport
    {
        public List<SelfCheckItem> Items { get; private set; }
        public NetworkReport NetworkReport { get; set; }
        public bool NetworkIncluded { get; set; }
        public bool ConnectivityIncluded { get; set; }
        public bool ConnectivityComplete { get; set; }
        public DateTime Timestamp { get; set; }
        public string Error { get; set; }

        public SelfCheckReport()
        {
            Items = new List<SelfCheckItem>();
            Timestamp = DateTime.Now;
            Error = string.Empty;
        }
    }
}
