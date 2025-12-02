namespace Assets.InventorySystem.Runtime.Audio
{
    public interface IAudioFeedback
    {
        // Called when inventory toggles open/close.
        void PlayToggle();

        // Called when an item is moved between slots (add, swap, restore).
        void PlayItemMove();
    }

    // Default no-op; hosts can inject a real implementation.
    public sealed class NullAudioFeedback : IAudioFeedback
    {
        public void PlayToggle() { /* intentionally empty */ }
        public void PlayItemMove() { /* intentionally empty */ }
    }
}