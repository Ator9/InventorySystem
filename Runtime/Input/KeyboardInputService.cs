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

            var keys = new[] { 
                k.digit1Key, k.digit2Key, k.digit3Key,
                k.digit4Key, k.digit5Key, k.digit6Key
            };

            for (var i = 0; i < keys.Length; i++)
            {
                if (keys[i].wasPressedThisFrame) return i;
            }

            return null;
        }
    }
}