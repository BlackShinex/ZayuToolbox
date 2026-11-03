using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Zayu.Core
{
    /// <summary>
    /// 基于公共 STUN 服务器的 NAT 类型检测（RFC 4787 思路）。
    ///
    /// 流程：
    ///   1. 解析至少 2 台不同公网 IP 的 IPv4 STUN 服务器；
    ///   2. 用同一个本地 UDP 套接字依次查询，比较映射地址判断映射行为；
    ///   3. 依据本机 IPv4 判定是否真的位于 NAT 后；
    ///   4. 尝试 CHANGE-REQUEST 测试过滤行为；公网 STUN 通常不支持，此时优雅降级为 ConeUnknown；
    ///   5. 任何网络失败都转成 NatResult.Error，不向上抛异常（取消除外）。
    /// </summary>
    public static class NatDetector
    {
        private const int QueryTimeoutMs = 1200;
        private const int QueryAttempts = 2;
        private const int FilteringTimeoutMs = 1000;
        private const int MaxMappingServers = 3;
        private const int MaxFilteringServers = 3;

        /// <summary>
        /// 检测本机的 NAT 映射 / 过滤行为，并填充公网 IPv4 出口信息。
        /// 不会抛出网络异常；仅取消时会抛出 OperationCanceledException。
        /// </summary>
        public static NatResult Detect(Action<string> progress, CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            NatResult result = new NatResult();
            try
            {
                DetectCore(result, progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Type = NatType.Unknown;
                result.TypeLabel = "未知";
                result.Error = "NAT 检测失败：" + ex.GetType().Name + " " + ex.Message;
                AddStep(result, "检测过程异常终止：" + ex.Message);
            }
            finally
            {
                stopwatch.Stop();
                result.Elapsed = stopwatch.Elapsed;
            }
            return result;
        }

        private static void DetectCore(NatResult result, Action<string> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            List<IpAddressEntry> localIpv4 = LocalAddressProvider.GetIpv4Addresses();
            IpAddressEntry primary = null;
            for (int i = 0; i < localIpv4.Count; i++)
            {
                if (localIpv4[i].IsPrimary)
                {
                    primary = localIpv4[i];
                    break;
                }
            }
            if (primary == null && localIpv4.Count > 0)
            {
                primary = localIpv4[0];
            }
            if (primary != null)
            {
                result.LocalIpv4 = primary.Address;
            }

            List<StunServer> servers = StunServers.Resolve(AddressFamily.InterNetwork);
            if (servers.Count == 0)
            {
                result.Type = NatType.Unknown;
                result.TypeLabel = "未知";
                result.Error = "无法解析 STUN 服务器地址（DNS 解析失败或网络未连接）。";
                AddStep(result, "未能解析任何可用的 IPv4 STUN 服务器地址。");
                return;
            }
            AddStep(result, "解析到 " + servers.Count + " 个公网 IP 互不相同的 STUN 服务器。");

            StunProbe probe = new StunProbe(AddressFamily.InterNetwork);
            try
            {
                result.LocalPort = probe.LocalPort;
                AddStep(result, "创建 UDP 探测套接字：本地端口 " + probe.LocalPort + "，本机 IPv4 "
                    + (string.IsNullOrEmpty(result.LocalIpv4) ? "未知" : result.LocalIpv4) + "。");

                // ---- 阶段 1：找到第一台可用的 STUN 服务器并拿到映射 ----
                StunServer firstServer = null;
                StunResponse firstResponse = null;
                for (int i = 0; i < servers.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    StunResponse response = QueryWithRetry(probe, servers[i], token, StunChangeRequest.None);
                    if (response != null && response.Success && response.Mapped != null)
                    {
                        firstServer = servers[i];
                        firstResponse = response;
                        break;
                    }
                    AddStep(result, "服务器 " + servers[i].Label + " 不可用："
                        + (response == null ? "无响应。" : response.Error));
                }

                if (firstResponse == null)
                {
                    result.Type = NatType.Unknown;
                    result.TypeLabel = "未知";
                    result.Error = "无法连接 STUN 服务器（UDP 可能被防火墙拦截）。";
                    AddStep(result, "所有 STUN 服务器均无响应，无法获取公网映射。");
                    return;
                }

                result.ExternalIpv4 = firstResponse.Mapped.Address.ToString();
                result.ExternalIpv4Port = firstResponse.Mapped.Port;
                result.ServerUsed = firstServer.Label;
                AddStep(result, "通过 " + firstServer.Label + " 获取到公网映射 "
                    + FormatEndPoint(firstResponse.Mapped)
                    + (firstResponse.ResponseSource == null
                        ? "。"
                        : "（响应来源 " + FormatEndPoint(firstResponse.ResponseSource) + "）。"));

                // ---- 阶段 2：同一 socket 查询其它公网 IP 的服务器，比较映射 ----
                Report(progress, "正在测试 NAT 映射行为…");
                token.ThrowIfCancellationRequested();

                List<StunServer> comparedServers = new List<StunServer>();
                comparedServers.Add(firstServer);
                List<StunResponse> mappingResponses = new List<StunResponse>();
                mappingResponses.Add(firstResponse);

                for (int i = 0; i < servers.Count && comparedServers.Count < MaxMappingServers; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (SameAddress(servers[i].Address, firstServer.Address))
                    {
                        continue; // 同一个公网 IP，无法用于比较。
                    }

                    StunResponse response = QueryWithRetry(probe, servers[i], token, StunChangeRequest.None);
                    if (response == null || !response.Success || response.Mapped == null)
                    {
                        AddStep(result, "服务器 " + servers[i].Label + " 不可用："
                            + (response == null ? "无响应。" : response.Error));
                        continue;
                    }

                    comparedServers.Add(servers[i]);
                    mappingResponses.Add(response);
                    AddStep(result, "通过 " + servers[i].Label + " 获取到公网映射 "
                        + FormatEndPoint(response.Mapped)
                        + (response.ResponseSource == null
                            ? "。"
                            : "（响应来源 " + FormatEndPoint(response.ResponseSource) + "）。"));
                }

                // ---- 阶段 3：判定映射行为 ----
                bool allSame = true;
                for (int i = 1; i < mappingResponses.Count; i++)
                {
                    if (!EndPointsEqual(firstResponse.Mapped, mappingResponses[i].Mapped))
                    {
                        allSame = false;
                        break;
                    }
                }

                if (mappingResponses.Count >= 2)
                {
                    if (allSame)
                    {
                        result.Mapping = MappingBehavior.EndpointIndependent;
                        AddStep(result, "共 " + mappingResponses.Count + " 台不同公网 IP 的服务器返回相同映射 "
                            + FormatEndPoint(firstResponse.Mapped)
                            + " → 端点无关映射（EndpointIndependent）。");
                    }
                    else
                    {
                        result.Mapping = MappingBehavior.EndpointDependent;
                        AddStep(result, "不同服务器返回的映射不一致（" + DescribeMappings(mappingResponses)
                            + "）→ 端点相关映射（EndpointDependent）。");
                    }
                }
                else
                {
                    result.Mapping = MappingBehavior.Unknown;
                    AddStep(result, "只有一台 STUN 服务器返回结果，无法比较映射行为。");
                }

                // ---- 端口保持 ----
                result.PortPreserved = result.ExternalIpv4Port > 0
                    && probe.LocalPort > 0
                    && result.ExternalIpv4Port == probe.LocalPort;
                if (result.PortPreserved)
                {
                    AddStep(result, "映射端口 " + result.ExternalIpv4Port + " 与本地端口 " + probe.LocalPort
                        + " 一致（端口保持）。");
                }
                else
                {
                    AddStep(result, "映射端口 " + result.ExternalIpv4Port + " 与本地端口 " + probe.LocalPort
                        + " 不同（端口未保持）。");
                }

                // ---- 无 NAT 判定 ----
                if (IsLocalPublicAddress(localIpv4, result.ExternalIpv4))
                {
                    result.Type = NatType.NoNat;
                    result.TypeLabel = "公网直连（无 NAT）";
                    result.Filtering = FilteringBehavior.Unknown;
                    result.Description = "公网出口 " + result.ExternalIpv4
                        + " 与本机 IPv4 地址一致，本机直接持有公网地址，未经过 NAT。";
                    AddStep(result, "公网出口地址就是本机地址 → 无 NAT（公网直连）。");
                    return;
                }

                // ---- 对称型 NAT ----
                if (result.Mapping == MappingBehavior.EndpointDependent)
                {
                    result.Type = NatType.Symmetric;
                    result.TypeLabel = "对称型 NAT（Symmetric）";
                    result.Description = "不同目标地址得到的公网映射不一致（端口或 IP 不同），属于端点相关映射的对称型 NAT，P2P 直连穿透较困难。";
                    AddStep(result, "结论：对称型 NAT（Symmetric）。");
                    return;
                }

                // ---- 端点无关映射：尝试过滤行为测试，并优雅降级 ----
                if (result.Mapping == MappingBehavior.EndpointIndependent)
                {
                    FilteringBehavior filtering = DetectFiltering(probe, servers, comparedServers, result, token);
                    result.Filtering = filtering;

                    if (filtering == FilteringBehavior.EndpointIndependent)
                    {
                        result.Type = NatType.FullCone;
                        result.TypeLabel = "全锥型 NAT（Full Cone）";
                        result.Description = "映射与过滤都与目标地址无关，任意外部主机都可以主动连接映射端口（全锥型 NAT，端点无关映射 + 端点无关过滤）。";
                    }
                    else if (filtering == FilteringBehavior.AddressDependent)
                    {
                        result.Type = NatType.RestrictedCone;
                        result.TypeLabel = "限制锥型 NAT（Restricted Cone）";
                        result.Description = "映射与目标地址无关，但只有本机访问过的外部 IP 才能主动连接映射端口（限制锥型 NAT）。";
                    }
                    else
                    {
                        result.Type = NatType.ConeUnknown;
                        result.TypeLabel = "锥型 NAT（端点无关映射）";
                        result.Description = "NAT 对不同目标的映射保持一致（端点无关映射），映射端口"
                            + (result.PortPreserved ? "与本地端口一致" : "与本地端口不同")
                            + "；无法进一步区分全锥/限制锥（公网 STUN 未提供过滤测试）。";
                    }

                    AddStep(result, "结论：" + result.TypeLabel + "。");
                    return;
                }

                // ---- 兜底：拿不到足够的映射样本 ----
                result.Type = NatType.Unknown;
                result.TypeLabel = "未知";
                result.Description = "检测到公网映射 " + result.ExternalIpv4 + ":" + result.ExternalIpv4Port
                    + "，但只有一台 STUN 服务器返回结果，无法判断映射行为。";
                AddStep(result, "无法判定 NAT 映射行为（可用 STUN 服务器不足）。");
            }
            finally
            {
                probe.Dispose();
            }
        }

        /// <summary>
        /// 尝试用 CHANGE-REQUEST 区分过滤行为。公网 STUN 基本不支持该属性，
        /// 此时返回 Unknown，由调用方降级为 ConeUnknown。
        /// </summary>
        private static FilteringBehavior DetectFiltering(StunProbe probe, List<StunServer> servers,
            List<StunServer> preferred, NatResult result, CancellationToken token)
        {
            // 优先使用已经成功过的服务器，再尝试其它服务器，按 IP 去重。
            List<StunServer> candidates = new List<StunServer>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < preferred.Count; i++)
            {
                AddCandidate(candidates, seen, preferred[i]);
            }
            for (int i = 0; i < servers.Count; i++)
            {
                AddCandidate(candidates, seen, servers[i]);
            }

            int tried = 0;
            for (int i = 0; i < candidates.Count && tried < MaxFilteringServers; i++)
            {
                token.ThrowIfCancellationRequested();
                StunServer server = candidates[i];
                tried++;

                // 1) 改 IP + 端口：收到来自其它 IP 的响应 → 端点无关过滤（全锥）。
                StunResponse fullConeResponse = probe.Query(server.GetEndPoint(), FilteringTimeoutMs,
                    StunChangeRequest.ChangeIpAndPort, token);
                if (fullConeResponse.Success && fullConeResponse.ResponseSource != null
                    && !fullConeResponse.ResponseSource.Address.Equals(server.Address))
                {
                    AddStep(result, "服务器 " + server.Label
                        + " 支持 CHANGE-REQUEST（改 IP+端口），响应来源变为 "
                        + FormatEndPoint(fullConeResponse.ResponseSource) + "。");
                    AddStep(result, "任何外部地址都能到达映射端口 → 端点无关过滤（EndpointIndependent）。");
                    return FilteringBehavior.EndpointIndependent;
                }
                AddStep(result, "服务器 " + server.Label + " 未对 CHANGE-IP+PORT 请求给出有效响应（"
                    + DescribeFailure(fullConeResponse) + "）。");

                // 2) 只改端口：收到同 IP 其它端口的响应 → 地址相关过滤（限制锥）。
                StunResponse restrictedResponse = probe.Query(server.GetEndPoint(), FilteringTimeoutMs,
                    StunChangeRequest.ChangePort, token);
                if (restrictedResponse.Success && restrictedResponse.ResponseSource != null
                    && restrictedResponse.ResponseSource.Address.Equals(server.Address)
                    && restrictedResponse.ResponseSource.Port != server.Port)
                {
                    AddStep(result, "服务器 " + server.Label
                        + " 支持 CHANGE-REQUEST（改端口），响应来源 "
                        + FormatEndPoint(restrictedResponse.ResponseSource) + "。");
                    AddStep(result, "只允许访问过的 IP 主动连接映射端口 → 地址相关过滤（AddressDependent）。");
                    return FilteringBehavior.AddressDependent;
                }
                AddStep(result, "服务器 " + server.Label + " 未对 CHANGE-PORT 请求给出有效响应（"
                    + DescribeFailure(restrictedResponse) + "）。");
            }

            AddStep(result, "公网 STUN 服务器均未提供有效的 CHANGE-REQUEST 过滤测试，过滤行为未知。");
            return FilteringBehavior.Unknown;
        }

        private static void AddCandidate(List<StunServer> candidates, HashSet<string> seen, StunServer server)
        {
            if (server == null || server.Address == null)
            {
                return;
            }
            if (candidates.Count >= MaxFilteringServers * 2)
            {
                return;
            }
            if (seen.Add(server.Address.ToString()))
            {
                candidates.Add(server);
            }
        }

        private static StunResponse QueryWithRetry(StunProbe probe, StunServer server,
            CancellationToken token, StunChangeRequest change)
        {
            StunResponse last = null;
            for (int attempt = 1; attempt <= QueryAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                IPEndPoint endpoint = server.GetEndPoint();
                last = probe.Query(endpoint, QueryTimeoutMs, change, token);
                if (last != null && last.Success)
                {
                    return last;
                }
            }
            return last;
        }

        private static bool IsLocalPublicAddress(List<IpAddressEntry> localIpv4, string externalIpv4)
        {
            if (string.IsNullOrEmpty(externalIpv4))
            {
                return false;
            }
            for (int i = 0; i < localIpv4.Count; i++)
            {
                IpAddressEntry entry = localIpv4[i];
                if (entry == null || entry.Scope != IpAddressScope.Public)
                {
                    continue;
                }
                if (string.Equals(entry.Address, externalIpv4, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool SameAddress(IPAddress a, IPAddress b)
        {
            return a != null && b != null && a.Equals(b);
        }

        private static bool EndPointsEqual(IPEndPoint a, IPEndPoint b)
        {
            if (a == null || b == null)
            {
                return false;
            }
            return a.Port == b.Port && a.Address.Equals(b.Address);
        }

        private static string DescribeMappings(List<StunResponse> responses)
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < responses.Count; i++)
            {
                if (responses[i] != null && responses[i].Mapped != null)
                {
                    parts.Add(FormatEndPoint(responses[i].Mapped));
                }
            }
            return string.Join("、", parts.ToArray());
        }

        private static string DescribeFailure(StunResponse response)
        {
            if (response == null)
            {
                return "无响应";
            }
            if (string.IsNullOrEmpty(response.Error))
            {
                return "无有效响应";
            }
            return response.Error;
        }

        private static string FormatEndPoint(IPEndPoint endpoint)
        {
            if (endpoint == null)
            {
                return "未知";
            }
            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return "[" + endpoint.Address + "]:" + endpoint.Port;
            }
            return endpoint.Address + ":" + endpoint.Port;
        }

        private static void AddStep(NatResult result, string message)
        {
            if (result == null || string.IsNullOrEmpty(message))
            {
                return;
            }
            result.Steps.Add(message);
        }

        private static void Report(Action<string> progress, string message)
        {
            if (progress != null)
            {
                progress(message);
            }
        }
    }
}
