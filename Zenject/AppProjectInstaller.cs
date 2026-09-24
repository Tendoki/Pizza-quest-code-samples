using DialogueSystem;
using GameAudio;
using GameInput;
using UnityEngine;
using Zenject;

public class AppProjectInstaller : MonoInstaller
{
    [Header("Service Prefabs")]
    [SerializeField] private CursorController cursorControllerPrefab;
    [SerializeField] private SceneFader sceneFaderPrefab;
    [SerializeField] private LoadingScreen loadingScreenPrefab;
    [SerializeField] private GameInputController gameInputControllerPrefab;
    [SerializeField] private AudioListener audioListenerPrefab;

    [Header("Audio")]
    [SerializeField] private AudioBackendType audioBackend = AudioBackendType.UnityAudio;
    [SerializeField] private AudioEventPaths fmodEventPaths;

    public override void InstallBindings()
    {
        Container.Bind<AssetProvider>().AsSingle();
        Container.Bind<GameDatabaseService>().AsSingle();
        Container.Bind<GameEventBus>().AsSingle();
        Container.Bind<SaveLoadService>().AsSingle();
        Container.Bind<DialogueLocaleSync>().AsSingle();
        Container.Bind<GameplayCameraStackService>().AsSingle();
        Container.Bind<GameplayUiSceneService>().AsSingle();
        Container.BindInterfacesAndSelfTo<AppFlowController>().AsSingle();

        Container.Bind<CursorController>()
            .FromComponentInNewPrefab(cursorControllerPrefab)
            .AsSingle()
            .NonLazy();

        Container.Bind<SceneFader>()
            .FromComponentInNewPrefab(sceneFaderPrefab)
            .AsSingle()
            .NonLazy();

        Container.Bind<LoadingScreen>()
            .FromComponentInNewPrefab(loadingScreenPrefab)
            .AsSingle()
            .NonLazy();

        Container.Bind<GameInputController>()
            .FromComponentInNewPrefab(gameInputControllerPrefab)
            .AsSingle()
            .NonLazy();
        
        Container.Bind<AudioListener>()
            .FromComponentInNewPrefab(audioListenerPrefab)
            .AsSingle()
            .NonLazy();

        Container.Bind<AudioManagerBase>()
            .FromMethod(_ => CreateAudioManager())
            .AsSingle()
            .NonLazy();

#if UNITY_EDITOR
        Container.Bind<EditorTestSaveService>()
            .FromNewComponentOnNewGameObject()
            .AsSingle()
            .NonLazy();
#endif
    }

    private AudioManagerBase CreateAudioManager()
    {
        var serviceObject = new GameObject("Audio Manager");
        serviceObject.transform.SetParent(transform, false);

        switch (audioBackend)
        {
            case AudioBackendType.FMOD:
            {
                var audioManager = serviceObject.AddComponent<FmodAudioManager>();
                audioManager.SetEventPaths(fmodEventPaths);
                return audioManager;
            }
            default:
                return serviceObject.AddComponent<UnityAudioManager>();
        }
    }
}
