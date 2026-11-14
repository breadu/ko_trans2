using System.Collections.Generic;
namespace KoTrans.Infrastructure;

public static class ShutdownCleanup
{
    public static async Task<IReadOnlyList<string>> RunAsync(IEnumerable<(string Name, Func<Task> Cleanup)> steps,
        Action<string>? onSlow = null, TimeSpan? noticeAfter = null)
    {
        var failed = new System.Collections.Generic.List<string>();
        foreach (var step in steps)
        {
            try {
                var task = step.Cleanup();
                if (onSlow != null && !task.IsCompleted) {
                    using var stopTimer = new CancellationTokenSource();
                    var delay = Task.Delay(noticeAfter ?? TimeSpan.FromSeconds(2), stopTimer.Token);
                    await Task.WhenAny(task, delay);
                    stopTimer.Cancel();
                    if (!task.IsCompleted) onSlow(step.Name);
                }
                await task;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { failed.Add(step.Name); }
        }
        return failed;
    }
}
