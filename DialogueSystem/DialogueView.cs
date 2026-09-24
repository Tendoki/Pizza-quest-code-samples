using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using DialogueSystem;
using GameAudio;
using R3;
using TMPro;
using UiNavigation;
using UnityEngine;
using UnityEngine.UI;
using Zenject;

public class DialogueView : MonoBehaviour, IUiSortingOrderApplier, IDialoguePresentation
{
    [Header("UI references")]
    [SerializeField] private Canvas canvas;

    [SerializeField] private RectTransform centralCharPlace;

    [SerializeField] private RectTransform leftCharacterPlace;

    [SerializeField] private RectTransform rightCharacterPlace;

    [SerializeField] private RectTransform leftCharacterNamePos;

    [SerializeField] private RectTransform rightCharacterNamePos;

    [Header("Text settings")]
    [SerializeField] private float textTypingSpeed = 0.01f;

    [SerializeField] private float autoPlayDelay = 2f;

    [Header("Full dialogue panel elements")]
    [SerializeField] private GameObject fullDialoguePanel;

    [SerializeField] private GameObject characterNameImg;

    [SerializeField] private TMP_Text characterNameTmp;

    [SerializeField] private TMP_Text dialogueLineTmp;

    [SerializeField] private Image arrowImg;

    [SerializeField] private Sprite deselectedArrowSprite;

    [SerializeField] private Sprite selectedArrowSprite;

    [SerializeField] private DialogueControlButton backButton;

    [SerializeField] private DialogueControlButton skipButton;

    [SerializeField] private AutoPlayToggle autoPlayToggle;

    [Header("Interrogation gradient")]
    [SerializeField] private Image interrogationGradient;

    [SerializeField] private Sprite mainCharGradient;

    [SerializeField] private Sprite speakerGradient;

    [Header("Simple dialogue panel elements")]
    [SerializeField] private GameObject simpleDialoguePanel;

    [SerializeField] private TMP_Text simpleLineTmp;

    [SerializeField] private Color simpleCharNameTextColor;

    [SerializeField] private Color simpleLineColor;

    [Header("Answers settings")]
    [SerializeField] private RectTransform answersPanel;

    [SerializeField] private List<AnswerButtonView> answerButtons;

    [SerializeField] private Vector2 answersHiddenPosition;

    [SerializeField] private Vector2 answersVisiblePosition;

    [SerializeField] private float showAnswersDuration = 0.35f;

    [Header("Last line")]
    [SerializeField] private TMP_Text lastDialogueLineTmp;

    [SerializeField] private Color lastLineCharNameColor;

    [Header("Inline prefab")]
    [SerializeField] private Transform inlinePrefabRoot;

    private DialogueViewModel _viewModel;
    private DialogueViewState _currentState = new HiddenDialogueViewState();

    private IDisposable _viewStateSubscription;
    private IDisposable _autoPlaySubscription;
    private IDisposable _sortingOrderSubscription;
    private IDisposable _visibilitySubscription;
    private CanvasGroup _visibilityGroup;

    private readonly Dictionary<int, DialogueCharacter> _characters = new();
    private DialogueCharacter _activeCharacter;
    private Sequence _answersSequence;
    private bool _answersLayoutPrepared;
    private GameObject _inlineInstance;

    private AudioManagerBase _audioManager;
    private CancellationTokenSource _typingCts;

    public void Bind(DialogueViewModel viewModel)
    {
        Unbind();
        Hide();
        if (viewModel == null)
            return;

        _visibilityGroup = canvas.GetComponent<CanvasGroup>();
        if (_visibilityGroup == null)
            _visibilityGroup = canvas.gameObject.AddComponent<CanvasGroup>();

        viewModel.BindPresentation(this);
        _viewModel = viewModel;
        SubscribeInternal();
    }

    public void ApplySortingOrder(UiSortingOrder sortingOrder)
    {
        if (canvas == null)
            return;
        canvas.overrideSorting = true;
        if (!string.IsNullOrEmpty(sortingOrder.LayerName))
            canvas.sortingLayerName = sortingOrder.LayerName;
        canvas.sortingOrder = sortingOrder.Order;
    }

    public void OnDialogueAreaClicked() =>
        _viewModel.PushLineInput(new LineInputSignal(LineInputSignalType.Advance));

    async UniTask IDialoguePresentation.PlayTypingAsync(CancellationToken ct)
    {
        CancelTyping();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, this.GetCancellationTokenOnDestroy());
        _typingCts = cts;
        try
        {
            await TypeTextAsync(cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_typingCts, cts))
                _typingCts = null;
        }
    }

    void IDialoguePresentation.ShowLineInstant()
    {
        ShowTextInstant();
    }

    private static string GetSinglePortraitAnimationName(FullDialogueViewState state)
    {
        if (!string.IsNullOrEmpty(state.CenterAnimationName))
            return state.CenterAnimationName;
        return state.SpeakerAnimationName;
    }

    [Inject]
    private void Construct(AudioManagerBase audioManager)
    {
        _audioManager = audioManager;
    }

    private void OnDestroy()
    {
        Unbind();
        KillAnswersSeq();
        DestroyInlinePrefab();
    }

    private void Unbind()
    {
        if (_viewModel == null)
            return;

        UnsubscribeInternal();
        var viewModel = _viewModel;
        _viewModel = null;
        try
        {
            CancelTyping();
        }
        finally
        {
            viewModel.UnbindPresentation(this);
        }
    }

    private void SubscribeInternal()
    {
        backButton.OnClicked += HandleBackButtonClicked;
        skipButton.OnClicked += HandleSkipButtonClicked;
        autoPlayToggle.Bind(_viewModel.IsAutoPlayEnabled, HandleAutoPlayToggleClicked);
        _viewStateSubscription = _viewModel.ViewState.Subscribe(ApplyState);
        _autoPlaySubscription = _viewModel.IsAutoPlayEnabled.Subscribe(ApplyAutoPlayVisual);
        _sortingOrderSubscription = _viewModel.SortingOrder.Subscribe(ApplySortingOrder);
        _visibilitySubscription = _viewModel.IsVisualVisible.Subscribe(ApplyVisualVisibility);
        for (int i = 0; i < answerButtons.Count; i++)
            answerButtons[i].Bind(HandleAnswerButtonClicked);
    }

    private void UnsubscribeInternal()
    {
        if (_viewModel == null)
            return;
        backButton.OnClicked -= HandleBackButtonClicked;
        skipButton.OnClicked -= HandleSkipButtonClicked;
        if (autoPlayToggle != null)
            autoPlayToggle.Bind(null, null);
        for (int i = 0; i < answerButtons.Count; i++)
        {
            if (answerButtons[i] != null)
                answerButtons[i].Bind(null);
        }
        _viewStateSubscription?.Dispose();
        _viewStateSubscription = null;
        _autoPlaySubscription?.Dispose();
        _autoPlaySubscription = null;
        _sortingOrderSubscription?.Dispose();
        _sortingOrderSubscription = null;
        _visibilitySubscription?.Dispose();
        _visibilitySubscription = null;
    }

    private void ApplyVisualVisibility(bool visible)
    {
        _visibilityGroup.alpha = visible ? 1f : 0f;
        _visibilityGroup.interactable = visible;
        _visibilityGroup.blocksRaycasts = visible;
    }

    private void ApplyState(DialogueViewState state)
    {
        _currentState = state ?? new HiddenDialogueViewState();
        if (_currentState is HiddenDialogueViewState)
        {
            Hide();
            return;
        }
        ShowCanvas();
        switch (_currentState)
        {
            case FullDialogueViewState fullState:
                ApplyFullState(fullState);
                break;
            case SimpleDialogueViewState simpleState:
                ApplySimpleState(simpleState);
                break;
            case AnswersDialogueViewState answersState:
                ApplyAnswersState(answersState);
                break;
        }
    }

    private void ShowCanvas()
    {
        if (canvas.gameObject.activeSelf)
            return;
        canvas.gameObject.SetActive(true);
        PrepareLayout();
        ApplySortingOrder(_viewModel.SortingOrder.Value);
    }

    private void Hide()
    {
        KillAnswersSeq();
        ResetAllCharacters();
        DestroyInlinePrefab();
        ResetPanelsToHiddenState();
        canvas.gameObject.SetActive(false);
        if (characterNameTmp != null)
            characterNameTmp.text = string.Empty;
        if (dialogueLineTmp != null)
            dialogueLineTmp.text = string.Empty;
        if (lastDialogueLineTmp != null)
            lastDialogueLineTmp.text = string.Empty;
        if (simpleLineTmp != null)
            simpleLineTmp.text = string.Empty;
    }

    private void PrepareLayout()
    {
        if (_answersLayoutPrepared)
            return;
        answersPanel.gameObject.SetActive(true);
        LayoutRebuilder.ForceRebuildLayoutImmediate(answersPanel);
        answersPanel.gameObject.SetActive(false);
        _answersLayoutPrepared = true;
    }

    private void ResetPanelsToHiddenState()
    {
        answersPanel.gameObject.SetActive(false);
        answersPanel.anchoredPosition = answersHiddenPosition;
        if (fullDialoguePanel != null)
            fullDialoguePanel.SetActive(false);
        if (simpleDialoguePanel != null)
            simpleDialoguePanel.SetActive(false);
        HideGradient();
        SetFullControlsVisible(false);
    }

    private void KillAnswersSeq()
    {
        _answersSequence?.Kill();
        _answersSequence = null;
    }

    private void ApplyFullState(FullDialogueViewState state)
    {
        HideAnswersPanelImmediate();
        HideSimpleDialoguePanel();
        DestroyInlinePrefab();
        characterNameTmp.text = state.SpeakerName ?? string.Empty;
        dialogueLineTmp.text = string.Empty;
        dialogueLineTmp.maxVisibleCharacters = int.MaxValue;
        UpdateLastDialogueLineText(state.LastSpeakerName, state.LastText);
        UpdateGradient(state.UseGradientAndFilter, state.IsSpeakerOnRight);
        ShowDialoguePortraits(state);
        SetSpeakerNameAndSideUI(
            state.SpeakerDisplayMode == DialogueSpeakerDisplayMode.BothSpeakers
                && state.IsSpeakerOnRight
        );
        SetFullControlsVisible(true);
        ShowFullDialoguePanel();
    }

    private void ApplySimpleState(SimpleDialogueViewState state)
    {
        HideAnswersPanelImmediate();
        HideFullDialoguePanel();
        HideGradient();
        HideCharacters();
        SetFullControlsVisible(false);
        ApplyInlinePrefab(state.InlinePrefab);
        ShowSimpleDialoguePanel();
        RefreshSimpleLine(state.SpeakerName, state.Text);
    }

    private void ApplyAnswersState(AnswersDialogueViewState state)
    {
        SetFullControlsVisible(false);
        HideFullDialoguePanel();
        HideSimpleDialoguePanel();
        DestroyInlinePrefab();
        UpdateGradient(state.UseGradientAndFilter, state.IsSpeakerOnRight);
        ShowQuestionPortrait(state);
        ApplyAnswers(state);
        ShowAnswersPanelAnimated();
    }

    private void ApplyAnswers(AnswersDialogueViewState state)
    {
        for (int i = 0; i < answerButtons.Count; i++)
        {
            var answerState = i < state.Answers.Count ? state.Answers[i] : null;
            answerButtons[i].Apply(answerState);
        }
    }

    private void ResetAllCharacters()
    {
        foreach (var character in _characters.Values)
        {
            if (character != null)
                Destroy(character.gameObject);
        }
        _characters.Clear();
        _activeCharacter = null;
    }

    private void HideCharacters()
    {
        foreach (var character in _characters.Values)
        {
            if (character != null)
                character.gameObject.SetActive(false);
        }
    }

    private DialogueCharacter GetCharacter(DialogueCharacter prefab)
    {
        if (prefab == null)
            return null;
        int key = prefab.GetInstanceID();
        if (_characters.TryGetValue(key, out var existing) && existing != null)
            return existing;
        var instance = Instantiate(prefab, rightCharacterPlace);
        instance.gameObject.SetActive(false);
        _characters[key] = instance;
        return instance;
    }

    private void ShowDialoguePortraits(FullDialogueViewState state)
    {
        HideCharacters();
        var leftCharacter = GetCharacter(state.LeftCharacterPrefab);
        var rightCharacter = GetCharacter(state.RightCharacterPrefab);
        bool showBoth = state.SpeakerDisplayMode == DialogueSpeakerDisplayMode.BothSpeakers;
        if (showBoth)
        {
            ShowSideCharacter(
                leftCharacter,
                leftCharacterPlace,
                !state.IsSpeakerOnRight,
                state.LeftAnimationName
            );
            ShowSideCharacter(
                rightCharacter,
                rightCharacterPlace,
                state.IsSpeakerOnRight,
                state.RightAnimationName
            );
            _activeCharacter = state.IsSpeakerOnRight ? rightCharacter : leftCharacter;
            return;
        }
        var active = GetCharacter(state.CenterCharacterPrefab);
        if (active == null)
            active = state.IsSpeakerOnRight ? rightCharacter : leftCharacter;
        _activeCharacter = active;
        if (active == null)
            return;
        active.transform.SetParent(centralCharPlace, false);
        active.gameObject.SetActive(true);
        SetImageBrightness(active.Image, 1f);
        PlayCharacterAnimation(active.Animator, GetSinglePortraitAnimationName(state));
    }

    private void ShowSideCharacter(
        DialogueCharacter character,
        RectTransform parent,
        bool isActive,
        string animationName
    )
    {
        if (character == null)
            return;
        character.transform.SetParent(parent, false);
        character.gameObject.SetActive(true);
        SetImageBrightness(character.Image, isActive ? 1f : 0.7f);
        PlayCharacterAnimation(character.Animator, animationName);
    }

    private void SetImageBrightness(Image image, float v)
    {
        if (image == null)
            return;
        Color color = image.color;
        Color.RGBToHSV(color, out float h, out float s, out float oldV);
        Color newColor = Color.HSVToRGB(h, s, v);
        newColor.a = color.a;
        image.color = newColor;
    }

    private void SetSpeakerNameAndSideUI(bool isRightSpeaker)
    {
        if (characterNameImg == null)
            return;
        var rt = characterNameImg.GetComponent<RectTransform>();
        rt.position = isRightSpeaker
            ? rightCharacterNamePos.position
            : leftCharacterNamePos.position;
    }

    private void ShowQuestionPortrait(AnswersDialogueViewState state)
    {
        HideCharacters();
        var portraitCharacter = GetCharacter(state.QuestionTargetCharacterPrefab);
        if (portraitCharacter == null)
        {
            _activeCharacter = null;
            return;
        }
        _activeCharacter = portraitCharacter;
        portraitCharacter.transform.SetParent(centralCharPlace, false);
        portraitCharacter.gameObject.SetActive(true);
        SetImageBrightness(portraitCharacter.Image, 1f);
        PlayCharacterAnimation(portraitCharacter.Animator, null);
    }

    private void UpdateGradient(bool useGradient, bool isSpeakerOnRight)
    {
        if (interrogationGradient == null)
            return;
        interrogationGradient.gameObject.SetActive(useGradient);
        if (!useGradient)
            return;
        interrogationGradient.sprite = isSpeakerOnRight ? speakerGradient : mainCharGradient;
    }

    private void SetFullControlsVisible(bool visible)
    {
        if (backButton != null)
            backButton.gameObject.SetActive(visible);
        if (skipButton != null)
            skipButton.gameObject.SetActive(visible);
        if (autoPlayToggle != null)
            autoPlayToggle.gameObject.SetActive(visible);
        if (arrowImg != null)
            arrowImg.gameObject.SetActive(visible);
        if (characterNameImg != null)
            characterNameImg.SetActive(visible);
        if (dialogueLineTmp != null)
            dialogueLineTmp.gameObject.SetActive(visible);
    }

    private void HideGradient()
    {
        if (interrogationGradient != null)
            interrogationGradient.gameObject.SetActive(false);
    }

    private void ApplyInlinePrefab(GameObject prefab)
    {
        DestroyInlinePrefab();
        if (prefab == null)
            return;
        _inlineInstance = Instantiate(prefab, inlinePrefabRoot);
    }

    private void DestroyInlinePrefab()
    {
        if (_inlineInstance != null)
            Destroy(_inlineInstance);
        _inlineInstance = null;
    }

    private void RefreshSimpleLine(string speaker, string line)
    {
        speaker ??= string.Empty;
        line ??= string.Empty;
        if (string.IsNullOrEmpty(speaker) && string.IsNullOrEmpty(line))
        {
            simpleLineTmp.text = string.Empty;
            return;
        }
        string speakerHex = ColorUtility.ToHtmlStringRGB(simpleCharNameTextColor);
        string lineHex = ColorUtility.ToHtmlStringRGB(simpleLineColor);
        string speakerPrefix = !string.IsNullOrEmpty(speaker) ? $"{speaker}: " : string.Empty;
        string formattedLine = FormatTextForTarget(simpleLineTmp, line, speakerPrefix);
        if (!string.IsNullOrEmpty(speaker))
            simpleLineTmp.text =
                $"<color=#{speakerHex}>{speaker}:</color> <color=#{lineHex}>{formattedLine}</color>";
        else
            simpleLineTmp.text = $"<color=#{lineHex}>{formattedLine}</color>";
    }

    private void UpdateLastDialogueLineText(string speaker, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            lastDialogueLineTmp.text = string.Empty;
            return;
        }
        string speakerHex = ColorUtility.ToHtmlStringRGB(lastLineCharNameColor);
        string speakerColored = $"<color=#{speakerHex}>{speaker}:</color>";
        string lineColored = $"<color=#FFFFFF>{text}</color>";
        lastDialogueLineTmp.text = $"{speakerColored} {lineColored}";
    }

    private void ApplyAutoPlayVisual(bool isEnabled)
    {
        arrowImg.sprite = isEnabled ? selectedArrowSprite : deselectedArrowSprite;
    }

    private void ShowFullDialoguePanel()
    {
        fullDialoguePanel.SetActive(true);
    }

    private void HideFullDialoguePanel()
    {
        fullDialoguePanel.SetActive(false);
    }

    private void ShowSimpleDialoguePanel()
    {
        simpleDialoguePanel.SetActive(true);
    }

    private void HideSimpleDialoguePanel()
    {
        simpleDialoguePanel.SetActive(false);
    }

    private void ShowTextInstant()
    {
        switch (_currentState)
        {
            case SimpleDialogueViewState simpleState:
                RefreshSimpleLine(simpleState.SpeakerName, simpleState.Text);
                break;
            case FullDialogueViewState fullState:
                dialogueLineTmp.text = string.IsNullOrEmpty(fullState.Text)
                    ? string.Empty
                    : FormatTextForTarget(dialogueLineTmp, fullState.Text);
                dialogueLineTmp.maxVisibleCharacters = int.MaxValue;
                break;
        }
    }

    private void CancelTyping()
    {
        var cts = _typingCts;
        if (cts == null)
            return;

        _typingCts = null;
        try
        {
            _audioManager.StopDialogueTypingLoop();
        }
        finally
        {
            cts.Cancel();
        }
    }

    private async UniTask TypeTextAsync(CancellationToken ct)
    {
        if (_currentState is not FullDialogueViewState fullState)
            return;

        string formatted = FormatTextForTarget(dialogueLineTmp, fullState.Text);
        if (fullState.UseTypingEffect && !string.IsNullOrEmpty(formatted))
        {
            var typingCts = _typingCts;
            try
            {
                dialogueLineTmp.text = formatted;
                dialogueLineTmp.maxVisibleCharacters = 0;
                dialogueLineTmp.ForceMeshUpdate();
                _audioManager.StartDialogueTypingLoop();
                int characterCount = dialogueLineTmp.textInfo.characterCount;
                for (int visibleCount = 1; visibleCount <= characterCount; visibleCount++)
                {
                    dialogueLineTmp.maxVisibleCharacters = visibleCount;
                    await UniTask.Delay(
                        TimeSpan.FromSeconds(textTypingSpeed),
                        cancellationToken: ct
                    );
                }
            }
            finally
            {
                // A canceled operation must not stop the sound of a newer presentation.
                if (ReferenceEquals(_typingCts, typingCts))
                    _audioManager.StopDialogueTypingLoop();
            }
        }
        else
        {
            dialogueLineTmp.text = formatted;
            dialogueLineTmp.maxVisibleCharacters = int.MaxValue;
        }
        if (_viewModel.IsAutoPlayEnabled.Value)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(autoPlayDelay), cancellationToken: ct);
            if (_activeCharacter != null)
            {
                await UniTask.WaitUntil(
                    () =>
                    {
                        var state = _activeCharacter.Animator.GetCurrentAnimatorStateInfo(0);
                        return state.normalizedTime >= 1f;
                    },
                    cancellationToken: ct
                );
            }
        }
    }

    private string FormatTextForTarget(
        TMP_Text targetText,
        string text,
        string firstLinePrefix = ""
    )
    {
        if (string.IsNullOrEmpty(text) || targetText == null)
            return text ?? string.Empty;
        string[] words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return string.Empty;
        float availableWidth = targetText.rectTransform.rect.width;
        if (availableWidth <= 0f)
            return text;
        var result = new StringBuilder(text.Length);
        string currentLine = firstLinePrefix ?? string.Empty;
        bool isFirstWord = true;
        foreach (string word in words)
        {
            string separator =
                isFirstWord || currentLine.EndsWith(" ", StringComparison.Ordinal)
                    ? string.Empty
                    : " ";
            string candidateLine = currentLine + separator + word;
            float candidateWidth = targetText.GetPreferredValues(candidateLine).x;
            if (!isFirstWord && candidateWidth > availableWidth)
            {
                result.Append('\n').Append(word);
                currentLine = word;
            }
            else
            {
                if (!isFirstWord && (result.Length == 0 || result[result.Length - 1] != '\n'))
                    result.Append(' ');
                result.Append(word);
                currentLine = candidateLine;
            }
            isFirstWord = false;
        }
        return result.ToString();
    }

    private void HandleBackButtonClicked() =>
        _viewModel.PushLineInput(new LineInputSignal(LineInputSignalType.Back));

    private void HandleSkipButtonClicked() =>
        _viewModel.PushLineInput(new LineInputSignal(LineInputSignalType.Skip));

    private void HandleAutoPlayToggleClicked() =>
        _viewModel.PushLineInput(new LineInputSignal(LineInputSignalType.ToggleAutoPlay));

    private void HandleAnswerButtonClicked(int answerIndex) =>
        _viewModel.PushAnswerInput(new AnswerSelectedSignal(answerIndex));

    private void ShowAnswersPanelAnimated()
    {
        answersPanel.anchoredPosition = answersHiddenPosition;
        answersPanel.gameObject.SetActive(true);
        _answersSequence?.Kill();
        _answersSequence = DOTween.Sequence();
        _answersSequence.Append(
            answersPanel
                .DOAnchorPos(answersVisiblePosition, showAnswersDuration)
                .SetEase(Ease.OutBack)
        );
        _answersSequence.Play();
    }

    private void HideAnswersPanelImmediate()
    {
        _answersSequence?.Kill();
        _answersSequence = null;
        answersPanel.anchoredPosition = answersHiddenPosition;
        answersPanel.gameObject.SetActive(false);
    }

    private void PlayCharacterAnimation(Animator charAnimator, string animationName)
    {
        if (charAnimator == null)
            return;
        if (string.IsNullOrEmpty(animationName))
            charAnimator.Play("Idle");
        else
            charAnimator.Play(animationName);
    }
}
