using System;
using System.Net;
using System.Net.Sockets;

namespace IpInfo.Core
{
    /// <summary>IP 网段计算辅助。</summary>
    public static class IpNetworkHelper
    {
        /// <summary>判断两个地址是否在同一子网内；地址族不同或前缀无效时返回 false。</summary>
        public static bool SameSubnet(string address, string networkAddress, int prefixLength)
        {
            IPAddress a;
            IPAddress b;
            if (!TryParse(address, out a)) return false;
            if (!TryParse(networkAddress, out b)) return false;
            if (a.AddressFamily != b.AddressFamily) return false;

            byte[] left = a.GetAddressBytes();
            byte[] right = b.GetAddressBytes();
            if (left.Length != right.Length) return false;

            int maxBits = left.Length * 8;
            if (prefixLength < 0) return false;
            if (prefixLength > maxBits) prefixLength = maxBits;

            int fullBytes = prefixLength / 8;
            for (int i = 0; i < fullBytes; i++)
            {
                if (left[i] != right[i]) return false;
            }

            int remainingBits = prefixLength % 8;
            if (remainingBits > 0)
            {
                int mask = 0xFF << (8 - remainingBits);
                if ((left[fullBytes] & mask) != (right[fullBytes] & mask)) return false;
            }
            return true;
        }

        /// <summary>判断地址是否是私有 / 唯一本地 / 链路本地等非公网地址。</summary>
        public static bool IsPrivateOrLocal(string address)
        {
            IpAddressScope scope = IpClassifier.Classify(address);
            return scope == IpAddressScope.Private
                || scope == IpAddressScope.SharedCgnat
                || scope == IpAddressScope.UniqueLocal
                || scope == IpAddressScope.LinkLocal;
        }

        private static bool TryParse(string text, out IPAddress address)
        {
            address = null;
            if (string.IsNullOrEmpty(text)) return false;
            return IPAddress.TryParse(IpPrivacy.StripScopeId(text), out address);
        }
    }
}
