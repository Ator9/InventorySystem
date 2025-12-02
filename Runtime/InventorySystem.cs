using Assets.InventorySystem.Runtime.Input;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Assets.InventorySystem.Runtime
{
    public class InventorySystem : MonoBehaviour
    {
        public static InventorySystem Instance { get; private set; }

        public LootContainer CurrentLootContainer { get; private set; }
        public VisualElement Root { get; private set; }
        public VisualElement RootBody { get; private set; }
        public VisualElement RootLootSlots { get; private set; }
        public VisualElement Background { get; private set; }

        private List<VisualElement> slots;
        private List<ItemSO> items = new();
        private int currentDraggedIndex = -1;
        private float slotWidth;
        private float slotHeight;
        private int selectedSlot = -1;
        private int lootIndexOffset = 0;
        private readonly int lootSlots = 70;

        private PlayerNetworkInventory playerNetworkInventory;
        private IInputService inputService;

        private VisualElement draggedElement;

        private void Awake()
        {
            Instance = this;
            inputService ??= new KeyboardInputService();
        }

        private void OnEnable()
        {
            Root = GetComponent<UIDocument>().rootVisualElement;
            RootBody = Root.Q<VisualElement>("Body");
            RootBody.style.display = DisplayStyle.None;
            RootLootSlots = Root.Q<VisualElement>("LootSlots");
            RootLootSlots.style.display = DisplayStyle.None;

            // Create background overlay
            CreateBackground();

            for (int i = 0; i < lootSlots; i++)
                Root.Q("LootSlots").Add(CreateSlot());

            // Setup slots (ToList starts at index 0)
            slots = Root.Q("BottomSlots").Children().ToList();
            slots.AddRange(Root.Q("MiddleSlots").Children().ToList());
            slots.AddRange(Root.Q("LootSlots").Children().ToList());
            lootIndexOffset = slots.Count - lootSlots;

            for (int i = 0; i < slots.Count; i++)
            {
                slots[i].RegisterCallback<PointerDownEvent>(OnPointerDown);
                slots[i].RegisterCallback<PointerMoveEvent>(OnPointerMove);
                slots[i].RegisterCallback<PointerUpEvent>(OnPointerUp);
                items.Add(null);
            }

            // Register global PointerUpEvent listener
            Root.RegisterCallback<PointerUpEvent>(OnGlobalPointerUp);

            // Get width and height of one slot for visuals
            Root.schedule.Execute(() =>
            {
                slotWidth = slots[0].resolvedStyle.width;
                slotHeight = slots[0].resolvedStyle.height;
            }).ExecuteLater(0);
        }

        public void ActivateContainerSlots(LootContainer lootContainer)
        {
            CurrentLootContainer = lootContainer;

            // Show only needed slots for the container, hide the rest
            for (int i = 0; i < lootSlots; i++)
                Root.Q("LootSlots").Children().ElementAt(i).style.display = i < CurrentLootContainer.slots ? DisplayStyle.Flex : DisplayStyle.None;

            RootLootSlots.style.display = DisplayStyle.Flex;
        }

        public void DeactivateContainerSlots()
        {
            RootLootSlots.style.display = DisplayStyle.None;

            for (int i = 0; i < CurrentLootContainer.slots; i++)
            {
                slots[i + lootIndexOffset].Clear();
                items[i + lootIndexOffset] = null;
            }

            CurrentLootContainer = null;
        }

        private void Start()
        {
            playerNetworkInventory = LocalPlayerManager.Instance.LocalPlayer.GetComponent<PlayerNetworkInventory>();
            playerNetworkInventory.LoadItemsFromDatabaseRpc();
        }

        void Update()
        {
            // Toggle inventory via input service
            if (inputService != null && inputService.TogglePressed())
                ToggleInventory();

            SelectSlot(); // Select slot with number keys
        }

        // Fill Player inventory from database
        public void FillInventoryFromDatabase(string itemId, int amount, string containerId, int slot)
        {
            ItemSO itemSO = GameManager.AssetManager.GetItemById(itemId);
            if (itemSO == null) return;

            var image = new Image { sprite = itemSO.icon };
            image.style.opacity = 0.9f;
            slots[slot].Add(image);
            items[slot] = itemSO;
        }

        public void AddItemToSlot(int index, ItemSO itemSO)
        {
            string containerId = CurrentLootContainer != null ? CurrentLootContainer.GetContainerId() : "inventory";

            // Swap items
            bool swap = false;
            if (items[index] != null && index != currentDraggedIndex && currentDraggedIndex >= 0)
            {
                swap = true;
                items[currentDraggedIndex] = items[index];
                slots[currentDraggedIndex].Add(slots[index].Children().First());

                playerNetworkInventory.SyncAddItemRpc(containerId, currentDraggedIndex, items[currentDraggedIndex].Id, 1);
            }

            // Add item and icon
            var image = new Image { sprite = itemSO.icon };
            image.style.opacity = 0.9f;
            slots[index].Add(image);
            items[index] = itemSO;

            // Sync with server
            playerNetworkInventory.SyncAddItemRpc(containerId, index, itemSO.Id, 1);

            // Remove from old location if it's coming from another slot
            if (currentDraggedIndex >= 0 && currentDraggedIndex != index && swap == false)
            {
                items[currentDraggedIndex] = null;
                playerNetworkInventory.SyncRemoveItemRpc(containerId, currentDraggedIndex);
            }

            if (currentDraggedIndex == selectedSlot || index == selectedSlot)
                UpdateItemInHand();

            // Reset currentDraggedIndex
            currentDraggedIndex = -1;
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            // Check if Body (Tab) is active to allow moving items
            if (Root.Q<VisualElement>("Body").style.display == DisplayStyle.None) return;

            var slot = evt.currentTarget as VisualElement;

            // Check if the slot has an item
            if (items[slots.IndexOf(slot)] != null)
            {
                currentDraggedIndex = slots.IndexOf(slot);
                if (currentDraggedIndex >= 0 && items[currentDraggedIndex] != null)
                {
                    draggedElement = new Image { sprite = items[currentDraggedIndex].icon };
                    draggedElement.style.width = slotWidth;
                    draggedElement.style.height = slotHeight;
                    draggedElement.style.position = Position.Absolute;
                    draggedElement.pickingMode = PickingMode.Ignore; // Ignore the image to not block the pointer position

                    // Convert screen coordinates to local coordinates
                    Vector2 localMousePosition = Root.WorldToLocal(evt.position);
                    draggedElement.style.left = localMousePosition.x;
                    draggedElement.style.top = localMousePosition.y;

                    Root.Add(draggedElement);

                    // Remove the icon from the slot
                    slots[currentDraggedIndex].Clear();
                }
            }
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (draggedElement != null)
            {
                // Convert screen coordinates to local coordinates
                Vector2 localMousePosition = Root.WorldToLocal(evt.position);
                draggedElement.style.left = localMousePosition.x;
                draggedElement.style.top = localMousePosition.y;
            }
        }

        // Add item to slot if it's a slot
        private void OnPointerUp(PointerUpEvent evt)
        {
            if (currentDraggedIndex == -1) return;

            var targetIndex = slots.IndexOf(evt.currentTarget as VisualElement);

            AddItemToSlot(targetIndex, items[currentDraggedIndex]);

            draggedElement.RemoveFromHierarchy();
            draggedElement = null;
            currentDraggedIndex = -1;
        }

        // Restore the item back if the target is not a slot
        private void OnGlobalPointerUp(PointerUpEvent evt)
        {
            if (currentDraggedIndex == -1) return;

            if (!slots.Contains(evt.currentTarget as VisualElement))
                RestoreItem();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus == false)
                RestoreItem();
        }

        private void RestoreItem()
        {
            if (currentDraggedIndex >= 0)
            {
                AddItemToSlot(currentDraggedIndex, items[currentDraggedIndex]);
                draggedElement.RemoveFromHierarchy();
                draggedElement = null;
                currentDraggedIndex = -1;
            }
        }

        // Fill loot container
        public void FillLootContainer(ItemSO item, int slot)
        {
            AddItemToSlot(slot + lootIndexOffset, item);
        }

        public void ClearInventory()
        {
            for (int i = 0; i < slots.Count; i++)
            {
                slots[i].Clear();
                items[i] = null;
            }
        }

        // Select slot with number keys via input service
        private void SelectSlot()
        {
            var currentSlot = selectedSlot;

            var pressed = inputService?.HotbarDigitPressed();
            if (pressed.HasValue)
                selectedSlot = pressed.Value;

            if (currentSlot != selectedSlot || currentSlot == -1) // Check if slot changed or if it's the first time
            {
                for (int i = 0; i < 6; i++)
                    slots[i].style.borderBottomColor = new StyleColor((i == selectedSlot) ? Color.black : Color.grey);

                if (selectedSlot >= 0)
                    UpdateItemInHand();
            }
        }

        private void UpdateItemInHand()
        {
            // Clear previously equipped item
            playerNetworkInventory.ClearEquippedItem();

            if (selectedSlot < 0 || items[selectedSlot] == null) 
                return;

            // Instantiate the item in the player's hand
            GameObject item = Instantiate(items[selectedSlot].prefab);
            item.transform.SetParent(LocalPlayerManager.Instance.LocalPlayer.GetComponent<PlayerActions>().rightHand.transform);
            item.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

            // Broadcast the equipped item to all players
            playerNetworkInventory.SyncEquippedItemRpc(items[selectedSlot].Id);
        }

        private VisualElement CreateSlot()
        {
            var slot = new VisualElement();
            slot.style.width = 90;
            slot.style.height = 90;
            slot.style.minHeight = 50;
            slot.style.minWidth = 50;

            slot.style.marginLeft = 2;
            slot.style.marginRight = 2;
            slot.style.marginTop = 2;
            slot.style.marginBottom = 2;

            slot.style.borderBottomWidth = 1;
            slot.style.borderBottomColor = Color.grey;
            slot.style.borderTopWidth = 1;
            slot.style.borderTopColor = Color.grey;
            slot.style.borderLeftWidth = 1;
            slot.style.borderLeftColor = Color.grey;
            slot.style.borderRightWidth = 1;
            slot.style.borderRightColor = Color.grey;

            return slot;
        }

        public bool IsInventoryOpen => RootBody.style.display == DisplayStyle.Flex;

        public void OpenInventory()
        {
            if (RootBody == null || Background == null) return;
            RootBody.style.display = DisplayStyle.Flex;
            Background.style.display = DisplayStyle.Flex;
        }

        public void CloseInventory()
        {
            if (RootBody == null || Background == null) return;
            RootBody.style.display = DisplayStyle.None;
            Background.style.display = DisplayStyle.None;
        }

        public void ToggleInventory()
        {
            if (IsInventoryOpen)
                CloseInventory();
            else
                OpenInventory();

            // Sound on toggle
            GameManager.AudioSource.PlayOneShot(GameManager.AssetManager.changeTargetAudio, 0.3f);
        }

        private void CreateBackground()
        {
            Background = new VisualElement();
            Background.name = "InventoryBackground";

            // Style the background
            Background.style.position = Position.Absolute;
            Background.style.width = new Length(100, LengthUnit.Percent);
            Background.style.height = new Length(100, LengthUnit.Percent);
            Background.style.backgroundColor = new Color(0, 0, 0, 0.6f); // Semi-transparent black
            Background.style.display = DisplayStyle.None;

            // Insert at the beginning so it appears behind everything
            Root.Insert(0, Background);
        }

        // Host can inject a custom input service (gamepad, touch, remappable keys, etc.)
        public void SetInputService(IInputService service)
        {
            inputService = service ?? new KeyboardInputService();
        }
    }
}
