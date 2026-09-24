using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using GameAudio;
using GameInput;
using GameMode;
using InventorySystem;
using PixelCrushers.DialogueSystem;
using R3;
using UiNavigation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DialogueSystem
{
    public enum DialogueSpeakerDisplayMode
    {
        ActiveSpeakerOnly,
        BothSpeakers,
    }

    public class AnswerSelectedSignal
    {
        public int AnswerIndex;

        public AnswerSelectedSignal(int answerIndex) => AnswerIndex = answerIndex;
    }

    public enum LineInputSignalType
    {
        Advance,
        Back,
        Skip,
        ToggleAutoPlay,
    }

    public class LineInputSignal
    {
        public LineInputSignalType Type;

        public LineInputSignal(LineInputSignalType type) => Type = type;
    }

    public class DialogueController : IUiWindowInputMapProvider, IDisposable
    {
        private const float ADVANCE_COOLDOWN_SEC = 0.15f;
        private const string AdvanceActionName = "Advance";

        private readonly DialogueModel _model;
        private readonly DialogueViewModel _viewModel;
        private readonly DialogueLocaleSync _dialogueLocaleSync;
        private readonly GameEventBus _gameEventBus;
        private readonly WindowManager _windowManager;
        private readonly GameInputController _gameInputController;
        private readonly AudioManagerBase _audioManager;

        private readonly ReactiveProperty<DialogueEntry> _currentEntry;
        private readonly IDisposable _currentEntrySubscription;
        private readonly Stack<DialogueEntry> _dialogueHistory = new();
        private readonly List<IUiPageInputMapProvider> _pageInputMapProviders = new(0);
        private readonly List<InputActionMap> _activeInputMaps = new(1);

        private IDialogueFlow _flow = NullDialogueFlow.Instance;
        private DialogueDatabase _currentDialogueDB;
        private Conversation _currentConversation;
        private string _currentSpeakerGUID;
        private string _currentEntryGUID;
        private State _state;
        private bool _suppressTypingOnce;

        private UniTaskCompletionSource _dialogueCompletion;
        private CancellationTokenSource _dialogueCts;
        private UniTask _playbackTask = UniTask.CompletedTask;
        private bool _completedNaturally;
        private bool _isClosing;
        private Exception _dialogueError;

        private UniTaskCompletionSource<LineInputSignal> _lineSignalTcs;
        private UniTaskCompletionSource<AnswerSelectedSignal> _answerSignalTcs;
        private InputAction _advanceAction;
        private float _nextAdvanceAllowedTime;

        public event Action Changed;

        private enum State
        {
            EnterNode,
            ShowLine,
            ShowChoice,
            End,
        }

        private enum NodeKind
        {
            End,
            Line,
            Auto,
            Choice,
        }

        public InputActionMap InputMap => _gameInputController.GetActionMap(InputActionMapType.Dialogue);

        public bool IsOpen { get; private set; }

        public string DialogueDatabaseKey { get; private set; }

        public ReactiveProperty<bool> IsAutoPlayEnabled => _viewModel.IsAutoPlayEnabled;

        public DialogueViewModel ViewModel => _viewModel;

        public UiWindowId WindowId => UiWindowId.Dialogue;

        public ReactiveProperty<UiSortingOrder> SortingOrder => _viewModel.SortingOrder;

        public IReadOnlyList<IUiPageInputMapProvider> PageInputMapProviders => _pageInputMapProviders;

        public DialogueController(
            DialogueModel model,
            DialogueLocaleSync dialogueLocaleSync,
            GameEventBus gameEventBus,
            WindowManager windowManager,
            GameInputController gameInputController,
            AudioManagerBase audioManager,
            IGameplayVisualEffects gameplayVisualEffects
        )
        {
            _model = model;
            _dialogueLocaleSync = dialogueLocaleSync;
            _gameEventBus = gameEventBus;
            _windowManager = windowManager;
            _gameInputController = gameInputController;
            _audioManager = audioManager;
            _viewModel = new DialogueViewModel(model, gameplayVisualEffects);
            _viewModel.LineInput += PushLineSignal;
            _viewModel.AnswerSelected += PushAnswerSignal;

            _currentEntry = new ReactiveProperty<DialogueEntry>();
            _currentEntrySubscription = _currentEntry.Subscribe(OnDialogueEntryChanged);

            _dialogueLocaleSync.LocaleApplied += OnDialogueLocaleApplied;
        }

        public async UniTask PlayAsync(
            Story_System.DialogueRequest dialogue,
            CancellationToken cancellationToken
        )
        {
            if (
                string.IsNullOrWhiteSpace(dialogue.DialogueDatabaseKey)
                || string.IsNullOrWhiteSpace(dialogue.ConversationTitle)
            )
            {
                return;
            }

            var completion = new UniTaskCompletionSource();
            var payload = DialogueAwaitPayload.Create(
                completion,
                dialogue.DialogueDatabaseKey,
                dialogue.ConversationTitle
            );

            await _windowManager.Push(
                new UiStackRequest(
                    UiWindowId.Dialogue,
                    payload: payload,
                    options: dialogue.HideLowerUi
                        ? UiStackOptions.Fullscreen
                        : UiStackOptions.Modal,
                    showOptions: UiShowOptions.DontWait
                ),
                cancellationToken
            );

            await completion.Task;
        }

        public void Dispose()
        {
            _viewModel.LineInput -= PushLineSignal;
            _viewModel.AnswerSelected -= PushAnswerSignal;
            UnbindInputActions();
            _currentEntrySubscription?.Dispose();
            _dialogueLocaleSync.LocaleApplied -= OnDialogueLocaleApplied;

            StopAndCompleteAsync().Forget(Debug.LogException);
        }

        public async UniTask PrepareShowAsync(object payload, CancellationToken ct)
        {
            if (_dialogueCompletion != null)
                await StopAndCompleteAsync();

            _dialogueCompletion = (payload as DialogueAwaitPayload)?.Completion;
            _dialogueCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _completedNaturally = false;
            _isClosing = false;
            _dialogueError = null;
            _playbackTask = UniTask.CompletedTask;

            var dialogueToken = _dialogueCts.Token;
            try
            {
                var preparation = await TryApplyPayloadAsync(payload, dialogueToken);
                dialogueToken.ThrowIfCancellationRequested();
                if (!preparation.Success)
                    throw new InvalidOperationException("Failed to prepare the dialogue conversation.");

                StartDialogue(preparation.StartConversation);
            }
            catch
            {
                IsOpen = false;
                _dialogueCompletion = null;
                _dialogueCts?.Dispose();
                _dialogueCts = null;
                ClearCurrentDialogue();
                throw;
            }
        }

        public UniTask PlayShowAsync(UiShowOptions options, CancellationToken ct)
        {
            _playbackTask = StartDialogueLoopAsync(_dialogueCts.Token).ToAsyncLazy().Task;
            WaitForDialogueEndAsync().Forget(Debug.LogException);
            return UniTask.CompletedTask;
        }

        public async UniTask PlayHideAsync(UiHideOptions options, CancellationToken ct)
        {
            _isClosing = true;

            try
            {
                await StopPlaybackAsync();
                _viewModel.Hide();
            }
            catch (OperationCanceledException)
            {
                _dialogueCompletion?.TrySetCanceled();
                throw;
            }
            catch (Exception ex)
            {
                _dialogueCompletion?.TrySetException(ex);
                throw;
            }
        }

        public UniTask CompleteHideAsync(CancellationToken ct)
        {
            var completion = _dialogueCompletion;
            var error = _dialogueError;
            var completedNaturally = _completedNaturally;
            var conversation = _currentConversation;
            var databaseKey = DialogueDatabaseKey;
            _dialogueCompletion = null;
            _completedNaturally = false;
            _dialogueError = null;

            try
            {
                ClearCurrentDialogue();
                if (error == null && completedNaturally)
                {
                    ApplyConversationCompletionEffects(conversation);
                    _gameEventBus.Publish(new ConversationCompletedEvent(databaseKey, conversation?.Title));
                }
            }
            catch (Exception ex)
            {
                error ??= ex;
            }

            _dialogueCts?.Dispose();
            _dialogueCts = null;
            _playbackTask = UniTask.CompletedTask;

            if (error != null)
            {
                if (completion != null)
                    completion.TrySetException(error);
                else
                    Debug.LogException(error);
            }
            else if (completedNaturally)
            {
                completion?.TrySetResult();
            }
            else
            {
                completion?.TrySetCanceled();
            }

            IsOpen = false;
            return UniTask.CompletedTask;
        }

        public void SetVisualVisible(bool visible) => _viewModel.SetVisualVisible(visible);

        public void OnObscuredBy(UiWindowId windowId) { }

        public IReadOnlyList<InputActionMap> GetActiveInputMaps()
        {
            _activeInputMaps.Clear();
            _activeInputMaps.Add(InputMap);
            return _activeInputMaps;
        }

        public void BindInputActions()
        {
            _advanceAction = InputMap?.FindAction(AdvanceActionName, false);
            if (_advanceAction != null)
                _advanceAction.performed += OnDialogueAdvance;
        }

        public void UnbindInputActions()
        {
            if (_advanceAction != null)
                _advanceAction.performed -= OnDialogueAdvance;
            _advanceAction = null;
        }

        // ---------------- Input from View ----------------

        public void PushLineSignal(LineInputSignal signal)
        {
            if (signal == null || !IsOpen)
                return;

            if (_lineSignalTcs == null)
                return;

            if (signal.Type == LineInputSignalType.Advance)
            {
                float now = Time.unscaledTime;
                if (now < _nextAdvanceAllowedTime)
                    return;

                _nextAdvanceAllowedTime = now + ADVANCE_COOLDOWN_SEC;
                _audioManager.PlayButtonClick();
            }

            _lineSignalTcs.TrySetResult(signal);
        }

        public void PushAnswerSignal(AnswerSelectedSignal signal)
        {
            if (signal == null || !IsOpen)
                return;

            if (_answerSignalTcs == null)
                return;

            _answerSignalTcs.TrySetResult(signal);
        }

        public void OnDialogueAdvance(InputAction.CallbackContext ctx)
        {
            if (!ctx.performed)
                return;
            PushLineSignal(new LineInputSignal(LineInputSignalType.Advance));
        }

        private static NodeKind GetNodeKind(DialogueEntry entry)
        {
            if (entry == null)
                return NodeKind.End;

            bool hasText = !string.IsNullOrEmpty(entry.currentDialogueText);
            int links = entry.outgoingLinks?.Count ?? 0;

            if (hasText)
                return NodeKind.Line;
            if (links == 0)
                return NodeKind.End;
            return (links == 1) ? NodeKind.Auto : NodeKind.Choice;
        }

        private static async UniTask CancelTypingAsync(CancellationTokenSource typingCts, UniTask typingTask)
        {
            typingCts.Cancel();
            try
            {
                await typingTask;
            }
            catch (OperationCanceledException) when (typingCts.IsCancellationRequested)
            {
                // The owner observes the expected cancellation before changing the displayed line.
            }
        }

        private static string TryGetGuidField(Actor actor)
        {
            if (actor == null || actor.fields == null)
                return null;

            var f = actor.fields.FirstOrDefault(x =>
                x != null && string.Equals(x.title, "Guid", StringComparison.OrdinalIgnoreCase)
            );

            return f != null ? f.value : null;
        }

        private static string TryGetGuidField(DialogueEntry entry)
        {
            if (entry == null || entry.fields == null)
                return null;

            var f = entry.fields.FirstOrDefault(x =>
                x != null && string.Equals(x.title, "Guid", StringComparison.OrdinalIgnoreCase)
            );

            return f != null ? f.value : null;
        }

        private async UniTask<(bool Success, Conversation StartConversation)> TryApplyPayloadAsync(
            object payload,
            CancellationToken ct
        )
        {
            _flow = NullDialogueFlow.Instance;
            _currentDialogueDB = null;
            _suppressTypingOnce = false;
            DialogueDatabaseKey = null;

            string startTitle;

            if (payload is DialogueAwaitPayload ap)
            {
                _flow = ap.DialogueFlow ?? NullDialogueFlow.Instance;
                DialogueDatabaseKey = ap.DialogueDatabaseKey;
                startTitle = ap.ConversationTitle;
            }
            else
            {
                return (false, null);
            }

            _currentDialogueDB = await _model.LoadDialogueDatabaseByKeyAsync(
                DialogueDatabaseKey,
                ct
            );

            if (_currentDialogueDB == null)
                return (false, null);
            if (string.IsNullOrWhiteSpace(DialogueDatabaseKey))
                return (false, null);
            if (string.IsNullOrWhiteSpace(startTitle))
                return (false, null);

            var startConversation = _currentDialogueDB.GetConversation(startTitle);
            if (startConversation == null)
                return (false, null);

            return (true, startConversation);
        }

        private void StartDialogue(Conversation startConversation)
        {
            IsOpen = true;

            _dialogueHistory.Clear();
            _currentConversation = null;
            _currentEntry.Value = startConversation.GetFirstDialogueEntry();
            _state = State.EnterNode;
        }

        private async UniTask WaitForDialogueEndAsync()
        {
            try
            {
                await _playbackTask;
                _completedNaturally = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _dialogueError = ex;
            }

            if (_isClosing)
                return;

            try
            {
                await _windowManager.HideAsync(this, UiHideOptions.AnimatedWait);
            }
            catch (OperationCanceledException)
            {
                _dialogueCompletion?.TrySetCanceled();
            }
            catch (Exception ex)
            {
                _dialogueCompletion?.TrySetException(ex);
            }
        }

        private async UniTask StopPlaybackAsync()
        {
            _dialogueCts?.Cancel();
            await _playbackTask;
        }

        private async UniTask StopAndCompleteAsync()
        {
            await StopPlaybackAsync();
            await CompleteHideAsync(CancellationToken.None);
        }

        private void ClearCurrentDialogue()
        {
            _currentDialogueDB = null;
            DialogueDatabaseKey = null;

            _currentConversation = null;
            _currentEntry.Value = null;

            _currentEntryGUID = null;
            _currentSpeakerGUID = null;

            _dialogueHistory.Clear();

            IsAutoPlayEnabled.Value = false;

            _flow = NullDialogueFlow.Instance;

            _lineSignalTcs?.TrySetResult(null);
            _answerSignalTcs?.TrySetResult(null);
            _lineSignalTcs = null;
            _answerSignalTcs = null;
            _suppressTypingOnce = false;

            _nextAdvanceAllowedTime = 0f;
            _viewModel.Reset();
        }

        private void OnDialogueLocaleApplied()
        {
            if (!IsOpen || _dialogueCts == null)
                return;

            RefreshLocalizedContentAsync(_dialogueCts.Token).Forget(ex =>
            {
                if (ex is not OperationCanceledException)
                    Debug.LogException(ex);
            });
        }

        private async UniTask RefreshLocalizedContentAsync(CancellationToken ct)
        {
            if (!IsOpen)
                return;

            var entry = _currentEntry.Value;
            if (entry == null)
                return;

            if (_state == State.ShowChoice)
                await ShowAnswers(entry, ct);
            else
                await ShowLine(entry, ct);
        }

        // ---------------- FSM loop ----------------
        private async UniTask StartDialogueLoopAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                switch (_state)
                {
                    case State.EnterNode:
                        EnterNode();
                        break;
                    case State.ShowLine:
                        await ShowLineStateAsync(ct);
                        break;
                    case State.ShowChoice:
                        await ShowChoiceStateAsync(ct);
                        break;
                    case State.End:
                        return;
                    default:
                        throw new InvalidOperationException($"Unknown dialogue state: {_state}.");
                }
            }
        }

        private void EnterNode()
        {
            int guard = 0;
            while (_currentEntry.Value != null && guard++ < 1000)
            {
                var e = _currentEntry.Value;
                var kind = GetNodeKind(e);

                switch (kind)
                {
                    case NodeKind.End:
                        _state = State.End;
                        return;

                    case NodeKind.Auto:
                        FollowLink(0, pushHistory: false);
                        continue;

                    case NodeKind.Choice:
                        _dialogueHistory.Clear();
                        _state = State.ShowChoice;
                        return;

                    case NodeKind.Line:
                        _state = State.ShowLine;
                        return;
                }
            }

            Debug.LogError("Dialogue: EnterNode possible infinite loop");
            _state = State.End;
        }

        private async UniTask ShowLineStateAsync(CancellationToken ct)
        {
            var entry = _currentEntry.Value;
            if (entry == null)
            {
                _state = State.End;
                return;
            }

            await DisplayLineAsync(entry, ct);

            if (_state == State.ShowLine)
                _state = State.EnterNode;
        }

        private async UniTask ShowChoiceStateAsync(CancellationToken ct)
        {
            var entry = _currentEntry.Value;
            if (entry == null)
            {
                _state = State.End;
                return;
            }

            await ShowAnswers(entry, ct);

            var signal = await WaitForAnswerAsync(ct);
            if (signal != null)
                GoToAnswer(signal.AnswerIndex);

            _state = State.EnterNode;
        }

        private async UniTask DisplayLineAsync(DialogueEntry entry, CancellationToken ct)
        {
            using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            await ShowLine(entry, ct);

            bool canType =
                _viewModel.ViewState.Value is FullDialogueViewState { UseTypingEffect: true };

            if (!canType)
            {
                _viewModel.ShowLineInstant();
                TryApplyOneTimeEffects(entry);

                var sig = await WaitForLineSignalAsync(ct);
                HandleLineSignal(sig);
                return;
            }

            var typingTask = _viewModel.PlayTypingAsync(typingCts.Token).ToAsyncLazy().Task;

            using var inputCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await HandleTypingAndInputAsync(entry, typingTask, typingCts, inputCts.Token);
            }
            finally
            {
                inputCts.Cancel();
                await CancelTypingAsync(typingCts, typingTask);
            }
        }

        private async UniTask HandleTypingAndInputAsync(
            DialogueEntry entry,
            UniTask typingTask,
            CancellationTokenSource typingCts,
            CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsOpen)
            {
                // WhenAny and the input branch may both await this signal before it completes.
                var signalTask = WaitForLineSignalAsync(ct).ToAsyncLazy().Task;
                int completed = await UniTask.WhenAny(typingTask, signalTask);
                ct.ThrowIfCancellationRequested();

                if (completed == 0)
                {
                    TryApplyOneTimeEffects(entry);

                    if (signalTask.Status == UniTaskStatus.Succeeded)
                    {
                        var sig = signalTask.GetAwaiter().GetResult();
                        HandleLineSignal(sig);
                        return;
                    }

                    if (
                        _viewModel.ViewState.Value is FullDialogueViewState
                        && IsAutoPlayEnabled.Value
                    )
                    {
                        GoNextAfterLine();
                        return;
                    }

                    var next = await signalTask;
                    HandleLineSignal(next);
                    return;
                }

                var s = signalTask.GetAwaiter().GetResult();
                if (s == null)
                    continue;

                switch (s.Type)
                {
                    case LineInputSignalType.Advance:
                        if (IsAutoPlayEnabled.Value)
                            continue;

                        await CancelTypingAsync(typingCts, typingTask);
                        ct.ThrowIfCancellationRequested();
                        _viewModel.ShowLineInstant();
                        TryApplyOneTimeEffects(entry);

                        var next = await WaitForLineSignalAsync(ct);
                        HandleLineSignal(next);
                        return;

                    case LineInputSignalType.Back:
                        await CancelTypingAsync(typingCts, typingTask);
                        ct.ThrowIfCancellationRequested();
                        IsAutoPlayEnabled.Value = false;
                        _suppressTypingOnce = true;
                        GoBack();
                        return;

                    case LineInputSignalType.Skip:
                        await CancelTypingAsync(typingCts, typingTask);
                        ct.ThrowIfCancellationRequested();
                        IsAutoPlayEnabled.Value = false;

                        TryApplyOneTimeEffects(entry);
                        SkipToNearestChoiceOrEnd();
                        return;

                    case LineInputSignalType.ToggleAutoPlay:
                        IsAutoPlayEnabled.Value = !IsAutoPlayEnabled.Value;
                        break;
                }
            }

            ct.ThrowIfCancellationRequested();
        }

        private async UniTask ShowLine(DialogueEntry entry, CancellationToken ct)
        {
            _flow.OnDialogueEntryRead(entry);
            await _viewModel.ShowLine(
                _currentDialogueDB,
                _currentConversation,
                entry,
                _suppressTypingOnce,
                ct
            );

            if (_viewModel.ViewState.Value is SimpleDialogueViewState)
                IsAutoPlayEnabled.Value = false;

            _suppressTypingOnce = false;
        }

        private void HandleLineSignal(LineInputSignal signal)
        {
            if (signal == null)
                return;

            switch (signal.Type)
            {
                case LineInputSignalType.Advance:
                    GoNextAfterLine();
                    break;

                case LineInputSignalType.Back:
                    _suppressTypingOnce = true;
                    GoBack();
                    break;

                case LineInputSignalType.Skip:
                    SkipToNearestChoiceOrEnd();
                    break;

                case LineInputSignalType.ToggleAutoPlay:
                    if (_viewModel.ViewState.Value is not FullDialogueViewState)
                    {
                        IsAutoPlayEnabled.Value = false;
                        return;
                    }

                    IsAutoPlayEnabled.Value = !IsAutoPlayEnabled.Value;
                    if (IsAutoPlayEnabled.Value)
                        GoNextAfterLine();
                    break;
            }
        }

        private void GoNextAfterLine()
        {
            var entry = _currentEntry.Value;
            if (entry == null)
            {
                _state = State.End;
                return;
            }

            int links = entry.outgoingLinks?.Count ?? 0;
            if (links == 0)
            {
                _state = State.End;
                return;
            }

            if (links == 1)
            {
                _dialogueHistory.Push(entry);
                FollowLink(0, pushHistory: false);
                return;
            }

            _state = State.ShowChoice;
        }

        private async UniTask ShowAnswers(DialogueEntry choiceEntry, CancellationToken ct)
        {
            _flow.OnDialogueEntryRead(choiceEntry);
            await _viewModel.ShowAnswers(_currentDialogueDB, _currentConversation, choiceEntry, ct);
        }

        private DialogueEntry GetDestinationEntry(DialogueEntry from, int linkIndex)
        {
            if (from?.outgoingLinks == null)
                return null;
            if (linkIndex < 0 || linkIndex >= from.outgoingLinks.Count)
                return null;
            if (_currentDialogueDB == null)
                return null;

            var link = from.outgoingLinks[linkIndex];
            return _currentDialogueDB.GetDialogueEntry(
                link.destinationConversationID,
                link.destinationDialogueID
            );
        }

        private void GoBack()
        {
            if (_dialogueHistory.Count < 1)
                return;

            _currentEntry.Value = _dialogueHistory.Pop();
            _state = State.EnterNode;
        }

        private void GoToAnswer(int answerIndex)
        {
            var choiceEntry = _currentEntry.Value;
            int count = choiceEntry?.outgoingLinks?.Count ?? 0;
            if (answerIndex < 0 || answerIndex >= count)
                return;

            FollowLink(answerIndex, pushHistory: false);
            _state = State.EnterNode;
        }

        private void FollowLink(int linkIndex, bool pushHistory)
        {
            var entry = _currentEntry.Value;
            if (entry?.outgoingLinks == null)
                return;
            if (linkIndex < 0 || linkIndex >= entry.outgoingLinks.Count)
                return;
            if (_currentDialogueDB == null)
                return;

            if (pushHistory)
                _dialogueHistory.Push(entry);

            var link = entry.outgoingLinks[linkIndex];
            _currentEntry.Value = _currentDialogueDB.GetDialogueEntry(
                link.destinationConversationID,
                link.destinationDialogueID
            );
        }

        private void SkipToNearestChoiceOrEnd()
        {
            int guard = 0;

            while (_currentEntry.Value != null && guard++ < 1000)
            {
                var e = _currentEntry.Value;
                int links = e.outgoingLinks?.Count ?? 0;

                _flow?.OnDialogueEntryRead(e);
                TryApplyOneTimeEffects(e);

                if (links == 0)
                {
                    _state = State.End;
                    return;
                }

                if (links > 1)
                {
                    _dialogueHistory.Clear();
                    _state = State.ShowChoice;
                    return;
                }

                FollowLink(0, pushHistory: false);
            }

            _state = State.End;
        }

        private UniTask<LineInputSignal> WaitForLineSignalAsync(CancellationToken ct)
        {
            _lineSignalTcs = new UniTaskCompletionSource<LineInputSignal>();
            return _lineSignalTcs.Task.AttachExternalCancellation(ct);
        }

        private UniTask<AnswerSelectedSignal> WaitForAnswerAsync(CancellationToken ct)
        {
            _answerSignalTcs = new UniTaskCompletionSource<AnswerSelectedSignal>();
            return _answerSignalTcs.Task.AttachExternalCancellation(ct);
        }

        private void OnDialogueEntryChanged(DialogueEntry entry)
        {
            if (entry == null || _currentDialogueDB == null)
                return;

            _currentEntryGUID = TryGetGuidField(entry);

            var actor = _currentDialogueDB.GetActor(entry.ActorID);
            _currentSpeakerGUID = TryGetGuidField(actor);

            var conv = _currentDialogueDB.GetConversation(entry.conversationID);
            var convTitle = conv != null ? conv.Title : null;

            if (_currentConversation == null || convTitle != _currentConversation.Title)
                SwitchConversation(convTitle);
        }

        private void SwitchConversation(string conversationTitle)
        {
            _currentConversation =
                _currentDialogueDB != null
                    ? _currentDialogueDB.GetConversation(conversationTitle)
                    : null;

            if (_currentConversation == null)
            {
                _state = State.End;
                return;
            }

            _flow.OnConversationChanged(_currentConversation.Title);

            var e = _currentEntry.Value;
            int links = e?.outgoingLinks?.Count ?? 0;
            if (e != null && e.id == 0 && string.IsNullOrEmpty(e.currentDialogueText) && links == 1)
                FollowLink(0, pushHistory: false);
        }

        private void TryApplyOneTimeEffects(DialogueEntry entry)
        {
            if (entry.fields == null)
                return;

            bool firstTimeSeen = !_model.HasSeenLine(DialogueDatabaseKey, _currentEntryGUID);

            if (!firstTimeSeen)
                return;

            _model.MarkLineSeen(DialogueDatabaseKey, _currentEntryGUID);

            foreach (var field in entry.fields)
            {
                if (field == null)
                    continue;

                if (
                    DialogueFieldUtility.HasTitle(
                        field.title,
                        GameConstants.DIALOGUE_ADD_NEW_CLUE_PARAM
                    )
                )
                {
                    var clueKey = DialogueFieldUtility.NormalizeValue(field.value);
                    if (!string.IsNullOrEmpty(clueKey))
                        _gameEventBus.Publish(new RequestAddClue(clueKey));
                    continue;
                }

                if (
                    DialogueFieldUtility.HasTitle(
                        field.title,
                        GameConstants.DIALOGUE_ADD_TESTIMONY_PARAM
                    )
                )
                {
                    if (
                        DialogueFieldUtility.TryParseTestimony(
                            field.value,
                            out var suspectKey,
                            out var testimonyId
                        )
                    )
                    {
                        _gameEventBus.Publish(new RequestAddTestimony(suspectKey, testimonyId));
                    }

                    continue;
                }

                if (
                    DialogueFieldUtility.HasTitle(
                        field.title,
                        GameConstants.DIALOGUE_CHANGE_CHAR_DIALOGUE_PARAM
                    )
                )
                {
                    if (
                        DialogueFieldUtility.TryParseChangeCharacterDialogue(
                            field.value,
                            out var characterKey,
                            out var conversationTitle
                        )
                    )
                    {
                        _gameEventBus.Publish(
                            new RequestChangeCharacterDialogue(characterKey, conversationTitle)
                        );
                    }

                    continue;
                }

                if (
                    DialogueFieldUtility.HasTitle(
                        field.title,
                        GameConstants.DIALOGUE_DISCOVER_SCENE_CLUE_PARAM
                    )
                )
                {
                    var clueKey = DialogueFieldUtility.NormalizeValue(field.value);
                    if (!string.IsNullOrEmpty(clueKey))
                        _gameEventBus.Publish(new RequestDiscoverSceneClue(clueKey));
                    continue;
                }

                if (
                    DialogueFieldUtility.HasTitle(
                        field.title,
                        GameConstants.DIALOGUE_DOSSIER_FIELD_PARAM
                    )
                )
                {
                    if (
                        DialogueFieldUtility.TryParseDossierFieldType(
                            field.value,
                            out var suspectKey,
                            out var fieldType
                        )
                    )
                    {
                        if (string.IsNullOrEmpty(suspectKey))
                            continue;

                        _gameEventBus.Publish(
                            new RequestUpdateDossierField(
                                suspectKey,
                                fieldType,
                                FieldStatus.FromDialogue
                            )
                        );
                    }
                }
            }
        }

        private void ApplyConversationCompletionEffects(Conversation conversation)
        {
            if (conversation?.fields == null)
                return;

            foreach (var field in conversation.fields)
            {
                if (field == null)
                    continue;

                if (
                    !DialogueFieldUtility.HasTitle(
                        field.title,
                        GameConstants.DIALOGUE_UNLOCK_ACTION_MENU_QUESTION_PARAM
                    )
                )
                {
                    continue;
                }

                if (
                    !DialogueFieldUtility.TryParseUnlockActionMenuQuestion(
                        field.value,
                        out var characterKey,
                        out var questionType,
                        out var questionId
                    )
                )
                {
                    continue;
                }

                _gameEventBus.Publish(
                    new RequestUnlockCharacterActionMenuQuestion(
                        characterKey,
                        questionType,
                        questionId
                    )
                );
            }
        }
    }
}
