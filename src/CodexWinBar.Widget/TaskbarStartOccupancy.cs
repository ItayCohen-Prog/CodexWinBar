using System.Drawing;
using System.Windows.Automation;

namespace CodexWinBar.Widget;

internal enum TaskbarStartLayoutStatus
{
    Success,
    Failed,
}

internal sealed record TaskbarStartLayout(TaskbarStartLayoutStatus Status, int? OccupiedRight, Rectangle? AppClusterRect)
{
    internal static TaskbarStartLayout Failed(Rectangle? fallbackAppCluster) =>
        new(TaskbarStartLayoutStatus.Failed, null, fallbackAppCluster);
}

/// <summary>
/// Measures interactive Windows taskbar content that occupies the start edge. Windows 11 renders
/// Widgets/Weather and the centered app band through XAML islands, so their useful geometry is not
/// consistently represented by child HWNDs. UI Automation exposes the actual button rectangles.
/// </summary>
internal static class TaskbarStartOccupancy
{
    private sealed record PendingMeasurement(int Generation, Task<TaskbarStartLayout> Task);

    private static readonly object PendingGate = new();
    private static readonly Dictionary<IntPtr, PendingMeasurement> Pending = [];
    private static readonly Dictionary<IntPtr, int> Generations = [];

    internal static TaskbarStartLayout Measure(IntPtr taskbar, Rectangle taskbarRect, Rectangle? fallbackAppCluster)
    {
        if (taskbar == IntPtr.Zero || taskbarRect.IsEmpty)
        {
            return TaskbarStartLayout.Failed(fallbackAppCluster);
        }

        Task<TaskbarStartLayout>? measurement = null;
        int generation;
        lock (PendingGate)
        {
            generation = Generations.GetValueOrDefault(taskbar);
            if (Pending.TryGetValue(taskbar, out PendingMeasurement? pending))
            {
                if (!pending.Task.IsCompleted)
                {
                    return TaskbarStartLayout.Failed(fallbackAppCluster);
                }

                if (pending.Generation == generation)
                {
                    measurement = pending.Task;
                }
                else
                {
                    Pending.Remove(taskbar);
                }
            }

            if (measurement is null)
            {
                measurement = Task.Run(() => MeasureCore(taskbar, taskbarRect, fallbackAppCluster));
                Pending[taskbar] = new PendingMeasurement(generation, measurement);
            }
        }

        // UI Automation calls run off the widget's window-owning thread. In embedded mode our own
        // child HWND is part of the taskbar subtree, and Microsoft explicitly warns against querying
        // a client's own UI on its UI thread. A stuck Explorer provider remains the sole pending
        // request for this taskbar instead of leaking another blocked worker on every refresh.
        if (!measurement.IsCompleted && !measurement.Wait(TimeSpan.FromMilliseconds(300)))
        {
            return TaskbarStartLayout.Failed(fallbackAppCluster);
        }

        lock (PendingGate)
        {
            if (Generations.GetValueOrDefault(taskbar) != generation)
            {
                return TaskbarStartLayout.Failed(fallbackAppCluster);
            }

            if (Pending.TryGetValue(taskbar, out PendingMeasurement? current) && ReferenceEquals(current.Task, measurement))
            {
                Pending.Remove(taskbar);
            }
        }

        return measurement.GetAwaiter().GetResult();
    }

    internal static void Invalidate(IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero)
        {
            return;
        }

        lock (PendingGate)
        {
            Generations[taskbar] = Generations.GetValueOrDefault(taskbar) + 1;
        }
    }

    private static TaskbarStartLayout MeasureCore(IntPtr taskbar, Rectangle taskbarRect, Rectangle? fallbackAppCluster)
    {
        Exception? lastError = null;
        IntPtr bridge = TaskbarInterop.TryGetAutomationBridge(taskbar);
        IntPtr[] handles = bridge == IntPtr.Zero ? [taskbar] : [taskbar, bridge];
        foreach (IntPtr handle in handles)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    AutomationElement root = AutomationElement.FromHandle(handle);
                    TaskbarStartLayout? measured = MeasureRoot(root, taskbarRect, fallbackAppCluster);
                    if (measured is not null)
                    {
                        return measured;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                if (attempt == 0)
                {
                    Thread.Sleep(20);
                }
            }
        }

        if (lastError is not null)
        {
            WidgetLog.Write($"Taskbar start occupancy measurement failed for 0x{taskbar.ToInt64():X}: {lastError.GetType().Name} (0x{lastError.HResult:X8}): {lastError.Message}");
        }
        else
        {
            WidgetLog.Write($"Taskbar start occupancy measurement returned no usable tree for 0x{taskbar.ToInt64():X}.");
        }

        return TaskbarStartLayout.Failed(fallbackAppCluster);
    }

    private static TaskbarStartLayout? MeasureRoot(AutomationElement root, Rectangle taskbarRect, Rectangle? fallbackAppCluster)
    {
        CacheRequest request = new()
        {
            TreeScope = TreeScope.Element,
        };
        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        AutomationElementCollection descendants;
        using (request.Activate())
        {
            descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        }

        List<(Rectangle Bounds, string AutomationId)> interactive = [];

        for (int i = 0; i < descendants.Count; i++)
        {
            try
            {
                AutomationElement.AutomationElementInformation current = descendants[i].Cached;
                Rectangle bounds = ToRectangle(current.BoundingRectangle);
                if (!current.IsOffscreen && IsInteractive(current.ControlType) && IsInTaskbarBand(taskbarRect, bounds))
                {
                    interactive.Add((bounds, current.AutomationId));
                }
            }
            catch (Exception)
            {
                // XAML descendants can disappear while Explorer is rebuilding the taskbar. One
                // stale element must not discard the rest of an otherwise valid measurement.
            }
        }

        // Every usable Windows 11 taskbar tree exposes at least Start and its app buttons. If an outer
        // HWND yields no interactive taskbar elements, let the caller retry the XAML bridge root.
        if (interactive.Count == 0)
        {
            return null;
        }

        Rectangle? appCluster = MergeAppClusters(
            taskbarRect,
            DeriveAppCluster(taskbarRect, interactive),
            fallbackAppCluster);
        int bandEnd = appCluster?.Left ?? taskbarRect.Left + (taskbarRect.Width / 2);
        int? occupiedRight = ContiguousStartRight(taskbarRect, bandEnd, interactive.Select(item => item.Bounds));
        return new TaskbarStartLayout(TaskbarStartLayoutStatus.Success, occupiedRight, appCluster);
    }

    /// <summary>Derives the complete Start/Search/Task View/pinned-app run from visible taskbar
    /// buttons. The run containing Start is preferred over undocumented internal XAML element names.</summary>
    internal static Rectangle? DeriveAppCluster(
        Rectangle taskbarRect,
        IEnumerable<(Rectangle Bounds, string AutomationId)> elements)
    {
        int adjacency = Math.Max(2, taskbarRect.Height / 2);
        List<List<(Rectangle Bounds, string AutomationId)>> runs = [];
        foreach ((Rectangle bounds, string automationId) in elements
            .Where(item => IsInTaskbarBand(taskbarRect, item.Bounds))
            .OrderBy(item => item.Bounds.Left)
            .ThenBy(item => item.Bounds.Right))
        {
            if (runs.Count == 0 || bounds.Left > runs[^1].Max(item => item.Bounds.Right) + adjacency)
            {
                runs.Add([]);
            }

            runs[^1].Add((bounds, automationId));
        }

        List<(Rectangle Bounds, string AutomationId)>? selected = runs
            .Where(run => run.Any(item => string.Equals(item.AutomationId, "StartButton", StringComparison.Ordinal)))
            .OrderByDescending(run => run.Count)
            .FirstOrDefault();
        selected ??= runs
            .Where(run => run.Any(item => IsAppClusterSeed(item.AutomationId)))
            .OrderByDescending(run => run.Count(item => IsAppClusterSeed(item.AutomationId)))
            .ThenBy(run => Math.Abs(RunBounds(run).Left + (RunBounds(run).Width / 2) - (taskbarRect.Left + (taskbarRect.Width / 2))))
            .FirstOrDefault();

        return selected is null ? null : Rectangle.Intersect(taskbarRect, RunBounds(selected));
    }

    /// <summary>
    /// Returns the far edge of the contiguous interactive block beginning at the taskbar start.
    /// Isolated controls near the centered app cluster are deliberately ignored.
    /// </summary>
    internal static int? ContiguousStartRight(Rectangle taskbarRect, int bandEnd, IEnumerable<Rectangle> candidates)
    {
        if (taskbarRect.IsEmpty || bandEnd <= taskbarRect.Left)
        {
            return null;
        }

        int currentRight = taskbarRect.Left;
        int allowedGap = Math.Max(1, taskbarRect.Height);
        IEnumerable<Rectangle> ordered = candidates
            .Where(rect => IsCandidateInStartBand(taskbarRect, bandEnd, rect))
            .OrderBy(rect => rect.Left)
            .ThenByDescending(rect => rect.Right);

        foreach (Rectangle rect in ordered)
        {
            if (rect.Left > currentRight + allowedGap)
            {
                break;
            }

            currentRight = Math.Max(currentRight, Math.Min(rect.Right, bandEnd));
        }

        return currentRight > taskbarRect.Left ? currentRight : null;
    }

    private static bool IsCandidateInStartBand(Rectangle taskbarRect, int bandEnd, Rectangle rect) =>
        !rect.IsEmpty && rect.Left >= taskbarRect.Left && rect.Right <= bandEnd && IsInTaskbarBand(taskbarRect, rect);

    private static bool IsInTaskbarBand(Rectangle taskbarRect, Rectangle rect)
    {
        if (rect.IsEmpty)
        {
            return false;
        }

        Rectangle overlap = Rectangle.Intersect(taskbarRect, rect);
        return overlap.Width > 0 && overlap.Height >= Math.Max(1, Math.Min(taskbarRect.Height, rect.Height) / 2);
    }

    private static bool IsInteractive(ControlType controlType) =>
        controlType == ControlType.Button ||
        controlType == ControlType.Hyperlink ||
        controlType == ControlType.MenuItem ||
        controlType == ControlType.SplitButton;

    private static bool IsAppClusterSeed(string automationId) =>
        string.Equals(automationId, "StartButton", StringComparison.Ordinal) ||
        string.Equals(automationId, "SearchButton", StringComparison.Ordinal) ||
        string.Equals(automationId, "TaskViewButton", StringComparison.Ordinal) ||
        string.Equals(automationId, "CopilotButton", StringComparison.Ordinal) ||
        automationId.StartsWith("Appid:", StringComparison.Ordinal);

    private static Rectangle? MergeAppClusters(Rectangle taskbarRect, Rectangle? automationCluster, Rectangle? fallbackCluster)
    {
        Rectangle fallback = fallbackCluster is null ? Rectangle.Empty : Rectangle.Intersect(taskbarRect, fallbackCluster.Value);
        if (fallback.IsEmpty || fallback.Width >= taskbarRect.Width / 2)
        {
            fallback = Rectangle.Empty;
        }

        if (automationCluster is null)
        {
            return fallback.IsEmpty ? null : fallback;
        }

        Rectangle automation = Rectangle.Intersect(taskbarRect, automationCluster.Value);
        if (automation.IsEmpty)
        {
            return fallback.IsEmpty ? null : fallback;
        }

        return fallback.IsEmpty ? automation : Rectangle.Intersect(taskbarRect, Rectangle.Union(automation, fallback));
    }

    private static Rectangle RunBounds(IEnumerable<(Rectangle Bounds, string AutomationId)> run)
    {
        using IEnumerator<(Rectangle Bounds, string AutomationId)> enumerator = run.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return Rectangle.Empty;
        }

        Rectangle result = enumerator.Current.Bounds;
        while (enumerator.MoveNext())
        {
            result = Rectangle.Union(result, enumerator.Current.Bounds);
        }

        return result;
    }

    private static Rectangle ToRectangle(System.Windows.Rect rect)
    {
        if (rect.IsEmpty || double.IsNaN(rect.Left) || double.IsNaN(rect.Top) ||
            double.IsNaN(rect.Right) || double.IsNaN(rect.Bottom))
        {
            return Rectangle.Empty;
        }

        return Rectangle.FromLTRB(
            (int)Math.Floor(rect.Left),
            (int)Math.Floor(rect.Top),
            (int)Math.Ceiling(rect.Right),
            (int)Math.Ceiling(rect.Bottom));
    }
}
