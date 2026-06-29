using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Cue.ViewModels;

namespace Cue.Pages;

/// <summary>
/// A layout-agnostic drag-to-reorder controller for one virtualized <see cref="ItemsRepeater"/> of
/// <see cref="TaskRowViewModel"/> rows. It reproduces the iOS Springboard feel on the desktop with
/// native WinUI pointer events instead of the framework's ghost-image drag/drop, <b>without</b>
/// fighting virtualization:
/// <list type="bullet">
/// <item>the pressed row is lifted and tracks the pointer 1:1 via a live <c>RenderTransform</c>;</item>
/// <item>only the <i>realized</i> rows in (and near) the viewport open a gap, animated with
/// <see cref="Storyboard"/> + <see cref="CubicEase"/> — off-screen rows are never touched;</item>
/// <item>a hysteresis dead-zone around each neighbor's center keeps the target slot from flickering;</item>
/// <item>dragging to a viewport edge auto-scrolls the list, and rows realized by that scroll join the
/// gap tracking on the next frame;</item>
/// <item>on drop the new order is applied <i>optimistically</i> to the bound collection and the moved
/// row's rank is persisted from its two real neighbors' ranks (held in memory for every row, realized
/// or not); only that one record is written, and a failed save reloads from the index.</item>
/// </list>
/// </summary>
/// <remarks>
/// Per-row geometry is read each frame as <c>measuredTop − appliedTranslate</c>, which recovers a
/// row's true layout position regardless of the gap transform we put on it. That makes the math
/// correct for variable-height rows and robust to element recycling: a container reused for a
/// different item during a scroll simply reports its current layout slot. Removing the dragged row
/// closes exactly its own height and the gap opens exactly that height, so the rows between the source
/// slot and the target slot shift by the dragged row's height — and we only ever apply that shift to
/// the realized ones.
/// </remarks>
public sealed class ReorderSurface
{
    private const double StartThreshold = 6.0;     // px of travel before a press becomes a drag
    private const double GapAnimationMs = 220.0;
    private const double DropAnimationMs = 170.0;
    private const double Epsilon = 0.5;
    private const double EdgeZone = 56.0;          // px from a viewport edge that triggers auto-scroll
    private const double MaxAutoScrollStep = 18.0; // px per ~16ms tick at the very edge

    private readonly ItemsRepeater _repeater;
    private readonly Func<ObservableCollection<TaskRowViewModel>, Guid, Task> _commitAsync;
    private readonly bool _animationsEnabled;
    private readonly Action<bool>? _suppressEntrance;

    private Drag? _drag;

    private ReorderSurface(
        ItemsRepeater repeater,
        Func<ObservableCollection<TaskRowViewModel>, Guid, Task> commitAsync,
        bool animationsEnabled,
        Action<bool>? suppressEntrance)
    {
        _repeater = repeater;
        _commitAsync = commitAsync;
        _animationsEnabled = animationsEnabled;
        _suppressEntrance = suppressEntrance;
    }

    /// <summary>Wires reorder gestures onto <paramref name="repeater"/> and returns the controller.</summary>
    public static ReorderSurface Attach(
        ItemsRepeater repeater,
        Func<ObservableCollection<TaskRowViewModel>, Guid, Task> commitAsync,
        bool animationsEnabled,
        Action<bool>? suppressEntrance = null)
    {
        var surface = new ReorderSurface(repeater, commitAsync, animationsEnabled, suppressEntrance);
        repeater.PointerPressed += surface.OnPointerPressed;
        repeater.PointerMoved += surface.OnPointerMoved;
        repeater.PointerReleased += surface.OnPointerReleased;
        repeater.PointerCanceled += surface.OnPointerLost;
        repeater.PointerCaptureLost += surface.OnPointerLost;
        return surface;
    }

    // Pointer plumbing

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not null) return;
        if (_repeater.ItemsSource is not ObservableCollection<TaskRowViewModel> items) return;

        var element = ChildFromSource(e.OriginalSource as DependencyObject);
        if (element is null) return;
        // The row template binds with compiled x:Bind, which does NOT flow a DataContext onto the
        // realized container — so map the element back to its row through the repeater's item index
        // rather than its (always-null) DataContext.
        var pressedIndex = _repeater.GetElementIndex(element);
        if (pressedIndex < 0 || pressedIndex >= items.Count) return;
        var row = items[pressedIndex];
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

        // Arm — but don't capture yet. We only become a drag once the pointer crosses the threshold,
        // so plain taps (handled elsewhere) and checkbox clicks keep working.
        _drag = new Drag(items, element, row, e.GetCurrentPoint(_repeater).Position, e.Pointer.PointerId);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || e.Pointer.PointerId != drag.PointerId) return;
        var repeaterPoint = e.GetCurrentPoint(_repeater).Position;

        if (!drag.Started)
        {
            if (Math.Abs(repeaterPoint.Y - drag.StartPoint.Y) < StartThreshold) return;
            if (!BeginDrag(drag)) { _drag = null; return; }
            _repeater.CapturePointer(e.Pointer);
        }

        e.Handled = true;
        drag.LastRepeaterPoint = repeaterPoint;
        if (drag.ScrollViewer is { } sv)
            drag.LastViewportPoint = e.GetCurrentPoint(sv).Position;
        Update(drag);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || e.Pointer.PointerId != drag.PointerId) return;
        if (!drag.Started) { _drag = null; return; }
        e.Handled = true;
        _repeater.ReleasePointerCapture(e.Pointer);
        _ = DropAsync(drag);
    }

    private void OnPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || !drag.Started) { _drag = null; return; }
        _ = DropAsync(drag); // capture lost mid-drag — settle into the current target
    }

    // Drag lifecycle

    private bool BeginDrag(Drag drag)
    {
        var top = LayoutTop(drag.Element);
        if (top is not { } originTop) return false;

        drag.OriginBaseTop = originTop;
        drag.DraggedHeight = drag.Element.ActualHeight;
        drag.GrabOffset = drag.StartPoint.Y - originTop;
        drag.GapTop = originTop;                 // gap starts in the row's own slot
        drag.ScrollViewer = FindScrollViewer(_repeater);
        drag.Started = true;
        _suppressEntrance?.Invoke(true);

        var lifted = TransformOf(drag.Element);
        Canvas.SetZIndex(drag.Element, 50);
        if (_animationsEnabled)
        {
            lifted.CenterX = drag.Element.ActualWidth / 2;
            lifted.CenterY = drag.Element.ActualHeight / 2;
            lifted.ScaleX = 1.03;
            lifted.ScaleY = 1.03;
            drag.Element.Opacity = 0.94;
        }

        drag.AutoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        drag.AutoScrollTimer.Tick += (_, _) => OnAutoScrollTick(drag);
        return true;
    }

    /// <summary>One frame of the drag: track the pointer, re-target the gap, and shift realized rows.</summary>
    private void Update(Drag drag)
    {
        // The dragged container can be recycled if its layout slot scrolls far out of view — settle.
        if (_repeater.GetElementIndex(drag.Element) < 0) { _ = DropAsync(drag); return; }

        var pointerY = PointerRepeaterY(drag);
        var draggedTop = pointerY - drag.GrabOffset;
        TransformOf(drag.Element).TranslateY = draggedTop - drag.OriginBaseTop;
        var draggedCenter = draggedTop + drag.DraggedHeight / 2;

        var siblings = RealizedSiblings(drag);
        var margin = Math.Max(6.0, drag.DraggedHeight * 0.2);

        // Hysteresis: only re-target when the dragged center passes a neighbor center by the margin.
        var k = drag.TargetIndex;
        if (k > siblings.Count) k = siblings.Count;
        while (k < siblings.Count && draggedCenter > siblings[k].Center + margin) k++;
        while (k > 0 && draggedCenter < siblings[k - 1].Center - margin) k--;
        drag.TargetIndex = k;

        drag.GapTop = k < siblings.Count
            ? siblings[k].Top
            : siblings.Count > 0 ? siblings[^1].Top + siblings[^1].Height : drag.OriginBaseTop;
        drag.NextRow = k < siblings.Count ? siblings[k].Row : null;
        drag.PrevRow = k > 0 ? siblings[k - 1].Row : null;

        ApplyGap(drag, siblings);
        UpdateAutoScroll(drag);
    }

    /// <summary>Animates each realized sibling to its resting position for the current gap.</summary>
    private void ApplyGap(Drag drag, List<Sibling> siblings)
    {
        Storyboard? storyboard = null;
        foreach (var sibling in siblings)
        {
            var shift = ShiftFor(drag, sibling.Top);
            if (Math.Abs(sibling.Transform.TranslateY - shift) < Epsilon) continue;

            if (_animationsEnabled)
            {
                storyboard ??= new Storyboard();
                AddDouble(storyboard, sibling.Transform, "TranslateY", shift, GapAnimationMs);
            }
            else
            {
                sibling.Transform.TranslateY = shift;
            }
        }
        storyboard?.Begin();
    }

    /// <summary>The displacement a row at <paramref name="rowTop"/> must take to open the gap.</summary>
    private static double ShiftFor(Drag drag, double rowTop)
    {
        if (drag.OriginBaseTop < drag.GapTop)
        {
            // Dragging down: rows below the origin and above the gap rise to fill the vacated slot.
            return rowTop > drag.OriginBaseTop + Epsilon && rowTop < drag.GapTop - Epsilon
                ? -drag.DraggedHeight : 0.0;
        }
        // Dragging up: rows at/below the gap and above the origin sink to open room above.
        return rowTop > drag.GapTop - Epsilon && rowTop < drag.OriginBaseTop - Epsilon
            ? drag.DraggedHeight : 0.0;
    }

    private async Task DropAsync(Drag drag)
    {
        // Keep `_drag` set through the settle so no new gesture starts mid-commit; cleared at the end.
        if (drag.Dropping) return;
        drag.Dropping = true;
        drag.AutoScrollTimer?.Stop();

        // Ease the lifted row down into the gap before the data move, so it lands rather than snaps.
        if (_animationsEnabled && _repeater.GetElementIndex(drag.Element) >= 0)
        {
            var settle = new Storyboard();
            var transform = TransformOf(drag.Element);
            AddDouble(settle, transform, "TranslateY", drag.GapTop - drag.OriginBaseTop, DropAnimationMs);
            AddDouble(settle, transform, "ScaleX", 1.0, DropAnimationMs);
            AddDouble(settle, transform, "ScaleY", 1.0, DropAnimationMs);
            drag.Element.Opacity = 1.0;
            var tcs = new TaskCompletionSource();
            settle.Completed += (_, _) => tcs.TrySetResult();
            settle.Begin();
            await tcs.Task;
        }

        var items = drag.Items;
        var oldIndex = items.IndexOf(drag.Row);
        if (oldIndex < 0) { ClearVisuals(drag); _suppressEntrance?.Invoke(false); _drag = null; return; }

        // Map the gap's realized neighbors to a collection index.
        var newIndex = oldIndex;
        if (drag.NextRow is { } next)
        {
            var ni = items.IndexOf(next);
            if (ni >= 0) newIndex = oldIndex < ni ? ni - 1 : ni;
        }
        else if (drag.PrevRow is { } prev)
        {
            var pi = items.IndexOf(prev);
            if (pi >= 0) newIndex = oldIndex <= pi ? pi : pi + 1;
        }
        newIndex = Math.Clamp(newIndex, 0, items.Count - 1);

        ClearVisuals(drag);

        if (newIndex != oldIndex)
        {
            var movedId = drag.Row.Id;
            items.Move(oldIndex, newIndex);
            _ = PersistAsync(items, movedId);
        }
        _suppressEntrance?.Invoke(false);
        _drag = null;
    }

    private void ClearVisuals(Drag drag)
    {
        // Clearing before the data move lets the layout pass land cleanly: realized rows are already at
        // their final slots, so dropping the transforms leaves no residual offset.
        foreach (var sibling in RealizedSiblings(drag))
            sibling.Transform.TranslateY = 0;

        drag.Element.RenderTransform = null;
        drag.Element.Opacity = 1.0;
        Canvas.SetZIndex(drag.Element, 0);
    }

    private async Task PersistAsync(ObservableCollection<TaskRowViewModel> items, Guid movedId)
    {
        try { await _commitAsync(items, movedId); }
        catch { /* the view model reloads from the index on failure */ }
    }

    // Auto-scroll

    private void UpdateAutoScroll(Drag drag)
    {
        if (drag.ScrollViewer is not { } sv || drag.AutoScrollTimer is not { } timer) return;
        var y = drag.LastViewportPoint.Y;
        var height = sv.ViewportHeight;

        drag.AutoScrollVelocity =
            y < EdgeZone ? -EdgeFactor(EdgeZone - y) :
            y > height - EdgeZone ? EdgeFactor(y - (height - EdgeZone)) :
            0.0;

        if (drag.AutoScrollVelocity != 0.0)
        {
            if (!timer.IsEnabled) timer.Start();
        }
        else if (timer.IsEnabled)
        {
            timer.Stop();
        }
    }

    private void OnAutoScrollTick(Drag drag)
    {
        if (drag.ScrollViewer is not { } sv) return;
        var target = sv.VerticalOffset + drag.AutoScrollVelocity;
        sv.ChangeView(null, target, null, disableAnimation: true);
        // Re-run a frame so the gap follows the now-stationary pointer and any newly realized rows join.
        Update(drag);
    }

    private static double EdgeFactor(double depth)
        => MaxAutoScrollStep * Math.Clamp(depth / EdgeZone, 0.0, 1.0);

    // Geometry

    /// <summary>Realized rows other than the dragged one, with clean layout geometry, top-sorted.</summary>
    private List<Sibling> RealizedSiblings(Drag drag)
    {
        var siblings = new List<Sibling>();
        var count = VisualTreeHelper.GetChildrenCount(_repeater);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(_repeater, i) is not FrameworkElement fe) continue;
            if (ReferenceEquals(fe, drag.Element)) continue;
            // x:Bind leaves DataContext null on realized rows; resolve the row by item index instead.
            var index = _repeater.GetElementIndex(fe);
            if (index < 0 || index >= drag.Items.Count) continue;
            var row = drag.Items[index];

            var transform = TransformOf(fe);
            var measured = fe.TransformToVisual(_repeater).TransformPoint(new Point(0, 0)).Y;
            var top = measured - transform.TranslateY; // recover the true layout slot
            siblings.Add(new Sibling(fe, row, transform, top, fe.ActualHeight));
        }
        siblings.Sort(static (a, b) => a.Top.CompareTo(b.Top));
        return siblings;
    }

    private double PointerRepeaterY(Drag drag)
        => drag.ScrollViewer is { } sv
            ? sv.TransformToVisual(_repeater).TransformPoint(drag.LastViewportPoint).Y
            : drag.LastRepeaterPoint.Y;

    private double? LayoutTop(FrameworkElement element)
    {
        try
        {
            var transform = element.RenderTransform as CompositeTransform;
            var measured = element.TransformToVisual(_repeater).TransformPoint(new Point(0, 0)).Y;
            return measured - (transform?.TranslateY ?? 0);
        }
        catch
        {
            return null;
        }
    }

    private static CompositeTransform TransformOf(FrameworkElement element)
    {
        if (element.RenderTransform is CompositeTransform existing) return existing;
        var transform = new CompositeTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private FrameworkElement? ChildFromSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (VisualTreeHelper.GetParent(current) == _repeater && current is FrameworkElement fe)
                return fe;
        }
        return null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject start)
    {
        for (var current = VisualTreeHelper.GetParent(start); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer sv) return sv;
        }
        return null;
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or CheckBox or Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)
                return true;
            if (current is ItemsRepeater)
                break;
        }
        return false;
    }

    private static void AddDouble(Storyboard storyboard, DependencyObject target, string property, double to, double durationMs)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private readonly record struct Sibling(
        FrameworkElement Element, TaskRowViewModel Row, CompositeTransform Transform, double Top, double Height)
    {
        public double Center => Top + Height / 2;
    }

    /// <summary>State for one in-flight drag.</summary>
    private sealed class Drag
    {
        public Drag(ObservableCollection<TaskRowViewModel> items, FrameworkElement element, TaskRowViewModel row, Point start, uint pointerId)
        {
            Items = items;
            Element = element;
            Row = row;
            StartPoint = start;
            LastRepeaterPoint = start;
            PointerId = pointerId;
        }

        public ObservableCollection<TaskRowViewModel> Items { get; }
        public FrameworkElement Element { get; }
        public TaskRowViewModel Row { get; }
        public Point StartPoint { get; }
        public uint PointerId { get; }

        public bool Started { get; set; }
        public bool Dropping { get; set; }
        public double OriginBaseTop { get; set; }
        public double DraggedHeight { get; set; }
        public double GrabOffset { get; set; }
        public double GapTop { get; set; }
        public int TargetIndex { get; set; }
        public TaskRowViewModel? PrevRow { get; set; }
        public TaskRowViewModel? NextRow { get; set; }

        public ScrollViewer? ScrollViewer { get; set; }
        public DispatcherTimer? AutoScrollTimer { get; set; }
        public double AutoScrollVelocity { get; set; }
        public Point LastViewportPoint { get; set; }
        public Point LastRepeaterPoint { get; set; }
    }
}
