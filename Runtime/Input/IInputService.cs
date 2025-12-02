namespace Assets.InventorySystem.Runtime.Input
{
    // Abstraction for inventory-related inputs.
    public interface IInputService
    {
        // Return true when the inventory toggle was pressed this frame.
        bool TogglePressed();

        // Return the hotbar index pressed (e.g., 0..8), or null if none.
        int? HotbarDigitPressed();
    }
}