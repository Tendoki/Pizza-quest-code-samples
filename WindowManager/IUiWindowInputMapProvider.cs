using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace UiNavigation
{
    public interface IUiWindowInputMapProvider : IUiWindow
    {
        InputActionMap InputMap { get; }
        IReadOnlyList<IUiPageInputMapProvider> PageInputMapProviders { get; }
        IReadOnlyList<InputActionMap> GetActiveInputMaps();
        void BindInputActions();
        void UnbindInputActions();
    }
}
