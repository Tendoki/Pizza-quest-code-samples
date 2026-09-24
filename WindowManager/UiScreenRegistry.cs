using System;
using System.Collections.Generic;

namespace UiNavigation
{
    public sealed class UiScreenRegistry 
    {
        private readonly Dictionary<UiWindowId, IUiWindow> _screens = new();

        public void Register(IUiWindow window)
        {
            if (window == null)
                throw new ArgumentNullException(nameof(window));

            if (_screens.TryGetValue(window.WindowId, out var existing))
            {
                if (ReferenceEquals(existing, window))
                    return;

                throw new InvalidOperationException($"UiScreenRegistry: duplicate registration for '{window.WindowId}'.");
            }

            _screens[window.WindowId] = window;
        }

        public IUiWindow Get(UiWindowId windowId)
        {
            if (_screens.TryGetValue(windowId, out var screen))
                return screen;

            throw new InvalidOperationException($"UiScreenRegistry: screen '{windowId}' is not registered.");
        }

        public void Clear()
        {
            _screens.Clear();
        }
    }
}
