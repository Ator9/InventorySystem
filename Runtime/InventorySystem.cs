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
    public partial class InventorySystem : MonoBehaviour
    {
        public static InventorySystem Instance { get; private set; }

        public LootContainer CurrentLootContainer { get; private set; }
        public VisualElement Root { get; private set; }
        public VisualElement RootBody { get; private set; }
        public VisualElement RootLootSlots { get; private set; }
        public VisualElement Background { get; private set; }

        [Header("Templates")]
        [SerializeField] private VisualTreeAsset slotTemplate; // Assign Assets/UI/Inventory/Slot.uxml

        private List<VisualElement> slots;
        private readonly List<ItemSO> items = new();
        private int currentDraggedIndex = -1;
        private float slotWidth;
        private float slotHeight;
        private int selectedSlot = -1;

        public const int BaseSlots = 18;
        private const int maxContainerSlots = 20;

        private PlayerNetworkInventory playerNetworkInventory;

        private IInputService inputService;
        private IAudioFeedback audioFeedback;

        private VisualElement draggedElement;

        private void Awake()
        {
            // Set default services if none provided
            inputService ??= new KeyboardInputService();
            audioFeedback ??= new NullAudioFeedback();

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

            for (int i = 0; i < maxContainerSlots; i++)
                RootLootSlots.Add(CreateSlot());

            // Setup slots (ToList starts at index 0)
            slots = Root.Q("BottomSlots").Children().ToList();
            slots.AddRange(Root.Q("MiddleSlots").Children().ToList());
            slots.AddRange(RootLootSlots.Children().ToList());

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

        public void SetInitialVisibility(bool visible) 
        { 
            if (visible) OpenInventory();
            else CloseInventory(); 
        }

        public void ActivateContainerSlots(LootContainer lootContainer)
        {
            CurrentLootContainer = lootContainer;

            // Show only needed slots for the container, hide the rest
            for (int i = 0; i < maxContainerSlots; i++)
            {
                RootLootSlots.Children().ElementAt(i).style.display =
                    i < CurrentLootContainer.slots ? DisplayStyle.Flex : DisplayStyle.None;
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
            //print(gameObject.name);

            // Select slot with keys
            if (playerNetworkInventory != null)
                SelectSlot();
        }

        public void FillInventoryFromDatabase(string itemId, int amount, string containerId, int slot)
        {
            ItemSO itemSO = GameManager.AssetManager.GetItemById(itemId);
            if (itemSO == null)
                return;

            if (containerId == LootContainer.SafeContainerId)
            {
                FillLootContainer(itemSO, slot);
                return;
            }

            SetSlotVisual(slot, itemSO, amount);
        }

        public void AddItemToSlot(int index, ItemSO itemSO)
        {
            if (!IsValidSlotIndex(index) || itemSO == null)
                return;

            string targetContainerId = GetContainerIdForSlot(index);
            string sourceContainerId = GetContainerIdForSlot(currentDraggedIndex);

            bool swap = TrySwapItemIntoDraggedSlot(index, sourceContainerId);

            SetSlotVisual(index, itemSO);

            playerNetworkInventory.SyncAddItemRpc(targetContainerId, index, itemSO.Id, 1);

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
            // Check if Body (Tab) is active to allow moving items
            if (!IsInventoryOpen) return;

            var slotIndex = GetSlotIndex(evt.currentTarget as VisualElement);

            if (!HasItem(slotIndex))
                return;

            currentDraggedIndex = slotIndex;

            CreateDraggedElement(DraggedItem, evt.position);

            // Hide the icon from the slot while dragging (do not Clear() the slot)
            HideSlotIcon(slotIndex);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            UpdateDraggedElementPosition(evt.position);
        }

        // Add item to slot if it's a slot
        private void OnPointerUp(PointerUpEvent evt)
        {
            if (!IsDraggingItem) return;

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
            if (!IsDraggingItem) return;

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
                SetSlotVisual(currentDraggedIndex, items[currentDraggedIndex]);
            }

            RemoveDraggedElement();
            ClearDraggedState();

            audioFeedback?.PlayItemMove();
        }

        // Fill loot container
        public void FillLootContainer(ItemSO item, int slot)
        {
            if (item == null)
                return;

            SetSlotVisual(slot + BaseSlots, item);
        }

        public void ClearInventory()
        {
            for (int i = 0; i < slots.Count; i++)
            {
                ClearSlotVisual(i);
            }

            //selectedSlot = -1;
            //for (int i = 0; i < Mathf.Min(6, slots.Count); i++)
            //    slots[i].style.borderBottomColor = Color.grey;
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

            var icon = GetSlotIcon(slotIndex);
            var count = GetSlotCount(slotIndex);

            if (icon != null)
            {
                icon.style.backgroundImage = new StyleBackground(itemSO.icon);
                icon.style.opacity = 0.9f;
            }

            if (count != null)
            {
                count.text = amount > 1 ? amount.ToString() : string.Empty;
            }

            items[slotIndex] = itemSO;
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

            if (count != null)
            {
                count.text = string.Empty;
            }

            items[slotIndex] = null;
        }

        private bool TrySwapItemIntoDraggedSlot(int index, string sourceContainerId)
        {
            if (!HasItem(index) || index == currentDraggedIndex || currentDraggedIndex < 0)
                return false;

            var targetIcon = GetSlotIcon(index);
            var targetCount = GetSlotCount(index);
            var draggedIcon = GetSlotIcon(currentDraggedIndex);
            var draggedCount = GetSlotCount(currentDraggedIndex);
            var targetItem = items[index];

            if (targetIcon == null || draggedIcon == null || targetItem == null)
                return false;

            // Copy target item visual to dragged slot.
            draggedIcon.style.backgroundImage = targetIcon.style.backgroundImage;
            draggedIcon.style.opacity = targetIcon.style.opacity;

            if (draggedCount != null)
                draggedCount.text = targetCount?.text;

            items[currentDraggedIndex] = targetItem;

            playerNetworkInventory.SyncAddItemRpc(sourceContainerId, currentDraggedIndex, targetItem.Id, 1);

            return true;
        }

        private void CreateDraggedElement(ItemSO itemSO, Vector2 pointerPosition)
        {
            draggedElement = new Image { sprite = itemSO.icon };
            draggedElement.style.width = slotWidth;
            draggedElement.style.height = slotHeight;
            draggedElement.style.position = Position.Absolute;
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
        }

        private ItemSO DraggedItem => HasItem(currentDraggedIndex) ? items[currentDraggedIndex] : null;

        private string GetContainerIdForSlot(int slotIndex)
        {
            if (slotIndex < BaseSlots)
                return LootContainer.InventoryContainerId;

            return CurrentLootContainer != null
                ? CurrentLootContainer.GetContainerId()
                : LootContainer.SafeContainerId;
        }
    }
}
