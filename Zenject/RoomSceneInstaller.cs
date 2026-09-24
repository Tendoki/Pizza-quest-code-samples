using UnityEngine;
using UnityEngine.Serialization;
using Zenject;

namespace Scene
{
    public class RoomSceneInstaller : MonoInstaller
    {
        [Header("Cameras")]
        [SerializeField] private Camera worldCamera;

        [FormerlySerializedAs("roomController")]
        [Header("Room")]
        [SerializeField] private RoomSceneController roomSceneController;

        public override void InstallBindings()
        {
            Container.BindInstance(worldCamera);
            Container.BindInstance(roomSceneController);
            Container.BindInterfacesAndSelfTo<ItemInteractionController>().AsSingle();
            Container.BindInterfacesTo<RoomSceneEntryPoint>().AsSingle().NonLazy();
        }
    }
}
