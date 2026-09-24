namespace UiNavigation
{
    public enum UiAwaitMode { Wait, DontWait }
    public enum UiHideMode { Animated, Instant }

    public readonly struct UiSortingOrder
    {
        public readonly string LayerName;
        public readonly int Order;

        public UiSortingOrder(string layerName, int order)
        {
            LayerName = layerName;
            Order = order;
        }
    }

    public readonly struct UiShowOptions
    {
        public readonly UiAwaitMode Await;

        public UiShowOptions(UiAwaitMode awaitMode)
        {
            Await = awaitMode;
        }

        public static UiShowOptions Wait => new UiShowOptions(UiAwaitMode.Wait);
        public static UiShowOptions DontWait => new UiShowOptions(UiAwaitMode.DontWait);
    }

    public readonly struct UiHideOptions
    {
        public readonly UiHideMode Mode;
        public readonly UiAwaitMode Await;

        public UiHideOptions(UiHideMode mode, UiAwaitMode awaitMode)
        {
            Mode = mode;
            Await = awaitMode;
        }

        public static UiHideOptions AnimatedWait => new UiHideOptions(UiHideMode.Animated, UiAwaitMode.Wait);
        public static UiHideOptions AnimatedDontWait => new UiHideOptions(UiHideMode.Animated, UiAwaitMode.DontWait);
        public static UiHideOptions InstantDontWait => new UiHideOptions(UiHideMode.Instant, UiAwaitMode.DontWait);
        public static UiHideOptions InstantWait => new UiHideOptions(UiHideMode.Instant, UiAwaitMode.Wait);
    }

    public readonly struct UiStackOptions
    {
        public readonly bool BlocksInputBelow;
        public readonly bool HidesVisualBelow;

        public UiStackOptions(bool blocksInputBelow, bool hidesVisualBelow)
        {
            BlocksInputBelow = blocksInputBelow;
            HidesVisualBelow = hidesVisualBelow;
        }

        public static UiStackOptions PassThrough => new UiStackOptions(false, false);
        public static UiStackOptions Modal => new UiStackOptions(true, false);
        public static UiStackOptions Fullscreen => new UiStackOptions(true, true);
        public static UiStackOptions VisualOnly => new UiStackOptions(false, false);
    }

    public readonly struct UiStackRequest
    {
        public UiWindowId WindowId { get; }
        public object Payload { get; }
        public UiStackOptions Options { get; }
        public UiShowOptions ShowOptions { get; }

        public UiStackRequest(
            UiWindowId windowId,
            object payload = null,
            UiStackOptions options = default,
            UiShowOptions? showOptions = null)
        {
            WindowId = windowId;
            Payload = payload;
            Options = options;
            ShowOptions = showOptions ?? UiShowOptions.DontWait;
        }
    }
}
