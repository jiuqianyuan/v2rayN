using System.Net.Sockets;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System;

namespace ServiceLib.Common;

/// <summary>
/// Happy Eyeballs（RFC 8305）连接实现。
/// 将解析到的 IPv6/IPv4 地址交替排列（IPv6 优先），按固定间隔（familyDelay）
/// 依次发起连接尝试，任意一次尝试率先成功即立即返回，避免因单一地址族连接缓慢
/// 而拖慢整体连接耗时。
/// </summary>
public static class HappyEyeballsConnection
{
    private static readonly TimeSpan DefaultPerAttemptTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultFamilyDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 对候选地址排序：IPv6、IPv4 分别按文本字典序排序后交替合并（IPv6 优先），
    /// 使得后续按顺序发起连接时，天然实现"两个地址族轮流尝试"的效果。
    /// </summary>
    public static IList<IPAddress> OrderCandidates(IEnumerable<IPAddress> addresses)
    {
        var list = addresses.Where(static address => address != null).ToList();
        if (list.Count == 0)
        {
            return [];
        }

        var ipv6 = list.Where(static address => address.AddressFamily == AddressFamily.InterNetworkV6)
            .OrderBy(static address => address.ToString(), StringComparer.Ordinal)
            .ToList();
        var ipv4 = list.Where(static address => address.AddressFamily == AddressFamily.InterNetwork)
            .OrderBy(static address => address.ToString(), StringComparer.Ordinal)
            .ToList();

        // 交替合并：ipv6[0], ipv4[0], ipv6[1], ipv4[1] ...；某一方耗尽后只追加另一方剩余元素。
        var interleaved = new List<IPAddress>(ipv6.Count + ipv4.Count);
        int i = 0, j = 0;
        while (i < ipv6.Count || j < ipv4.Count)
        {
            if (i < ipv6.Count)
                interleaved.Add(ipv6[i++]);
            if (j < ipv4.Count)
                interleaved.Add(ipv4[j++]);
        }
        return interleaved;
    }

    /// <summary>
    /// 可作为 SocketsHttpHandler.ConnectCallback 使用：解析目标主机后，
    /// 按 Happy Eyeballs 策略发起连接。
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken,
        TimeSpan? perAttemptTimeout = null,
        TimeSpan? familyDelay = null)
    {
        if (context.DnsEndPoint is null)
        {
            throw new InvalidOperationException("DnsEndPoint is required for Happy Eyeballs connection.");
        }

        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var ordered = OrderCandidates(addresses);

        return await ConnectFamiliesAsync(port, ordered, perAttemptTimeout ?? DefaultPerAttemptTimeout, familyDelay ?? DefaultFamilyDelay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按 orderedAddresses 的顺序逐个发起连接尝试。每发起一个新尝试后，
    /// 等待 familyDelay 时间，期间：
    ///   - 若已发起但尚未确定失败的尝试中有一个率先连接成功，立即返回；
    ///   - 若这些尝试逐个宣告失败且全部失败（没有等到 familyDelay），
    ///     不必再空等剩余时间，立刻发起下一个地址；
    ///   - 若等到 familyDelay 到期仍无结果，则在保留现有尝试的同时并发发起下一个地址。
    /// 全部地址发起完毕后，进入统一等待阶段，直到某个尝试成功或全部失败/超时。
    /// </summary>
    private static async Task<Stream> ConnectFamiliesAsync(
        int port,
        IList<IPAddress> orderedAddresses,
        TimeSpan perAttemptTimeout,
        TimeSpan familyDelay,
        CancellationToken cancellationToken)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = new List<(Task<Socket?> Task, CancellationTokenSource Cts)>();

        // 仅保存"尚未确定失败"的尝试，用于和 delayTask 竞速。
        // 一旦某个尝试失败就立刻从这里移除——否则它会作为一个已完成的任务
        // 一直留在集合里，导致后续每次 Task.WhenAny 都被它"抢先"命中，
        // 使得原本用于错峰发起下一个地址的 familyDelay 形同虚设。
        var outstanding = new List<Task<Socket?>>();

        try
        {
            for (int i = 0; i < orderedAddresses.Count; i++)
            {
                raceCts.Token.ThrowIfCancellationRequested();

                var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(raceCts.Token);
                attemptCts.CancelAfter(perAttemptTimeout);
                var task = ConnectSingleAsync(orderedAddresses[i], port, attemptCts.Token);
                attempts.Add((task, attemptCts));
                outstanding.Add(task);

                var isLast = i == orderedAddresses.Count - 1;
                if (isLast)
                {
                    continue;
                }

                // 在发起下一个地址之前，等待 familyDelay，或提前得到成功/全部失败的结果。
                var delayTask = Task.Delay(familyDelay, raceCts.Token);
                while (outstanding.Count > 0)
                {
                    var completed = await Task.WhenAny(outstanding.Cast<Task>().Append(delayTask)).ConfigureAwait(false);
                    if (completed == delayTask)
                    {
                        break; // 延迟已到，发起下一个地址
                    }

                    var finishedTask = (Task<Socket?>)completed;
                    outstanding.Remove(finishedTask); // 及时清理，避免被重复命中
                    var socket = await finishedTask.ConfigureAwait(false);
                    if (socket != null)
                    {
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    // 失败则继续循环：若 outstanding 已空，会自然跳出等待，立即发起下一个地址
                }
            }

            // 所有地址均已发起连接尝试，统一等待其中任意一个率先完成。
            while (attempts.Count > 0)
            {
                var finishedTask = await Task.WhenAny(attempts.Select(a => (Task)a.Task)).ConfigureAwait(false);
                var finished = attempts.First(a => (Task)a.Task == finishedTask);
                attempts.Remove(finished);

                var socket = await finished.Task.ConfigureAwait(false);
                finished.Cts.Dispose();
                if (socket != null)
                {
                    return new NetworkStream(socket, ownsSocket: true);
                }
            }

            throw new SocketException((int)SocketError.TimedOut);
        }
        finally
        {
            // 无论成功、失败还是异常，都取消并释放所有仍在进行中的尝试。
            raceCts.Cancel();
            foreach (var (_, attemptCts) in attempts)
            {
                attemptCts.Dispose();
            }
        }
    }

    /// <summary>
    /// 对单个地址发起一次 TCP 连接；失败、超时或被取消统一返回 null，
    /// 不向上抛出异常，方便调用方通过返回值判断结果。
    /// </summary>
    private static async Task<Socket?> ConnectSingleAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            return null;
        }
    }
}
