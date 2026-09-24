using System;
using System.Threading;
using CharacterActionMenu;
using Cysharp.Threading.Tasks;
using DialogueSystem;
using GameMode;
using InterrogationSystem;
using InventorySystem;
using Map;
using Pause_Menu;
using Quest_System;
using Scene;
using Story_System;
using Story_System.Visual_Graph;
using UiNavigation;
using UnityEngine;
using Zenject;

public class GameplayEntryPoint : IInitializable, IDisposable
{
    private readonly DiContainer _container;

    private readonly Camera _uiCamera;
    private readonly CharacterActionMenuView _characterActionMenuView;
    private readonly DialogueView _dialogueView;
    private readonly InventoryController _inventoryController;
    private readonly CluesPanelView _cluesPanelView;
    private readonly SuspectsPanelView _suspectsPanelView;
    private readonly InterrogationController _interrogationController;
    private readonly MapView _mapView;
    private readonly ItemTooltipView _itemTooltipView;
    private readonly RoomTitlePresenter _roomTitlePresenter;
    private readonly RoomHudView _roomHudView;
    private readonly ClueNotificationView _clueNotificationView;
    private readonly DossierNotificationView _dossierNotificationView;
    private readonly StoryBackdropPanelView _storyBackdropPanelView;
    private readonly PauseMenuView _pauseMenuView;

    private readonly GameplayCameraStackService _cameraStackService;
    private readonly GameplayUiSceneService _uiSceneService;
    private readonly GameplayController _gameplayController;
    private readonly GameplayAssetPreloadService _assetPreloadService;
    private readonly GoalManager _goalManager;
    private readonly StoryController _storyController;
    private readonly PlayerProgressProvider _playerProgressProvider;
    private readonly SaveLoadService _saveLoadService;
    private readonly GameDatabaseService _gameDatabaseService;
    private readonly GameEventBus _gameEventBus;
    private readonly DialogueLocaleSync _dialogueLocaleSync;
    private readonly UiScreenRegistry _uiScreenRegistry;
    private readonly WindowManager _windowManager;

    private readonly DialogueController _dialogueController;
    private readonly RoomCharacterInteractionController _roomCharacterInteractionController;
    private readonly CluesPanelController _cluesPanelController;
    private readonly SuspectsPanelController _suspectsPanelController;
    private readonly MapViewModel _mapViewModel;
    private readonly ItemTooltipController _itemTooltipController;
    private readonly RoomUiController _roomUiController;
    private readonly NotificationPresenter _notificationPresenter;
    private readonly StoryNodeActionFactory _storyNodeActionFactory;

    private StoryBackdropPanelViewModel _storyBackdropPanelViewModel;
    private PauseMenuViewModel _pauseMenuViewModel;
    private bool _booted;

    public GameplayEntryPoint(
        DiContainer container,
        Camera uiCamera,
        CharacterActionMenuView characterActionMenuView,
        DialogueView dialogueView,
        InventoryController inventoryController,
        CluesPanelView cluesPanelView,
        SuspectsPanelView suspectsPanelView,
        InterrogationController interrogationController,
        MapView mapView,
        ItemTooltipView itemTooltipView,
        RoomTitlePresenter roomTitlePresenter,
        RoomHudView roomHudView,
        ClueNotificationView clueNotificationView,
        DossierNotificationView dossierNotificationView,
        StoryBackdropPanelView storyBackdropPanelView,
        PauseMenuView pauseMenuView,
        GameplayCameraStackService cameraStackService,
        GameplayUiSceneService uiSceneService,
        GameplayController gameplayController,
        GameplayAssetPreloadService assetPreloadService,
        GoalManager goalManager,
        StoryController storyController,
        PlayerProgressProvider playerProgressProvider,
        SaveLoadService saveLoadService,
        GameDatabaseService gameDatabaseService,
        GameEventBus gameEventBus,
        DialogueLocaleSync dialogueLocaleSync,
        UiScreenRegistry uiScreenRegistry,
        WindowManager windowManager,
        DialogueController dialogueController,
        RoomCharacterInteractionController roomCharacterInteractionController,
        CluesPanelController cluesPanelController,
        SuspectsPanelController suspectsPanelController,
        MapViewModel mapViewModel,
        ItemTooltipController itemTooltipController,
        RoomUiController roomUiController,
        NotificationPresenter notificationPresenter,
        StoryNodeActionFactory storyNodeActionFactory)
    {
        _container = container;
        _uiCamera = uiCamera;
        _characterActionMenuView = characterActionMenuView;
        _dialogueView = dialogueView;
        _inventoryController = inventoryController;
        _cluesPanelView = cluesPanelView;
        _suspectsPanelView = suspectsPanelView;
        _interrogationController = interrogationController;
        _mapView = mapView;
        _itemTooltipView = itemTooltipView;
        _roomTitlePresenter = roomTitlePresenter;
        _roomHudView = roomHudView;
        _clueNotificationView = clueNotificationView;
        _dossierNotificationView = dossierNotificationView;
        _storyBackdropPanelView = storyBackdropPanelView;
        _pauseMenuView = pauseMenuView;
        _cameraStackService = cameraStackService;
        _uiSceneService = uiSceneService;
        _gameplayController = gameplayController;
        _assetPreloadService = assetPreloadService;
        _goalManager = goalManager;
        _storyController = storyController;
        _playerProgressProvider = playerProgressProvider;
        _saveLoadService = saveLoadService;
        _gameDatabaseService = gameDatabaseService;
        _gameEventBus = gameEventBus;
        _dialogueLocaleSync = dialogueLocaleSync;
        _uiScreenRegistry = uiScreenRegistry;
        _windowManager = windowManager;
        _dialogueController = dialogueController;
        _roomCharacterInteractionController = roomCharacterInteractionController;
        _cluesPanelController = cluesPanelController;
        _suspectsPanelController = suspectsPanelController;
        _mapViewModel = mapViewModel;
        _itemTooltipController = itemTooltipController;
        _roomUiController = roomUiController;
        _notificationPresenter = notificationPresenter;
        _storyNodeActionFactory = storyNodeActionFactory;
    }

    public void Initialize()
    {
        if (_booted)
            return;

        BootAsync().Forget();
    }

    public void Dispose()
    {
        _pauseMenuViewModel?.Dispose();
    }

    private async UniTask BootAsync()
    {
        await _assetPreloadService.PreloadAssetsAsync(CancellationToken.None);

        var storyNodeFactory = new StoryNodeFactory(
            _storyNodeActionFactory,
            _goalManager,
            _storyController.SequenceRunner,
            _dialogueController);
        var storyGraph = CreateStoryGraph(_gameDatabaseService.Database?.StoryGraph, storyNodeFactory);

        var progressData = _saveLoadService.LoadProgress();
        _playerProgressProvider.Restore(progressData, storyGraph, _saveLoadService.IsNewGameLaunch);

        _cameraStackService.SetUiCamera(_uiCamera);

        _characterActionMenuView.Bind(_roomCharacterInteractionController.MenuViewModel);
        _uiScreenRegistry.Register(_roomCharacterInteractionController.MenuViewModel);

        _dialogueView.Bind(_dialogueController.ViewModel);
        _uiScreenRegistry.Register(_dialogueController);

        _inventoryController.Init();
        _uiScreenRegistry.Register(_inventoryController);

        _cluesPanelView.Init();
        _uiScreenRegistry.Register(_cluesPanelController);

        _suspectsPanelView.Init();
        _uiScreenRegistry.Register(_suspectsPanelController);

        _interrogationController.Init();

        _mapView.Bind(_mapViewModel);
        _uiScreenRegistry.Register(_mapViewModel);

        _itemTooltipView.Bind(_itemTooltipController);
        _roomHudView.Bind(_roomUiController.Hud);
        _uiScreenRegistry.Register(_roomUiController);

        _clueNotificationView.Bind(_notificationPresenter);
        _dossierNotificationView.Bind(_notificationPresenter);

        _storyBackdropPanelViewModel = _container.Instantiate<StoryBackdropPanelViewModel>();
        _storyBackdropPanelView.Bind(_storyBackdropPanelViewModel);
        _uiScreenRegistry.Register(_storyBackdropPanelViewModel);

        _pauseMenuViewModel = _container.Instantiate<PauseMenuViewModel>();
        _pauseMenuView.Bind(_pauseMenuViewModel);
        _uiScreenRegistry.Register(_pauseMenuViewModel);

        _booted = true;
        Debug.Log("[GameplayBootstrap] Booted");
        _uiSceneService.MarkReady();
        _gameplayController.Run();
    }

    private StoryGraph CreateStoryGraph(
        StoryGraphAsset storyGraphAsset,
        StoryNodeFactory storyNodeFactory)
    {
        if (storyGraphAsset == null)
        {
            Debug.LogError("[GameplayBootstrap] Story Graph asset is missing.");
            return new StoryGraph();
        }

        return storyGraphAsset.CreateRuntimeGraph(storyNodeFactory);
    }
}
