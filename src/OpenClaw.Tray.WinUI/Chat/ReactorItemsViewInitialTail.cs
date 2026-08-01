using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;
using WinUIItemsView = Microsoft.UI.Xaml.Controls.ItemsView;
using WinUIScrollingAnimationMode = Microsoft.UI.Xaml.Controls.ScrollingAnimationMode;
using WinUIScrollingInteractionState = Microsoft.UI.Xaml.Controls.ScrollingInteractionState;
using WinUIScrollingScrollCompletedEventArgs = Microsoft.UI.Xaml.Controls.ScrollingScrollCompletedEventArgs;
using WinUIScrollingScrollOptions = Microsoft.UI.Xaml.Controls.ScrollingScrollOptions;
using WinUIScrollingSnapPointsMode = Microsoft.UI.Xaml.Controls.ScrollingSnapPointsMode;
using WinUIScrollView = Microsoft.UI.Xaml.Controls.ScrollView;

namespace OpenClawTray.Chat;

file sealed record ItemsViewInitialTailElement(
    Element Child,
    int InitialTailIndex,
    string InitialTailRequestKey) : Element
{
    static ItemsViewInitialTailElement() =>
        ControlRegistry.RegisterDecorator<ItemsViewInitialTailElement>(
            static () => new ItemsViewInitialTailHandler());
}

file sealed class ItemsViewInitialTailHandler
    : IDecoratorElementHandler<ItemsViewInitialTailElement>
{
    private static readonly ConditionalWeakTable<WinUIItemsView, ItemsViewInitialTailPositioner> Positioners = new();

    public UIElement Mount(MountContext context, ItemsViewInitialTailElement element)
    {
        var control = context.MountChild(element.Child);
        if (control is not WinUIItemsView itemsView)
            throw new InvalidOperationException("ItemsView initial-tail positioning requires an ItemsView child.");

        var positioner = new ItemsViewInitialTailPositioner(itemsView);
        Positioners.Add(itemsView, positioner);
        positioner.Request(element.InitialTailIndex, element.InitialTailRequestKey);
        return itemsView;
    }

    public UIElement Update(
        UpdateContext context,
        ItemsViewInitialTailElement oldElement,
        ItemsViewInitialTailElement newElement,
        UIElement control)
    {
        var updated = context.ReconcileChild(oldElement.Child, newElement.Child, control);
        if (updated is not WinUIItemsView itemsView)
            throw new InvalidOperationException("ItemsView initial-tail positioning requires an ItemsView child.");
        if (!string.Equals(oldElement.InitialTailRequestKey, newElement.InitialTailRequestKey, StringComparison.Ordinal)
            && Positioners.TryGetValue(itemsView, out var positioner))
            positioner.Request(newElement.InitialTailIndex, newElement.InitialTailRequestKey);
        else if (Positioners.TryGetValue(itemsView, out var existingPositioner))
            existingPositioner.UpdateTailIndex(newElement.InitialTailIndex);
        return itemsView;
    }

    public V1UnmountDisposition Unmount(UnmountContext context, ItemsViewInitialTailElement? element, UIElement control)
    {
        if (control is WinUIItemsView itemsView && Positioners.TryGetValue(itemsView, out var positioner))
        {
            Positioners.Remove(itemsView);
            positioner.Dispose();
        }
        return V1UnmountDisposition.ContinueDefaultTraversal;
    }
}

file sealed class ItemsViewInitialTailPositioner : IDisposable
{
    private const double FollowThreshold = 60;
    private const double BottomEpsilon = 1;
    private const int TailSettleTickMs = 16;
    private const int TailSettleMaxTicks = 32;
    private const int TailSettleStableTicks = 2;

    private readonly WinUIItemsView itemsView;
    private readonly HashSet<int> _pendingScrollOperations = [];
    private string? _requestKey;
    private int _tailIndex;
    private int _version;
    private bool _valid;
    private bool _awaitingLayout;
    private WinUIScrollView? _awaitingScrollView;
    private WinUIScrollView? _scrollView;
    private DispatcherTimer? _tailSettleTimer;
    private int _tailSettleVersion;
    private int _tailSettleTicks;
    private int _tailSettleStableTicks;
    private bool _following;
    private bool _bringingTail;
    private bool _disposed;

    public ItemsViewInitialTailPositioner(WinUIItemsView itemsView)
    {
        this.itemsView = itemsView;
        itemsView.Loaded += OnLoaded;
        itemsView.Unloaded += OnUnloaded;
    }

    public void Request(int tailIndex, string requestKey)
    {
        if (_disposed || string.Equals(_requestKey, requestKey, StringComparison.Ordinal))
            return;
        _requestKey = requestKey;
        _version++;
        DetachLayout();
        StopTailSettle();
        _valid = tailIndex >= 0;
        if (!_valid) return;
        _tailIndex = tailIndex;
        _following = true;
        if (itemsView.IsLoaded) AwaitLayout();
    }

    public void UpdateTailIndex(int tailIndex)
    {
        var changed = _tailIndex != tailIndex;
        _tailIndex = tailIndex;
        if (changed && _following && tailIndex >= 0 && itemsView.IsLoaded)
            QueueTailRequest(tailIndex, _version);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_valid) AwaitLayout();
    }

    private void AwaitLayout()
    {
        if (_disposed || !_valid || !itemsView.IsLoaded || _awaitingLayout) return;
        if (itemsView.ScrollView is { IsLoaded: false } scrollView)
        {
            _awaitingScrollView = scrollView;
            scrollView.Loaded += OnScrollViewLoaded;
            return;
        }
        _awaitingLayout = true;
        itemsView.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnScrollViewLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is WinUIScrollView scrollView) scrollView.Loaded -= OnScrollViewLoaded;
        _awaitingScrollView = null;
        AwaitLayout();
    }

    private void OnLayoutUpdated(object? sender, object args)
    {
        DetachLayout();
        if (itemsView.ScrollView is not { IsLoaded: true })
        {
            AwaitLayout();
            return;
        }
        var version = _version;
        var index = _tailIndex;
        itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_valid || !itemsView.IsLoaded || version != _version
                || itemsView.ScrollView is not { IsLoaded: true })
            {
                if (!_disposed && _valid) AwaitLayout();
                return;
            }
            AttachScrollView();
            StartTailRequest(index, version);
        });
    }

    private void AttachScrollView()
    {
        var nextScrollView = itemsView.ScrollView;
        if (!ReferenceEquals(_scrollView, nextScrollView))
        {
            DetachScrollView();
            _scrollView = nextScrollView;
            if (_scrollView is not null)
            {
                _scrollView.ViewChanged += OnViewChanged;
                _scrollView.ScrollCompleted += OnScrollCompleted;
            }
        }

    }

    private void OnViewChanged(WinUIScrollView sender, object args)
    {
        if (_bringingTail)
            return;

        if (_pendingScrollOperations.Count > 0
            && sender.State != WinUIScrollingInteractionState.Interaction)
        {
            return;
        }

        _following = IsNearBottom(sender);
    }

    private void OnScrollCompleted(
        WinUIScrollView sender,
        WinUIScrollingScrollCompletedEventArgs args)
    {
        _pendingScrollOperations.Remove(args.CorrelationId);
        if (!_bringingTail)
            _following = IsNearBottom(sender);
    }

    private void QueueTailRequest(int index, int version)
    {
        if (!itemsView.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_valid || !itemsView.IsLoaded || version != _version
                || !_following || index != _tailIndex)
            {
                return;
            }

            AttachScrollView();
            StartTailRequest(index, version);
        }))
        {
            _following = false;
        }
    }

    private void StartTailRequest(int index, int version)
    {
        if (_scrollView is not { IsLoaded: true })
            return;

        _following = true;
        _bringingTail = true;
        itemsView.StartBringItemIntoView(index, new BringIntoViewOptions
        {
            AnimationDesired = false,
            VerticalAlignmentRatio = 1.0,
        });

        _tailSettleVersion = version;
        _tailSettleTicks = 0;
        _tailSettleStableTicks = 0;
        _tailSettleTimer ??= CreateTailSettleTimer();
        _tailSettleTimer.Stop();
        _tailSettleTimer.Start();
    }

    private DispatcherTimer CreateTailSettleTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(TailSettleTickMs),
        };
        timer.Tick += OnTailSettleTick;
        return timer;
    }

    private void OnTailSettleTick(object? sender, object args)
    {
        if (_disposed || !_valid || !itemsView.IsLoaded
            || _tailSettleVersion != _version
            || _scrollView is not { IsLoaded: true } scrollView)
        {
            StopTailSettle();
            return;
        }

        if (scrollView.State == WinUIScrollingInteractionState.Interaction)
        {
            _following = IsNearBottom(scrollView);
            StopTailSettle();
            return;
        }

        _tailSettleTicks++;
        if (_pendingScrollOperations.Count == 0)
            ScrollToBottomIfNeeded(scrollView);

        if (_pendingScrollOperations.Count == 0
            && scrollView.ScrollableHeight - scrollView.VerticalOffset <= BottomEpsilon)
        {
            _tailSettleStableTicks++;
        }
        else
        {
            _tailSettleStableTicks = 0;
        }

        if (_tailSettleStableTicks >= TailSettleStableTicks
            || _tailSettleTicks >= TailSettleMaxTicks)
        {
            _following = IsNearBottom(scrollView);
            StopTailSettle();
        }
    }

    private void ScrollToBottomIfNeeded(WinUIScrollView scrollView)
    {
        if (scrollView.ScrollableHeight - scrollView.VerticalOffset <= BottomEpsilon)
            return;

        var correlationId = scrollView.ScrollTo(
            scrollView.HorizontalOffset,
            scrollView.ScrollableHeight,
            new WinUIScrollingScrollOptions(
                WinUIScrollingAnimationMode.Disabled,
                WinUIScrollingSnapPointsMode.Ignore));
        if (correlationId >= 0)
            _pendingScrollOperations.Add(correlationId);
    }

    private static bool IsNearBottom(WinUIScrollView scrollView) =>
        scrollView.ScrollableHeight - scrollView.VerticalOffset <= FollowThreshold;

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _version++;
        DetachLayout();
        StopTailSettle();
        DetachScrollView();
    }

    private void DetachLayout()
    {
        if (_awaitingScrollView is { } scrollView)
        {
            scrollView.Loaded -= OnScrollViewLoaded;
            _awaitingScrollView = null;
        }
        if (_awaitingLayout)
        {
            itemsView.LayoutUpdated -= OnLayoutUpdated;
            _awaitingLayout = false;
        }
    }

    private void StopTailSettle()
    {
        _tailSettleTimer?.Stop();
        _bringingTail = false;
    }

    private void DetachScrollView()
    {
        if (_scrollView is not null)
        {
            _scrollView.ViewChanged -= OnViewChanged;
            _scrollView.ScrollCompleted -= OnScrollCompleted;
        }
        _scrollView = null;
        _pendingScrollOperations.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _version++;
        DetachLayout();
        StopTailSettle();
        if (_tailSettleTimer is not null)
        {
            _tailSettleTimer.Tick -= OnTailSettleTick;
            _tailSettleTimer = null;
        }
        DetachScrollView();
        itemsView.Loaded -= OnLoaded;
        itemsView.Unloaded -= OnUnloaded;
    }
}

internal static class ItemsViewInitialTailExtensions
{
    public static Element PositionInitialTail<T>(
        this ItemsViewElement<T> itemsView,
        int initialTailIndex,
        string initialTailRequestKey) =>
        new ItemsViewInitialTailElement(
            itemsView,
            initialTailIndex,
            initialTailRequestKey);
}
