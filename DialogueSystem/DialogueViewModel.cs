using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using PixelCrushers.DialogueSystem;
using R3;
using UiNavigation;

namespace DialogueSystem
{
    public class DialogueViewModel
    {
        private readonly DialogueModel _model;
        private readonly IGameplayVisualEffects _gameplayVisualEffects;
        private readonly DialoguePresentationResolver _presentationResolver = new();

        private IDialoguePresentation _presentation;
        private CancellationTokenSource _presentationCts;

        private string _lastSpeakerName;
        private string _lastText;

        public event Action<LineInputSignal> LineInput;
        public event Action<AnswerSelectedSignal> AnswerSelected;

        public ReactiveProperty<DialogueViewState> ViewState { get; } = new();

        public ReactiveProperty<UiSortingOrder> SortingOrder { get; } = new();

        public ReactiveProperty<bool> IsAutoPlayEnabled { get; } = new();

        public ReactiveProperty<bool> IsVisualVisible { get; } = new(true);

        private IDialoguePresentation Presentation =>
            _presentation ?? throw new InvalidOperationException("Dialogue presentation is not bound.");

        public DialogueViewModel(DialogueModel model, IGameplayVisualEffects gameplayVisualEffects)
        {
            _model = model;
            _gameplayVisualEffects = gameplayVisualEffects;
            ViewState.Value = new HiddenDialogueViewState();
        }

        public void BindPresentation(IDialoguePresentation presentation)
        {
            if (presentation == null)
                throw new ArgumentNullException(nameof(presentation));
            if (ReferenceEquals(_presentation, presentation))
                return;
            if (_presentation != null)
                throw new InvalidOperationException("Dialogue already has a presentation.");

            _presentationCts = new CancellationTokenSource();
            _presentation = presentation;
        }

        public void UnbindPresentation(IDialoguePresentation presentation)
        {
            if (!ReferenceEquals(_presentation, presentation))
                return;

            var cts = _presentationCts;
            _presentation = null;
            _presentationCts = null;
            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            finally
            {
                cts.Dispose();
            }
        }

        public void SetVisualVisible(bool visible)
        {
            IsVisualVisible.Value = visible;
            _gameplayVisualEffects.SetDialogueDesaturationRequested(visible && ViewState.Value.UseDesaturation);
        }

        public void Reset()
        {
            _presentationResolver.Reset();
            _lastSpeakerName = null;
            _lastText = null;
            IsAutoPlayEnabled.Value = false;
            _model.ReleaseInlinePrefab();
            SetState(new HiddenDialogueViewState());
        }

        public async UniTask ShowLine(
            DialogueDatabase database,
            Conversation conversation,
            DialogueEntry entry,
            bool suppressTypingOnce,
            CancellationToken ct
        )
        {
            var presentation = _presentationResolver.Resolve(database, conversation, entry);
            var state = await CreateLineState(database, entry, presentation, suppressTypingOnce, ct);
            ct.ThrowIfCancellationRequested();
            SetState(state);
        }

        public async UniTask ShowAnswers(
            DialogueDatabase database,
            Conversation conversation,
            DialogueEntry entry,
            CancellationToken ct
        )
        {
            var presentation = _presentationResolver.Resolve(database, conversation, entry);
            var state = await CreateAnswersState(database, entry, presentation, ct);
            ct.ThrowIfCancellationRequested();
            SetState(state);
        }

        public void Hide()
        {
            _model.ReleaseInlinePrefab();
            SetState(new HiddenDialogueViewState());
        }

        public async UniTask PlayTypingAsync(CancellationToken ct)
        {
            var presentation = Presentation;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _presentationCts.Token);
            await presentation.PlayTypingAsync(cts.Token);
        }

        public void ShowLineInstant()
        {
            Presentation.ShowLineInstant();
        }

        public void PushLineInput(LineInputSignal signal)
        {
            LineInput?.Invoke(signal);
        }

        public void PushAnswerInput(AnswerSelectedSignal signal)
        {
            AnswerSelected?.Invoke(signal);
        }

        private static DialogueEntry GetDestinationEntry(
            DialogueDatabase database,
            DialogueEntry from,
            int linkIndex
        )
        {
            if (from?.outgoingLinks == null)
                return null;
            if (linkIndex < 0 || linkIndex >= from.outgoingLinks.Count)
                return null;
            if (database == null)
                return null;
            var link = from.outgoingLinks[linkIndex];
            return database.GetDialogueEntry(
                link.destinationConversationID,
                link.destinationDialogueID
            );
        }

        private void SetState(DialogueViewState state)
        {
            ViewState.Value = state ?? new HiddenDialogueViewState();
            _gameplayVisualEffects.SetDialogueDesaturationRequested(
                IsVisualVisible.Value && ViewState.Value.UseDesaturation
            );
        }

        private async UniTask<DialogueViewState> CreateLineState(
            DialogueDatabase database,
            DialogueEntry entry,
            DialogueLinePresentation presentation,
            bool suppressTypingOnce,
            CancellationToken ct
        )
        {
            if (presentation == null)
                return new HiddenDialogueViewState();
            ReadLine(database, entry, out var speakerName, out var text);
            bool isSimple =
                presentation.UseSimplePresentation
                || !string.IsNullOrEmpty(presentation.InlinePrefabKey);
            if (isSimple)
            {
                return new SimpleDialogueViewState
                {
                    SpeakerName = speakerName,
                    Text = text,
                    InlinePrefab = await _model.LoadInlinePrefabAsync(
                        presentation.InlinePrefabKey,
                        ct
                    ),
                };
            }
            _model.ReleaseInlinePrefab();
            return new FullDialogueViewState
            {
                SpeakerName = speakerName,
                Text = text,
                LastSpeakerName = _lastSpeakerName,
                LastText = _lastText,
                SpeakerDisplayMode = presentation.SpeakerDisplayMode,
                UseGradientAndFilter = presentation.UseGradientAndFilter,
                UseDesaturation = presentation.UseDesaturation,
                IsSpeakerOnRight = presentation.IsSpeakerOnRight,
                UseTypingEffect = !suppressTypingOnce,
                LeftCharacterPrefab = await _model.GetDialogueCharacterAsync(
                    presentation.LeftCharacterKey,
                    ct
                ),
                RightCharacterPrefab = await _model.GetDialogueCharacterAsync(
                    presentation.RightCharacterKey,
                    ct
                ),
                CenterCharacterPrefab = await _model.GetDialogueCharacterAsync(
                    presentation.CenterCharacterKey,
                    ct
                ),
                LeftAnimationName = presentation.LeftAnimationName,
                RightAnimationName = presentation.RightAnimationName,
                CenterAnimationName = presentation.CenterAnimationName,
                SpeakerAnimationName = presentation.SpeakerAnimationName,
            };
        }

        private async UniTask<DialogueViewState> CreateAnswersState(
            DialogueDatabase database,
            DialogueEntry entry,
            DialogueLinePresentation presentation,
            CancellationToken ct
        )
        {
            if (presentation == null)
                return new HiddenDialogueViewState();
            _model.ReleaseInlinePrefab();
            var state = new AnswersDialogueViewState
            {
                SpeakerDisplayMode = presentation.SpeakerDisplayMode,
                UseGradientAndFilter = presentation.UseGradientAndFilter,
                UseDesaturation = presentation.UseDesaturation,
                IsSpeakerOnRight = presentation.IsSpeakerOnRight,
                QuestionTargetCharacterPrefab = await _model.GetDialogueCharacterAsync(
                    presentation.QuestionTargetCharacterKey,
                    ct
                ),
            };
            int count = entry?.outgoingLinks?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                var answerEntry = GetDestinationEntry(database, entry, i);
                var answerText = answerEntry?.currentMenuText;
                if (string.IsNullOrEmpty(answerText))
                    answerText = answerEntry?.currentDialogueText;
                state.Answers.Add(
                    new AnswerViewState
                    {
                        Text = answerText,
                        IsVisible = !string.IsNullOrEmpty(answerText),
                    }
                );
            }
            return state;
        }

        private void ReadLine(
            DialogueDatabase database,
            DialogueEntry entry,
            out string speakerName,
            out string text
        )
        {
            var actor = database != null && entry != null ? database.GetActor(entry.ActorID) : null;
            speakerName = actor != null ? actor.LookupLocalizedValue("Display Name") : string.Empty;
            if (string.IsNullOrEmpty(speakerName))
                speakerName = actor?.Name ?? string.Empty;
            text = entry?.currentDialogueText;
            if (string.IsNullOrEmpty(text))
                text = entry?.DialogueText;
            if (!string.IsNullOrEmpty(speakerName))
                _lastSpeakerName = speakerName;
            if (!string.IsNullOrEmpty(text))
                _lastText = text;
        }
    }
}
