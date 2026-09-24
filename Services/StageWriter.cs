using System.Collections.Concurrent;
using DiskGrowthMonitor.Models;

namespace DiskGrowthMonitor.Services;

/// <summary>
/// 扫描结果写库门面：所有数据库访问经由此类集中到**一个后台写线程**（SQLite 连接
/// 不允许并发命令，多线程扫描下的串行化是硬要求）。
///
/// 为什么异步化（空转基线的直接产物）：旧实现里批量落库在扫描线程上同步执行，
/// 每攒满一批（2 万条）枚举就要暂停等写完 —— 全盘约 18 万条目录记录，
/// 写库把枚举流水线打断了 9 次。改为后台写后，扫描线程只做「入队」，落库与枚举重叠；
/// 队列上限 4 批（约 8 万行），写慢了入队自然阻塞，内存峰值有界。
///
/// 失败语义（与旧实现不同，须注意）：
///   · 批量插入失败 → 记录 <see cref="Error"/> 并丢弃该批；扫描照常跑完（计数在内存里仍是
///     完整的），但 <see cref="DiskScanner.Scan"/> 结束时会把 <c>WriteError</c> 带回，
///     Program 据此**中止提交** —— 暂存表是一次性数据（下轮 CreateStageTable 重建），
///     不提交即不影响上一轮快照，宁可本轮白扫也不让残缺数据进入对比。
///   · 子树复用失败 → 异常沿调用栈抛回扫描线程（与旧实现一致，由 ScanRoot 捕获记为根失败）。
/// </summary>
public sealed class StageWriter
{
    private readonly DatabaseService _db;
    private readonly BlockingCollection<WorkItem> _queue;
    private readonly Thread _worker;
    private volatile Exception? _error;
    private bool _completed;

    public StageWriter(DatabaseService db)
    {
        _db = db;
        // 上限 4 批 × 2 万条 = 约 8 万行在途，足够让写库全程不空转，又不至于在
        // 「磁盘满 / 库损坏」等慢写场景下把整棵扫描树的缓冲堆在内存里
        _queue = new BlockingCollection<WorkItem>(4);
        _worker = new Thread(WorkLoop) { IsBackground = true, Name = "StageWriter" };
        _worker.Start();
    }

    /// <summary>写库线程捕获的第一个异常（供扫描结束后统一上报）。null = 无错误。</summary>
    public Exception? Error => _error;

    /// <summary>批量写入暂存表（异步入队；队列满时阻塞 = 对扫描线程的天然背压）。</summary>
    public void InsertBatch(IReadOnlyList<DirSnapshot> batch)
    {
        if (batch.Count == 0 || _completed) return;
        _queue.Add(new BatchItem(batch));
    }

    /// <summary>
    /// 尝试复用 dirPath 的整个子树：上一轮快照存在该目录行时，把其子树全部行复制进暂存表。
    /// 结果由扫描线程同步等待（子树统计是后续累加的输入，无法异步）；
    /// 经同一队列执行以维持与批量插入的先后顺序（SQLite 单连接禁止并发命令）。
    /// </summary>
    /// <param name="currentDepth">该目录在本次扫描中的层级（用于校正与上次扫描根不同导致的 depth 偏移）。</param>
    public bool TryReuseSubtree(string rootKey, string dirPath, int currentDepth, out DirStat stat)
    {
        stat = default;
        if (_completed) return false;

        var item = new ReuseItem(rootKey, dirPath, currentDepth);
        _queue.Add(item);
        try
        {
            // net45 的 TaskCompletionSource 不继承 Task（.NET 5+ 才有），必须经 .Task 等待
            item.Done.Task.Wait();
        }
        catch (AggregateException ex)
        {
            // 还原工作线程里的原始异常（去掉 AggregateException 包装），让 ScanRoot 按旧路径捕获
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
        }
        stat = item.Result!.Stat;
        return item.Result!.Ok;
    }

    /// <summary>
    /// 结束写入并等待写线程把队列清空。由 <see cref="DiskScanner.Scan"/> 在全部扫描根
    /// 结束后调用（无论成败都必须调，否则写线程挂着直到进程退出）。
    /// </summary>
    public void Flush()
    {
        if (_completed) return;
        _completed = true;
        _queue.CompleteAdding();
        _worker.Join();
    }

    private void WorkLoop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                switch (item)
                {
                    case BatchItem b:
                        _db.InsertStageBatch(b.Batch);
                        break;
                    case ReuseItem r:
                        bool ok = _db.TryReuseSubtree(r.RootKey, r.DirPath, r.Depth, out var stat);
                        r.Result = new ReuseResult(ok, stat);
                        r.Done.SetResult(true);
                        break;
                }
            }
            catch (Exception ex)
            {
                if (item is ReuseItem fail)
                {
                    // 复用失败：调用方在同步等待，必须把异常送回去
                    fail.Done.SetException(ex);
                }
                else
                {
                    // 批量插入失败：只记第一个错误；后续批次继续尝试（可能只是单批偶发）
                    _error ??= ex;
                }
            }
        }
    }

    // ------------------------------------------------------------------ 工作项

    private abstract class WorkItem { }

    private sealed class BatchItem : WorkItem
    {
        public readonly IReadOnlyList<DirSnapshot> Batch;
        public BatchItem(IReadOnlyList<DirSnapshot> batch) => Batch = batch;
    }

    private sealed class ReuseItem : WorkItem
    {
        public readonly string RootKey;
        public readonly string DirPath;
        public readonly int Depth;
        public readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.None);
        public ReuseResult? Result;

        public ReuseItem(string rootKey, string dirPath, int depth)
        {
            RootKey = rootKey;
            DirPath = dirPath;
            Depth = depth;
        }
    }

    private sealed class ReuseResult
    {
        public readonly bool Ok;
        public readonly DirStat Stat;
        public ReuseResult(bool ok, DirStat stat) { Ok = ok; Stat = stat; }
    }
}
