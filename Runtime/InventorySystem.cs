using Assets.InventorySystem.Runtime.Audio;
using Assets.InventorySystem.Runtime.Input;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Assets.InventorySystem.Runtime
{
[RequireComponent(typeof(UIDocument))]
    [RequireComponent(typeof(InventoryContextMenu))]
    public partial class InventorySystem : MonoBehaviour
    {
        public static InventorySystem Instance { get; private set; }

        private InventoryContextMenu inventoryContextMenu;
        public LootContainer CurrentLootContainer { get; private set; }
        public VisualElement Root { get; private set; }
        public VisualElement RootBody { get; private set; }
        public VisualElement RootLootSlots { get; private set; }
        public VisualElement Background { get; private set; }

        [Header("Templates")]
        [SerializeField] private VisualTreeAsset slotTemplate; // Assign Assets/UI/Inventory/Slot.uxml

        private List<VisualElement> slots;
        private readonly List<ItemSO> items = new();
        private readonly List<int> itemAmounts = new();
        private int currentDraggedIndex = -1;
        private float slotWidth;
        private float slotHeight;
        private int selectedSlot = -1;

        public const int BaseSlots = 18;
        private const int maxContainerSlots = 120;

        private PlayerNetworkInventory playerNetworkInventory;

        private IInputService inputService;
        private IAudioFeedback audioFeedback;

        private VisualElement draggedElement;

        private void Awake()
        {
            // Set default services if none provided
            inputService ??= new KeyboardInputService();
            audioFeedback ??= new NullAudioFeedback();
            inventoryContextMenu = GetComponent<InventoryContextMenu>();

            SceneManager.activeSceneChanged += OnSceneChanged;
        }

        private void Start()
        {
            // In menu, playerNetworkInventory may be null. UI will still initialize in OnEnable.
            if (LocalPlayerManager.Instance != null && LocalPlayerManager.Instance.LocalPlayer != null)
            {
                playerNetworkInventory = LocalPlayerManager.Instance.LocalPlayer.GetComponent<PlayerNetworkInventory>();
            }
        }

        private void OnEnable()
        {
            Root = GetComponent<UIDocument>().rootVisualElement;
            RootBody = Root.Q<VisualElement>("Body");
            RootLootSlots = Root.Q<VisualElement>("LootSlots");

            CreateBackground(); // Create background color

            inventoryContextMenu.Initialize();

            for (int i = 0; i < maxContainerSlots; i++)
                RootLootSlots.Add(CreateSlot());

            // Setup slots (ToList starts at index 0)
            slots = Root.Q("BottomSlots").Children().ToList();
            slots.AddRange(Root.Q("MiddleSlots").Children().ToList());

            // Reverse loot slots so container slot 0 maps to the top visual slot.
            slots.AddRange(RootLootSlots.Children().Reverse().ToList());

            for (int i = 0; i < slots.Count; i++)
            {
                slots[i].RegisterCallback<PointerDownEvent>(OnPointerDown);
                slots[i].RegisterCallback<PointerMoveEvent>(OnPointerMove);
                slots[i].RegisterCallback<PointerUpEvent>(OnPointerUp);
                items.Add(null);
                itemAmounts.Add(0);
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

            int visibleSlots = Mathf.Min(CurrentLootContainer.slots, maxContainerSlots);

            // Show the top-most generated slots so container slot 0 appears at the top.
            for (int i = 0; i < maxContainerSlots; i++)
            {
                bool shouldShow = i >= maxContainerSlots - visibleSlots;
                RootLootSlots.Children().ElementAt(i).style.display =
                    shouldShow ? DisplayStyle.Flex : DisplayStyle.None;
            }

            RootLootSlots.style.display = DisplayStyle.Flex;
        }

        public void DeactivateContainerSlots()
        {
            RootLootSlots.style.display = DisplayStyle.None;

            if (CurrentLootContainer == null)
                return;

            int visibleSlots = Mathf.Min(CurrentLootContainer.slots, maxContainerSlots);

            for (int i = 0; i < visibleSlots; i++)
            {
                ClearSlotVisual(i + BaseSlots);
            }

            CurrentLootContainer = null;
        }

        void Update()
        {
            if (!InteractionEnabled)
                return;

            // Select slot with keys
            if (playerNetworkInventory != null)
                SelectSlot();
        }

        public void FillInventoryFromDatabase(string itemId, int amount, string containerId, int slot)
        {
            ItemSO itemSO = GameManager.AssetManager.GetItemById(itemId);
            if (itemSO == null)
                return;

            int displayAmount = NormalizeItemAmount(itemSO, amount);

            if (containerId == LootContainer.SafeContainerId)
            {
                FillLootContainer(itemSO, slot, displayAmount);
                return;
            }

            SetSlotVisual(slot, itemSO, displayAmount);
        }

        public void AddItemToSlot(int index, ItemSO itemSO)
        {
            if (!IsValidSlotIndex(index) || itemSO == null)
                return;

            string targetContainerId = GetContainerIdForSlot(index);
            string sourceContainerId = GetContainerIdForSlot(currentDraggedIndex);
            int draggedAmount = DraggedItemAmount;

            bool swap = TrySwapItemIntoDraggedSlot(index, sourceContainerId);

            if (!swap)
            {
                bool isTakingFromOpenContainer = currentDraggedIndex >= BaseSlots && CurrentLootContainer != null;

                if (isTakingFromOpenContainer)
                {
                    SetSlotVisual(index, itemSO, draggedAmount);
                    ClearSlotVisual(currentDraggedIndex);

                    playerNetworkInventory.TakeContainerItemRpc(
                        sourceContainerId,
                        currentDraggedIndex,
                        targetContainerId,
                        index,
                        itemSO.Id
                    );

                    if (currentDraggedIndex == selectedSlot || index == selectedSlot)
                        UpdateItemInHand();

                    audioFeedback?.PlayItemMove();
                    ClearDraggedState();
                    return;
                }
            }

            SetSlotVisual(index, itemSO, draggedAmount);

            playerNetworkInventory.SyncAddItemRpc(targetContainerId, index, itemSO.Id, draggedAmount);

            if (currentDraggedIndex >= 0 && currentDraggedIndex != index && swap == false)
            {
                ClearSlotVisual(currentDraggedIndex);
                playerNetworkInventory.SyncRemoveItemRpc(sourceContainerId, currentDraggedIndex);
            }

            if (currentDraggedIndex == selectedSlot || index == selectedSlot)
                UpdateItemInHand();

            audioFeedback?.PlayItemMove();
            ClearDraggedState();
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (!InteractionEnabled || !IsInventoryOpen) return;

            var slotIndex = GetSlotIndex(evt.currentTarget as VisualElement);

            if (!HasItem(slotIndex))
                return;

            if (evt.button == (int)MouseButton.RightMouse)
            {
                inventoryContextMenu.Show(slotIndex, evt.position);
                evt.StopPropagation();
                return;
            }

            if (evt.button != (int)MouseButton.LeftMouse)
                return;

            currentDraggedIndex = slotIndex;

            CreateDraggedElement(DraggedItem, evt.position);

            // Hide the icon from the slot while dragging (do not Clear() the slot)
            HideSlotIcon(slotIndex);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!InteractionEnabled)
                return;

            UpdateDraggedElementPosition(evt.position);
        }

        // Add item to slot if it's a slot
        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!InteractionEnabled || !IsDraggingItem) return;

            var targetIndex = GetSlotIndex(evt.currentTarget as VisualElement);

            if (!IsValidSlotIndex(targetIndex))
            {
                RestoreItem();
                return;
            }

            AddItemToSlot(targetIndex, DraggedItem);

            RemoveDraggedElement();
        }

        // Restore the item back if the target is not a slot
        private void OnGlobalPointerUp(PointerUpEvent evt)
        {
            if (!InteractionEnabled || !IsDraggingItem) return;

            if (!IsValidSlotIndex(GetSlotIndex(evt.currentTarget as VisualElement)))
                RestoreItem();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
                RestoreItem();
        }

        private void RestoreItem()
        {
            if (!IsDraggingItem) return;

            if (HasItem(currentDraggedIndex))
            {
                SetSlotVisual(currentDraggedIndex, items[currentDraggedIndex], itemAmounts[currentDraggedIndex]);
            }

            RemoveDraggedElement();
            ClearDraggedState();

            audioFeedback?.PlayItemMove();
        }

        // Fill loot container
        public void FillLootContainer(ItemSO item, int slot, int amount = 1)
        {
            if (item == null)
                return;

            SetSlotVisual(slot + BaseSlots, item, amount);
        }

        public void ClearInventory()
        {
            for (int i = 0; i < slots.Count; i++)
            {
                ClearSlotVisual(i);
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
            return slotTemplate.CloneTree().Q<VisualElement>("Slot");
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

            inventoryContextMenu.Hide();
            RootBody.style.display = DisplayStyle.None;
            Background.style.display = DisplayStyle.None;
        }

        public void ToggleInventory()
        {
            if (IsInventoryOpen)
                CloseInventory();
            else
                OpenInventory();

            // Play audio feedback on toggle
            audioFeedback?.PlayToggle();
        }

        private void CreateBackground()
        {
            Background = new VisualElement
            {
                name = "InventoryBackground"
            };

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

        // Allow host to inject a custom audio feedback (e.g., using AudioSource or FMOD)
        public void SetAudioFeedback(IAudioFeedback feedback)
        {
            audioFeedback = feedback ?? new NullAudioFeedback();
        }

        // Bind the player's network inventory so UI interactions can sync
        public void SetPlayerNetworkInventory(PlayerNetworkInventory inventory)
        {
            playerNetworkInventory = inventory;
        }

        // Indicates the UI is ready to accept items (slots created)
        public bool IsUiReady => slots != null && slots.Count > 0;

        private void OnSceneChanged(Scene oldScene, Scene newScene)
        {
            // Choose the active InventorySystem based on known GameObjects per scene.
            ResolveActiveInstance(newScene);
        }

        private void ResolveActiveInstance(Scene scene)
        {
            // Search the new scene's root objects (excludes DontDestroyOnLoad)
            var roots = scene.GetRootGameObjects();

            // Prefer CharacterHud in gameplay scenes
            var hudGo = roots.FirstOrDefault(go => go.name == "CharacterHud");
            if (hudGo != null)
            {
                var gameInv = hudGo.GetComponent<InventorySystem>();
                if (gameInv != null && gameObject.name == "MainMenu")
                {
                    Instance = gameInv;
                    return;
                }
            }

            // Fallback: persistent MainMenu (DontDestroyOnLoad)
            var persistentMenu = GameObject.Find("MainMenu");
            if (persistentMenu != null && gameObject.name == "MainMenu")
            {
                var menuInvPersistent = persistentMenu.GetComponent<InventorySystem>();
                if (menuInvPersistent != null)
                {
                    Instance = menuInvPersistent;
                    return;
                }
            }
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnSceneChanged;
        }

        private void SetSlotVisual(int slotIndex, ItemSO itemSO, int amount = 1)
        {
            if (itemSO == null)
                return;

            amount = NormalizeItemAmount(itemSO, amount);

            var icon = GetSlotIcon(slotIndex);
            var count = GetSlotCount(slotIndex);

            if (icon != null)
            {
                icon.style.backgroundImage = new StyleBackground(itemSO.icon);
                icon.style.opacity = 0.9f;
            }

            SetSlotCount(slotIndex, count, itemSO, amount);

            items[slotIndex] = itemSO;
            itemAmounts[slotIndex] = amount;
        }

        private void ClearSlotVisual(int slotIndex)
        {
            var icon = GetSlotIcon(slotIndex);
            var count = GetSlotCount(slotIndex);

            if (icon != null)
            {
                icon.style.backgroundImage = null;
                icon.style.opacity = 1f;
            }

            SetSlotCount(slotIndex, count, null, 0);

            items[slotIndex] = null;
            itemAmounts[slotIndex] = 0;
        }

        private bool TrySwapItemIntoDraggedSlot(int index, string sourceContainerId)
        {
            if (!HasItem(index) || index == currentDraggedIndex || currentDraggedIndex < 0)
                return false;

            var targetIcon = GetSlotIcon(index);
            var draggedIcon = GetSlotIcon(currentDraggedIndex);
            var draggedCount = GetSlotCount(currentDraggedIndex);
            var targetItem = items[index];
            int targetAmount = itemAmounts[index];

            if (targetIcon == null || draggedIcon == null || targetItem == null)
                return false;

            // Copy target item visual to dragged slot.
            draggedIcon.style.backgroundImage = targetIcon.style.backgroundImage;
            draggedIcon.style.opacity = targetIcon.style.opacity;

            SetSlotCount(currentDraggedIndex, draggedCount, targetItem, targetAmount);

            items[currentDraggedIndex] = targetItem;
            itemAmounts[currentDraggedIndex] = targetAmount;

            playerNetworkInventory.SyncAddItemRpc(sourceContainerId, currentDraggedIndex, targetItem.Id, targetAmount);

            return true;
        }

        private void CreateDraggedElement(ItemSO itemSO, Vector2 pointerPosition)
        {
            var sourceIcon = GetSlotIcon(currentDraggedIndex);

            draggedElement = new VisualElement();
            draggedElement.style.position = Position.Absolute;
            draggedElement.style.width = sourceIcon != null ? sourceIcon.resolvedStyle.width : slotWidth;
            draggedElement.style.height = sourceIcon != null ? sourceIcon.resolvedStyle.height : slotHeight;
            draggedElement.style.backgroundImage = new StyleBackground(itemSO.icon);
            draggedElement.style.opacity = 0.9f;
            draggedElement.pickingMode = PickingMode.Ignore;

            UpdateDraggedElementPosition(pointerPosition);
            Root.Add(draggedElement);
        }

        private void UpdateDraggedElementPosition(Vector2 pointerPosition)
        {
            if (draggedElement == null)
                return;

            Vector2 localMousePosition = Root.WorldToLocal(pointerPosition);
            draggedElement.style.left = localMousePosition.x;
            draggedElement.style.top = localMousePosition.y;
        }

        private void RemoveDraggedElement()
        {
            if (draggedElement == null)
                return;

            draggedElement.RemoveFromHierarchy();
            draggedElement = null;
        }

        private VisualElement GetSlotIcon(int slotIndex)
        {
            var slot = GetSlot(slotIndex);
            return slot?.Q<VisualElement>("Icon");
        }

        private Label GetSlotCount(int slotIndex)
        {
            var slot = GetSlot(slotIndex);
            return slot?.Q<Label>("Count");
        }

        private void SetSlotCount(int slotIndex, Label count, ItemSO itemSO, int amount)
        {
            if (count == null)
                return;

            string text = string.Empty;

            if (itemSO is ConsumableSO consumable)
            {
                text = GetConsumableUsageLabel(consumable, amount);
            }
            else if (amount > 1)
            {
                // Reserved for stack counts.
                text = "x" + amount;
            }

            count.text = text;
            count.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private string GetConsumableUsageLabel(ConsumableSO consumable, int amount)
        {
            int maxUses = Mathf.Max(1, consumable.maxUses);
            int usesLeft = Mathf.Clamp(amount, 0, maxUses);
            int totalRestore = GetConsumableTotalRestore(consumable);

            if (totalRestore <= 0)
                return string.Empty;

            int currentRestore = GetConsumableRestoreValue(totalRestore, maxUses, usesLeft);

            if (maxUses <= 1)
                return currentRestore.ToString();

            return currentRestore + "/" + totalRestore;
        }

        private int GetConsumableTotalRestore(ConsumableSO consumable)
        {
            return Mathf.Max(0, consumable.health)
                + Mathf.Max(0, consumable.food)
                + Mathf.Max(0, consumable.water)
                + Mathf.Max(0, consumable.energy);
        }

        private int GetConsumableRestoreValue(int totalRestore, int maxUses, int usesLeft)
        {
            return Mathf.RoundToInt(totalRestore * (usesLeft / (float)maxUses));
        }

        private VisualElement GetSlot(int slotIndex)
        {
            return IsValidSlotIndex(slotIndex) ? slots[slotIndex] : null;
        }

        private bool IsValidSlotIndex(int slotIndex)
        {
            return slots != null && slotIndex >= 0 && slotIndex < slots.Count;
        }

        private void ClearDraggedState()
        {
            currentDraggedIndex = -1;
        }

        public bool IsDraggingItem => currentDraggedIndex >= 0;

        private int GetSlotIndex(VisualElement element)
        {
            return slots == null ? -1 : slots.IndexOf(element);
        }

        private bool HasItem(int slotIndex)
        {
            return IsValidSlotIndex(slotIndex) && items[slotIndex] != null;
        }

        private void HideSlotIcon(int slotIndex)
        {
            var icon = GetSlotIcon(slotIndex);
            if (icon != null)
                icon.style.backgroundImage = null;

            SetSlotCount(slotIndex, GetSlotCount(slotIndex), null, 0);
        }

        private ItemSO DraggedItem => HasItem(currentDraggedIndex) ? items[currentDraggedIndex] : null;

        private int DraggedItemAmount => HasItem(currentDraggedIndex) ? itemAmounts[currentDraggedIndex] : 0;

        private int NormalizeItemAmount(ItemSO itemSO, int amount)
        {
            if (amount > 0)
                return amount;

            if (itemSO is ConsumableSO consumable)
                return Mathf.Max(1, consumable.maxUses);

            return 1;
        }

        private string GetContainerIdForSlot(int slotIndex)
        {
            if (slotIndex < BaseSlots)
                return LootContainer.InventoryContainerId;

            return CurrentLootContainer != null
                ? CurrentLootContainer.GetContainerId()
                : LootContainer.SafeContainerId;
        }

        public bool HasItemAt(int slotIndex)
        {
            return HasItem(slotIndex);
        }

        public ItemSO GetItemAt(int slotIndex)
        {
            return HasItem(slotIndex) ? items[slotIndex] : null;
        }

        public void SellItemAt(int slotIndex)
        {
            if (!HasItem(slotIndex) || playerNetworkInventory == null)
                return;

            string containerId = GetContainerIdForSlot(slotIndex);
            playerNetworkInventory.SellItemRpc(containerId, slotIndex);
        }

        public void ClearItemAt(int slotIndex)
        {
            ClearSlotVisual(slotIndex);
            audioFeedback?.PlayItemMove();
        }

        public int GetItemAmountAt(int slotIndex)
        {
            return HasItem(slotIndex) ? itemAmounts[slotIndex] : 0;
        }

        public bool CanUseConsumableAt(int slotIndex)
        {
            return HasItem(slotIndex) && items[slotIndex] is ConsumableSO;
        }

        public void UseConsumableAt(int slotIndex, bool useAll)
        {
            if (!CanUseConsumableAt(slotIndex) || playerNetworkInventory == null)
                return;

            string containerId = GetContainerIdForSlot(slotIndex);
            playerNetworkInventory.UseConsumableRpc(containerId, slotIndex, useAll);
        }

        public void UpdateItemAmountAt(int slotIndex, int amount)
        {
            if (!HasItem(slotIndex))
                return;

            if (amount <= 0)
            {
                ClearSlotVisual(slotIndex);

                if (slotIndex == selectedSlot)
                    UpdateItemInHand();

                audioFeedback?.PlayItemMove();
                return;
            }

            SetSlotVisual(slotIndex, items[slotIndex], amount);
            audioFeedback?.PlayItemMove();
        }

        public bool InteractionEnabled { get; private set; } = true;

        public void SetInteractionEnabled(bool enabled)
        {
            InteractionEnabled = enabled;

            if (!enabled)
            {
                inventoryContextMenu.Hide();

                if (IsDraggingItem)
                    RestoreItem();
            }
        }
    }
}
