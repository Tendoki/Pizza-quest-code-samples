using System;
using GameMode;
using InterrogationSystem;
using Map;
using UnityEngine;
using Zenject;

namespace Scene
{
    public class RoomSceneEntryPoint : IInitializable, IDisposable
    {
        private readonly Camera _worldCamera;
        private readonly RoomSceneController _roomSceneController;
        private readonly GameModeCoordinator _gameModeCoordinator;
        private readonly RoomsModel _roomsModel;
        private readonly GameplayCameraStackService _cameraStackService;
        private readonly GameplayController _gameplayController;
        private readonly RoomModeController _roomModeController;
        private readonly RoomCharacterInteractionController _roomCharacterInteractionController;
        private readonly InterrogationController _interrogationController;
        private readonly CurrentRoom _currentRoom;

        public RoomSceneEntryPoint(
            Camera worldCamera,
            RoomSceneController roomSceneController,
            GameModeCoordinator gameModeCoordinator,
            RoomsModel roomsModel,
            GameplayCameraStackService cameraStackService,
            GameplayController gameplayController,
            RoomModeController roomModeController,
            RoomCharacterInteractionController roomCharacterInteractionController,
            InterrogationController interrogationController,
            CurrentRoom currentRoom)
        {
            _worldCamera = worldCamera;
            _roomSceneController = roomSceneController;
            _gameModeCoordinator = gameModeCoordinator;
            _roomsModel = roomsModel;
            _cameraStackService = cameraStackService;
            _gameplayController = gameplayController;
            _roomModeController = roomModeController;
            _roomCharacterInteractionController = roomCharacterInteractionController;
            _interrogationController = interrogationController;
            _currentRoom = currentRoom;
        }

        public void Initialize()
        {
            _cameraStackService.ApplyTo(_worldCamera);

            string sceneName = _roomSceneController.gameObject.scene.name;

            _roomsModel.TrySetCurrentRoomID(sceneName);
            _roomsModel.TrySetRoomVisited(sceneName);

            _roomSceneController.Init();
            _currentRoom.Set(_roomSceneController, _roomSceneController, _roomSceneController.ItemInteractions);
            _roomModeController.BindRoom(_currentRoom);

            _gameModeCoordinator.Register(new IntroGameModeController());
            _gameModeCoordinator.Register(_roomModeController);
            _gameModeCoordinator.Register(_roomCharacterInteractionController);
            _gameModeCoordinator.Register(_interrogationController);

            Debug.Log("[RoomSceneEntryPoint] Initialized");

            _gameplayController.NotifyRoomSceneReady(_gameModeCoordinator);
        }

        public void Dispose()
        {
            try
            {
                _roomModeController.UnbindRoom();
                _currentRoom.Clear(_roomSceneController);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}
