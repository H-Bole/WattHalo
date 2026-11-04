namespace WattMonitor.Updater;

// The old directory remains intact until the committed marker is durable.
internal static class DirectoryTransaction
{
    internal static void Apply(string target, string transaction)
    {
        var candidate = Path.Combine(transaction, "candidate");
        var old = Path.Combine(transaction, "old");
        var targetExists = Directory.Exists(target);
        var candidateExists = Directory.Exists(candidate);
        var oldExists = Directory.Exists(old);
        if (targetExists && candidateExists && !oldExists) Directory.Move(target, old);
        else if (targetExists && !candidateExists && oldExists) return;
        else if (targetExists || !candidateExists || !oldExists) throw new IOException("更新事务目录状态无效。");
        Directory.Move(candidate, target);
    }

    internal static void Restore(string target, string transaction)
    {
        var old = Path.Combine(transaction, "old");
        var failed = Path.Combine(transaction, "failed");
        if (Directory.Exists(old))
        {
            if (Directory.Exists(target))
            {
                if (Directory.Exists(failed)) throw new IOException("恢复目录冲突，原安装备份已保留。");
                Directory.Move(target, failed);
            }
            Directory.Move(old, target);
        }
        else if (!Directory.Exists(target)) throw new IOException("找不到原安装目录和备份，无法自动恢复。");
    }

    // A distinct directory name persists the committed state even if individual markers are deleted.
    internal static void MoveCommittedToCleanup(string transaction, string cleanup)
    {
        if (!Directory.Exists(transaction) || Directory.Exists(cleanup) || File.Exists(cleanup))
            throw new IOException("已提交更新的清理目录状态无效。");
        Directory.Move(transaction, cleanup);
    }

    internal static void FinishCommittedCleanup(string cleanup, Action verifyTarget)
    {
        if (!Directory.Exists(cleanup)) return;
        var old = Path.Combine(cleanup, "old");
        if (Directory.Exists(old))
        {
            verifyTarget();
            Directory.Delete(old, true);
        }
        Directory.Delete(cleanup, true);
    }
}
