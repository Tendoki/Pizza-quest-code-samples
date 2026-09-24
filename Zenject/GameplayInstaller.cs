using CharacterActionMenu;
using DialogueSystem;
using GameMode;
using InterrogationSystem;
using InventorySystem;
using InventorySystem.AskAboutSystem;
using Map;
using Pause_Menu;
using Quest_System;
using Scene;
using Story_System;
using Story_System.Visual_Graph;
using UiNavigation;
using UnityEngine;
using UnityEngine.Rendering;
using Zenject;

public class GameplayInstaller : MonoInstaller
{
    [Header("Cameras")]
    [SerializeField] private Camera uiCamera;

    [Header("Visual Effects")]
    [SerializeField] private Volume gameplayVolume;

    [Header("Character Interaction")]
    [SerializeField] private CharacterActionMenuView characterActionMenuView;

    [Header("Dialogues")]
    [SerializeField] private DialogueView dialogueView;

    [Header("Inventory")]
    [SerializeField] private InventoryController inventoryController;

    [Header("Panels")]
    [SerializeField] private CluesPanelView cluesPanelView;
    [SerializeField] private SuspectsPanelView suspectsPanelView;

    [Header("Interrogation")]
    [SerializeField] private InterrogationController interrogationController;

    [Header("Map")]
    [SerializeField] private MapView mapView;

    [Header("Room UI")]
    [SerializeField] private ItemTooltipView itemTooltipView;
    [SerializeField] private RoomTitlePresenter roomTitlePresenter;
    [SerializeField] private RoomHudView roomHudView;

    [Header("Notification")]
    [SerializeField] private ClueNotificationView clueNotificationView;
    [SerializeField] private DossierNotificationView dossierNotificationView;

    [Header("Story Backdrop")]
    [SerializeField] private StoryBackdropPanelView storyBackdropPanelView;

    [Header("Pause Menu")]
    [SerializeField] private PauseMenuView pauseMenuView;

    public override void InstallBindings()
    {
        Container.BindInstance(uiCamera);
        Container.BindInstance(characterActionMenuView);
        Container.BindInstance(dialogueView);
        Container.BindInstance(inventoryController);
        Container.BindInstance(cluesPanelView);
        Container.BindInstance(suspectsPanelView);
        Container.BindInstance(interrogationController);
        Container.BindInstance(mapView);
        Container.BindInstance(itemTooltipView);
        Container.BindInstance(roomTitlePresenter);
        Container.BindInstance(roomHudView);
        Container.BindInstance(clueNotificationView);
        Container.BindInstance(dossierNotificationView);
        Container.BindInstance(storyBackdropPanelView);
        Container.BindInstance(pauseMenuView);

        Container.Bind<UiScreenRegistry>().AsSingle();
        Container.BindInterfacesAndSelfTo<WindowManager>().AsSingle();
        Container.Bind<GameModeCoordinator>().AsSingle();
        Container.Bind<IGameplayVisualEffects>().To<GameplayVisualEffects>().AsSingle().WithArguments(gameplayVolume);
        Container.Bind<DialogueState>().AsSingle();
        Container.Bind<CharactersState>().AsSingle();
        Container.Bind<CluesState>().AsSingle();
        Container.Bind<DossierState>().AsSingle();
        Container.Bind<RoomsState>().AsSingle();
        Container.Bind<SceneItemsState>().AsSingle();
        Container.Bind<GameplayFeaturesState>().AsSingle();
        Container.Bind<StoryState>().AsSingle();
        Container.Bind<GoalsState>().AsSingle();
        Container.BindInterfacesAndSelfTo<DialogueModel>().AsSingle();
        Container.BindInterfacesAndSelfTo<CharactersModel>().AsSingle();
        Container.BindInterfacesAndSelfTo<CluesModel>().AsSingle();
        Container.BindInterfacesAndSelfTo<DossierModel>().AsSingle();
        Container.BindInterfacesAndSelfTo<SelectionPanelModel>().AsSingle();
        Container.BindInterfacesAndSelfTo<InterrogationModel>().AsSingle();
        Container.BindInterfacesAndSelfTo<ProtocolModel>().AsSingle();
        Container.BindInterfacesAndSelfTo<ProtocolController>().AsSingle();
        Container.BindInterfacesAndSelfTo<CharacterActionMenuModel>().AsSingle();
        Container.BindInterfacesAndSelfTo<CharacterInteractionModel>().AsSingle();
        Container.BindInterfacesAndSelfTo<RoomsModel>().AsSingle();
        Container.BindInterfacesAndSelfTo<MapModel>().AsSingle();
        Container.Bind<SceneItemsModel>().AsSingle();
        Container.BindInterfacesAndSelfTo<InventoryButtonModel>().AsSingle();
        Container.Bind<RoomHudModel>().AsSingle();
        Container.Bind<GameplayAssetPreloadService>().AsSingle();
        Container.BindInterfacesAndSelfTo<DialogueController>().AsSingle();
        Container.Bind<RoomCharacterInteractionController>().AsSingle();
        Container.Bind<CluesPanelViewModel>()
            .AsSingle()
            .WithArguments(GameConstants.CLUES_VIEW_GRID_SIZE);
        Container.BindInterfacesAndSelfTo<CluesPanelController>().AsSingle();
        Container.Bind<SuspectsPanelViewModel>()
            .AsSingle()
            .WithArguments(GameConstants.SUSPECTS_VIEW_GRID_SIZE);
        Container.BindInterfacesAndSelfTo<SuspectsPanelController>().AsSingle();
        Container.Bind<MapViewModel>().AsSingle();
        Container.Bind<ItemTooltipController>().AsSingle();
        Container.Bind<RoomUiController>().AsSingle();
        Container.Bind<CurrentRoom>().AsSingle();
        Container.Bind<ObjectiveFactory>().AsSingle();
        Container.Bind<StoryNodeActionFactory>().AsSingle();
        Container.BindInterfacesAndSelfTo<RoomModeController>().AsSingle();
        Container.Bind<NotificationPresenter>().AsSingle();
        Container.Bind<GoalManager>().AsSingle();
        Container.Bind<StoryController>().AsSingle();
        Container.BindInterfacesAndSelfTo<PlayerProgressProvider>().AsSingle();
        Container.BindInterfacesAndSelfTo<GameplayController>().AsSingle();
        Container.BindInterfacesTo<GameplayEntryPoint>().AsSingle().NonLazy();
    }
}
