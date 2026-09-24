using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using GameInput;
using UiNavigation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace GameMode
{
    public class WindowManager : IDisposable
    {
        private const string BaseSortingLayer = "Base UI";
        private const int BaseSortingOrder = 1000;
        private const int SortingOrderStep = 10;

        private sealed class UiStackEntry
        {
            public IUiWindow Window;
            public UiStackOptions Options;
            public bool VisualVisible;
            public CancellationTokenSource LifetimeCts;
            public CancellationTokenSource TransitionCts;
            public UniTaskCompletionSource TransitionDone;
        }

        private sealed class QueuedOp
        {
            public Func<CancellationToken, UniTask> Run;
            public UniTaskCompletionSource Completion;
            public CancellationTokenSource Cts;
            public CancellationToken Token;
        }

        private readonly UiScreenRegistry _screenRegistry;
        private readonly GameEventBus _gameEventBus;
        private readonly GameInputController _gameInputController;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly Queue<QueuedOp> _queue = new Queue<QueuedOp>();
        private readonly List<UiStackEntry> _stack = new List<UiStackEntry>();
        private readonly List<IUiWindowInputMapProvider> _activeInputProviders =
            new List<IUiWindowInputMapProvider>();

        private QueuedOp _activeOperation;
        private bool _queueRunning;

        public WindowManager(
            UiScreenRegistry screenRegistry,
            GameEventBus gameEventBus,
            GameInputController gameInputController
        )
        {
            _screenRegistry =
                screenRegistry ?? throw new ArgumentNullException(nameof(screenRegistry));
            _gameEventBus = gameEventBus ?? throw new ArgumentNullException(nameof(gameEventBus));
            _gameInputController =
                gameInputController ?? throw new ArgumentNullException(nameof(gameInputController));
        }

        public UniTask Push(UiStackRequest request, CancellationToken ct = default) =>
            Enqueue(operationToken => PushImpl(request, operationToken), ct);

        public UniTask Pop(UiHideOptions options, CancellationToken ct = default) =>
            Enqueue(operationToken => PopImpl(options, operationToken), ct);

        public UniTask Replace(
            UiWindowId oldId,
            UiStackRequest request,
            UiHideOptions hideOptions = default,
            CancellationToken ct = default
        ) => Enqueue(operationToken => ReplaceImpl(oldId, request, ResolveHideOptions(hideOptions), operationToken), ct);

        public UniTask ReplaceTop(
            UiStackRequest request,
            UiHideOptions hideOptions = default,
            CancellationToken ct = default
        ) => Enqueue(operationToken => ReplaceTopImpl(request, ResolveHideOptions(hideOptions), operationToken), ct);

        public UniTask PopAndReplace(
            UiWindowId oldId,
            UiStackRequest request,
            UiHideOptions hideOptions = default,
            CancellationToken ct = default
        ) =>
            Enqueue(
                operationToken => PopAndReplaceImpl(oldId, request, ResolveHideOptions(hideOptions), operationToken),
                ct
            );

        public UniTask HideAsync(
            UiWindowId windowId,
            UiHideOptions options,
            CancellationToken ct = default
        ) => Enqueue(operationToken => HideImpl(windowId, options, operationToken), ct);

        public UniTask HideAsync(
            IUiWindow window,
            UiHideOptions options,
            CancellationToken ct = default
        ) => Enqueue(operationToken => HideImpl(window, options, operationToken), ct);

        public UniTask Clear(UiHideOptions options, CancellationToken ct = default) =>
            Enqueue(operationToken => ClearImpl(options, operationToken), ct);

        public void RefreshInputMaps()
        {
            ApplyInputProviders(CollectInputProviders(out var stopsLowerSources));
            PublishInputMaps(stopsLowerSources);
        }

        public void Dispose()
        {
            if (_lifetimeCts.IsCancellationRequested)
                return;

            _lifetimeCts.Cancel();

            while (_queue.Count > 0)
            {
                var item = _queue.Dequeue();
                item.Completion.TrySetCanceled(item.Token);
                item.Cts.Dispose();
            }

            ClearInputProviders();

            foreach (var entry in _stack)
                entry.LifetimeCts?.Dispose();

            _stack.Clear();
            _lifetimeCts.Dispose();
        }

        private UniTask Enqueue(Func<CancellationToken, UniTask> operation, CancellationToken callerToken)
        {
            if (_lifetimeCts.IsCancellationRequested)
                return UniTask.FromCanceled();

            var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetimeCts.Token);
            var completion = new UniTaskCompletionSource();
            _queue.Enqueue(
                new QueuedOp
                {
                    Run = operation,
                    Completion = completion,
                    Cts = cts,
                    Token = cts.Token,
                }
            );

            if (!_queueRunning)
            {
                _queueRunning = true;
                RunQueueAsync().Forget();
            }

            return completion.Task;
        }

        private async UniTask RunQueueAsync()
        {
            while (_queue.Count > 0)
            {
                var item = _queue.Dequeue();
                _activeOperation = item;

                try
                {
                    if (item.Token.IsCancellationRequested)
                    {
                        item.Completion.TrySetCanceled(item.Token);
                        continue;
                    }

                    await item.Run(item.Token);
                    item.Completion.TrySetResult();
                }
                catch (OperationCanceledException)
                {
                    item.Completion.TrySetCanceled(item.Token);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    item.Completion.TrySetException(ex);
                }
                finally
                {
                    item.Cts?.Dispose();
                    _activeOperation = null;
                }
            }

            _queueRunning = false;
        }

        private async UniTask PushImpl(UiStackRequest request, CancellationToken ct)
        {
            var window = _screenRegistry.Get(request.WindowId);
            var existing = FindEntry(window);
            if (existing != null)
            {
                var previousCompletion = existing.TransitionDone;
                CancelTransition(existing);

                if (previousCompletion != null)
                {
                    try
                    {
                        await previousCompletion.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        // The previous transition was canceled to prepare the window again.
                    }
                }

                ct.ThrowIfCancellationRequested();
            }

            await window.PrepareShowAsync(request.Payload, ct);
            ct.ThrowIfCancellationRequested();

            if (existing != null)
            {
                _stack.Remove(existing);
                existing.LifetimeCts?.Dispose();
            }

            var previousTop = _stack.Count > 0 ? _stack[_stack.Count - 1].Window : null;
            var entry = new UiStackEntry
            {
                Window = window,
                Options = request.Options,
                VisualVisible = true,
                LifetimeCts = _activeOperation.Cts,
            };
            _stack.Add(entry);
            _activeOperation.Cts = null;
            previousTop?.OnObscuredBy(window.WindowId);

            RefreshVisuals();
            RefreshInputMaps();
            await RunShow(entry, request.ShowOptions, ct);
        }

        private UniTask PopImpl(UiHideOptions options, CancellationToken ct)
        {
            return _stack.Count == 0
                ? UniTask.CompletedTask
                : HideEntry(_stack[_stack.Count - 1], options, ct);
        }

        private async UniTask ReplaceImpl(
            UiWindowId oldId,
            UiStackRequest request,
            UiHideOptions hideOptions,
            CancellationToken ct
        )
        {
            var index = FindEntryIndex(oldId);
            if (index < 0)
            {
                await PushImpl(request, ct);
                return;
            }

            var oldEntry = _stack[index];
            var newEntry = new UiStackEntry
            {
                Window = _screenRegistry.Get(request.WindowId),
                Options = request.Options,
                VisualVisible = true,
            };

            var inputLocked = true;
            LockInput();
            try
            {
                await RunHide(oldEntry, hideOptions, ct);
                await oldEntry.Window.CompleteHideAsync(ct);
                ct.ThrowIfCancellationRequested();
                _stack.RemoveAt(index);
                oldEntry.LifetimeCts?.Dispose();

                UnlockInput();
                inputLocked = false;

                await newEntry.Window.PrepareShowAsync(request.Payload, ct);
                ct.ThrowIfCancellationRequested();
                newEntry.LifetimeCts = _activeOperation.Cts;
                _stack.Insert(index, newEntry);
                _activeOperation.Cts = null;
                RefreshVisuals();
                RefreshInputMaps();
                await RunShow(newEntry, request.ShowOptions, ct);
            }
            finally
            {
                if (inputLocked)
                    UnlockInput();

                RefreshInputMaps();
            }
        }

        private async UniTask ReplaceTopImpl(
            UiStackRequest request,
            UiHideOptions hideOptions,
            CancellationToken ct
        )
        {
            if (_stack.Count == 0)
            {
                await PushImpl(request, ct);
                return;
            }

            await ReplaceImpl(_stack[_stack.Count - 1].Window.WindowId, request, hideOptions, ct);
        }

        private async UniTask PopAndReplaceImpl(
            UiWindowId oldId,
            UiStackRequest request,
            UiHideOptions hideOptions,
            CancellationToken ct
        )
        {
            var index = FindEntryIndex(oldId);
            if (index >= 0)
                await HideEntry(_stack[index], hideOptions, ct);

            await ReplaceTopImpl(request, hideOptions, ct);
        }

        private UniTask HideImpl(UiWindowId windowId, UiHideOptions options, CancellationToken ct)
        {
            var entry = FindEntry(windowId);
            return entry == null ? UniTask.CompletedTask : HideEntry(entry, options, ct);
        }

        private UniTask HideImpl(IUiWindow window, UiHideOptions options, CancellationToken ct)
        {
            var entry = FindEntry(window);
            return entry == null ? UniTask.CompletedTask : HideEntry(entry, options, ct);
        }

        private async UniTask ClearImpl(UiHideOptions options, CancellationToken ct)
        {
            LockInput();
            try
            {
                ClearInputProviders();

                for (int i = _stack.Count - 1; i >= 0; i--)
                {
                    var entry = _stack[i];
                    _stack.RemoveAt(i);
                    try
                    {
                        await RunHide(entry, options, ct);
                        await entry.Window.CompleteHideAsync(ct);
                        ct.ThrowIfCancellationRequested();
                    }
                    finally
                    {
                        entry.LifetimeCts?.Dispose();
                    }
                }

                RefreshVisuals();
            }
            finally
            {
                UnlockInput();
                RefreshInputMaps();
            }
        }

        private async UniTask HideEntry(
            UiStackEntry entry,
            UiHideOptions options,
            CancellationToken ct
        )
        {
            LockInput();
            try
            {
                ClearInputProviders();
                await RunHide(entry, options, ct);
                await entry.Window.CompleteHideAsync(ct);
                ct.ThrowIfCancellationRequested();
                _stack.Remove(entry);
                entry.LifetimeCts?.Dispose();
                RefreshVisuals();
            }
            finally
            {
                UnlockInput();
                RefreshInputMaps();
            }
        }

        private async UniTask RunShow(
            UiStackEntry entry,
            UiShowOptions options,
            CancellationToken ct
        )
        {
            var transitionTask = RunTransitionAsync(
                entry,
                token => entry.Window.PlayShowAsync(options, token),
                ct
            );

            if (options.Await == UiAwaitMode.Wait)
            {
                await transitionTask;
                return;
            }

            transitionTask.Forget(LogTransitionError);
        }

        private async UniTask RunHide(
            UiStackEntry entry,
            UiHideOptions options,
            CancellationToken ct
        )
        {
            await RunTransitionAsync(
                entry,
                token =>
                    entry.Window.PlayHideAsync(
                        new UiHideOptions(options.Mode, UiAwaitMode.Wait),
                        token
                    ),
                ct
            );
        }

        private async UniTask RunTransitionAsync(
            UiStackEntry entry,
            Func<CancellationToken, UniTask> operation,
            CancellationToken externalCt)
        {
            while (entry.TransitionDone != null)
            {
                var previousCts = entry.TransitionCts;
                var previousCompletion = entry.TransitionDone;

                previousCts?.Cancel();

                try
                {
                    await previousCompletion.Task;
                }
                catch (OperationCanceledException)
                {
                    // The previous transition was canceled before starting the new one.
                }

                // Completion can resume this method before the old transition clears its fields.
                if (ReferenceEquals(entry.TransitionDone, previousCompletion))
                    break;
            }

            externalCt.ThrowIfCancellationRequested();

            var transitionCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _lifetimeCts.Token);
            var transitionCompletion = new UniTaskCompletionSource();

            entry.TransitionCts = transitionCts;
            entry.TransitionDone = transitionCompletion;

            try
            {
                await operation(transitionCts.Token);
                transitionCompletion.TrySetResult();
            }
            catch (OperationCanceledException) when (transitionCts.IsCancellationRequested)
            {
                transitionCompletion.TrySetCanceled(transitionCts.Token);
            }
            catch (Exception ex)
            {
                transitionCompletion.TrySetException(ex);
            }
            finally
            {
                if (ReferenceEquals(entry.TransitionCts, transitionCts))
                    entry.TransitionCts = null;
                if (ReferenceEquals(entry.TransitionDone, transitionCompletion))
                    entry.TransitionDone = null;

                transitionCts.Dispose();
            }

            await transitionCompletion.Task;
        }

        private List<IUiWindowInputMapProvider> CollectInputProviders(out bool stopsLowerSources)
        {
            stopsLowerSources = false;
            var providers = new List<IUiWindowInputMapProvider>();

            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                var entry = _stack[i];

                if (
                    entry.Window is IUiWindowInputMapProvider provider
                    && HasActiveInputMaps(provider)
                )
                {
                    providers.Add(provider);
                }

                if (!entry.Options.BlocksInputBelow)
                    continue;

                stopsLowerSources = true;
                break;
            }

            providers.Reverse();
            return providers;
        }

        private void ApplyInputProviders(IReadOnlyList<IUiWindowInputMapProvider> providers)
        {
            for (int i = _activeInputProviders.Count - 1; i >= 0; i--)
            {
                var provider = _activeInputProviders[i];
                if (ContainsProvider(providers, provider))
                    continue;

                provider.UnbindInputActions();
                _activeInputProviders.RemoveAt(i);
            }

            for (int i = 0; i < providers.Count; i++)
            {
                var provider = providers[i];
                if (ContainsProvider(_activeInputProviders, provider))
                    continue;

                provider.BindInputActions();
                _activeInputProviders.Add(provider);
            }
        }

        private void PublishInputMaps(bool stopsLowerSources)
        {
            var maps = new List<InputActionMap>();
            for (int i = 0; i < _activeInputProviders.Count; i++)
            {
                var inputMaps = _activeInputProviders[i].GetActiveInputMaps();
                if (inputMaps == null)
                    continue;

                for (int j = 0; j < inputMaps.Count; j++)
                {
                    var map = inputMaps[j];
                    if (map != null && !maps.Contains(map))
                        maps.Add(map);
                }
            }

            _gameEventBus.Publish(
                new InputMapSourceChangedEvent(InputMapSource.ModeUi, maps, stopsLowerSources)
            );
        }

        private void RefreshVisuals()
        {
            var hideLower = false;
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                var entry = _stack[i];
                var visible = !hideLower;
                if (entry.VisualVisible != visible)
                {
                    entry.VisualVisible = visible;
                    if (!visible)
                        CancelTransition(entry);
                    entry.Window.SetVisualVisible(visible);
                }

                entry.Window.SortingOrder.Value = new UiSortingOrder(
                    BaseSortingLayer,
                    BaseSortingOrder + i * SortingOrderStep
                );

                if (entry.Options.HidesVisualBelow)
                    hideLower = true;
            }
        }

        private void ClearInputProviders()
        {
            for (int i = _activeInputProviders.Count - 1; i >= 0; i--)
                _activeInputProviders[i].UnbindInputActions();

            _activeInputProviders.Clear();

            _gameEventBus.Publish(
                new InputMapSourceChangedEvent(
                    InputMapSource.ModeUi,
                    Array.Empty<InputActionMap>(),
                    false
                )
            );
        }

        private static void CancelTransition(UiStackEntry entry)
        {
            if (entry == null)
                return;

            entry.TransitionCts?.Cancel();
        }

        private static void LogTransitionError(Exception ex)
        {
            if (ex is OperationCanceledException)
                return;

            Debug.LogException(ex);
        }

        private void LockInput()
        {
            _gameInputController.LockInput();
        }

        private void UnlockInput()
        {
            _gameInputController.UnlockInput();
        }

        private UiStackEntry FindEntry(UiWindowId id)
        {
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                if (_stack[i].Window.WindowId == id)
                    return _stack[i];
            }

            return null;
        }

        private UiStackEntry FindEntry(IUiWindow window)
        {
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_stack[i].Window, window))
                    return _stack[i];
            }

            return null;
        }

        private int FindEntryIndex(UiWindowId id)
        {
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                if (_stack[i].Window.WindowId == id)
                    return i;
            }

            return -1;
        }

        private static bool ContainsProvider(
            IReadOnlyList<IUiWindowInputMapProvider> providers,
            IUiWindowInputMapProvider provider
        )
        {
            for (int i = 0; i < providers.Count; i++)
            {
                if (ReferenceEquals(providers[i], provider))
                    return true;
            }

            return false;
        }

        private static bool HasActiveInputMaps(IUiWindowInputMapProvider provider)
        {
            var inputMaps = provider.GetActiveInputMaps();
            if (inputMaps == null)
                return false;

            for (int i = 0; i < inputMaps.Count; i++)
            {
                if (inputMaps[i] != null)
                    return true;
            }

            return false;
        }

        private static UiHideOptions ResolveHideOptions(UiHideOptions options)
        {
            return Equals(options, default(UiHideOptions)) ? UiHideOptions.AnimatedWait : options;
        }

    }
}
