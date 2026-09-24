using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using R3;

namespace UiNavigation
{
    public interface IUiWindow
    {
        UiWindowId WindowId { get; }
        ReactiveProperty<UiSortingOrder> SortingOrder { get; }
        event Action Changed;
        UniTask PrepareShowAsync(object payload, CancellationToken ct);
        UniTask PlayShowAsync(UiShowOptions options, CancellationToken ct);
        UniTask PlayHideAsync(UiHideOptions options, CancellationToken ct);
        UniTask CompleteHideAsync(CancellationToken ct);
        void SetVisualVisible(bool visible);
        void OnObscuredBy(UiWindowId windowId);
    }
}
