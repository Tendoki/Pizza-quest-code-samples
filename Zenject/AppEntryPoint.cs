using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DialogueSystem;
using GameAudio;
using GameInput;
using Newtonsoft.Json;
using SaveLoad;
using UnityEngine;
using UnityEngine.Localization.Settings;
using Zenject;

public class AppEntryPoint : MonoBehaviour
{
    private static AppEntryPoint _instance;
    
    private AssetProvider _assetProvider;
    private GameDatabaseService _gameDatabaseService;
    private GameEventBus _gameEventBus;
    private SaveLoadService _saveLoadService;
    private DialogueLocaleSync _dialogueLocaleSync;
    private AppFlowController _appFlowController;
    private AudioManagerBase _audioManager;
    private CursorController _cursorController;
    private SceneFader _sceneFader;
    private GameInputController _gameInputController;
#if UNITY_EDITOR
    private EditorTestSaveService _editorTestSaveService;
#endif

    [Inject]
    private void Construct(
        AssetProvider assetProvider,
        GameDatabaseService gameDatabaseService,
        GameEventBus gameEventBus,
        SaveLoadService saveLoadService,
        DialogueLocaleSync dialogueLocaleSync,
        AppFlowController appFlowController,
        AudioManagerBase audioManager,
        CursorController cursorController,
        SceneFader sceneFader,
        GameInputController gameInputController
#if UNITY_EDITOR
        , EditorTestSaveService editorTestSaveService
#endif
        )
    {
        _assetProvider = assetProvider;
        _gameDatabaseService = gameDatabaseService;
        _gameEventBus = gameEventBus;
        _saveLoadService = saveLoadService;
        _dialogueLocaleSync = dialogueLocaleSync;
        _appFlowController = appFlowController;
        _audioManager = audioManager;
        _cursorController = cursorController;
        _sceneFader = sceneFader;
        _gameInputController = gameInputController;
#if UNITY_EDITOR
        _editorTestSaveService = editorTestSaveService;
#endif
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        BootAsync().Forget();
    }

    private async UniTask BootAsync()
    {
        try
        {
            Debug.Log("[BOOT] Start");
            Debug.Log("[BOOT] Setup Services Start");
            await SetupServices();
            Debug.Log("[BOOT] Setup Services Done");
            
            await _appFlowController.RunEntryPointFlowAsync();
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex);
        }
    }
    
    private async UniTask SetupServices()
    {
        await LocalizationSettings.InitializationOperation.ToUniTask();

        var gameDatabase = await _assetProvider.LoadGameDatabaseAsync();
        if (gameDatabase == null)
            Debug.LogError("[Services] Game Database addressable asset is missing.");
        _gameDatabaseService.SetDatabase(gameDatabase);

        await _gameEventBus.Init();
        await _audioManager.Init(gameDatabase?.AudioClipLibrary, CancellationToken.None);
        await _cursorController.Init();
        await _sceneFader.Init();
        await _gameInputController.Init();
        await _dialogueLocaleSync.Init();
        
        JsonConvert.DefaultSettings = () => new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new Vector2Converter() }
        };
        
#if UNITY_EDITOR
        await _editorTestSaveService.Init();
#endif
        await _appFlowController.Init();
    }

    private void OnDestroy()
    {
        if (_instance != this)
            return;

        _instance = null;
    }
}
