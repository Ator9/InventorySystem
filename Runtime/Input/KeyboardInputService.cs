using UnityEngine.InputSystem;

namespace Assets.InventorySystem.Runtime.Input
{
    // Default input provider using Unity's Input System Keyboard.
    public sealed class KeyboardInputService : IInputService
    {
        public bool TogglePressed()
        {
            return Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame;
        }

        public int? HotbarDigitPressed()
        {
            var k = Keyboard.current;
            if (k == null) return null;

            if (k.digit1Key.wasPressedThisFrame) return 0;
            if (k.digit2Key.wasPressedThisFrame) return 1;
            if (k.digit3Key.wasPressedThisFrame) return 2;
            if (k.digit4Key.wasPressedThisFrame) return 3;
            if (k.digit5Key.wasPressedThisFrame) return 4;
            if (k.digit6Key.wasPressedThisFrame) return 5;

            // Extend as needed for more slots:
            // if (k.digit7Key.wasPressedThisFrame) return 6; etc.

            return null;
        }
    }
}