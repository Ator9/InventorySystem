using Assets.InventorySystem.Runtime.Audio;
using Assets.InventorySystem.Runtime.Input;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
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

        [Header("Initial Visibility")]
        [SerializeField] private bool startHidden = true; // Live Game default: hidden

        [Header("Input")]
        [SerializeField] private bool allowToggleKey = true; // Menu: set false to disable Tab toggling

        [Header("Templates")]
        [SerializeField] private VisualTreeAsset slotTemplate; // Assign Assets/UI/Inventory/Slot.uxml

        private List<VisualElement> slots;
        private List<ItemSO> items = new();
        private int currentDraggedIndex = -1;
        private float slotWidth;
        private float slotHeight;
        private int selectedSlot = -1;
        private readonly int baseSlots = 18;
        private readonly int lootSlots = 120;

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

            ApplyInitialValues();
            CreateBackground(); // Create background color

            for (int i = 0; i < lootSlots; i++)
                Root.Q("LootSlots").Add(CreateSlot());

            // Setup slots (ToList starts at index 0)
            slots = Root.Q("BottomSlots").Children().ToList();
            slots.AddRange(Root.Q("MiddleSlots").Children().ToList());
            slots.AddRange(Root.Q("LootSlots").Children().ToList());

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
            {
                Root.Q("LootSlots").Children().ElementAt(i).style.display = i < CurrentLootContainer.slots ? DisplayStyle.Flex : DisplayStyle.None;
            }

            RootLootSlots.style.display = DisplayStyle.Flex;
        }

        public void DeactivateContainerSlots()
        {
            RootLootSlots.style.display = DisplayStyle.None;

            for (int i = 0; i < CurrentLootContainer.slots; i++)
            {
                slots[i + baseSlots].Clear();
                items[i + baseSlots] = null;
            }

            CurrentLootContainer = null;
        }

        void Update()
        {
            // Toggle inventory via input service
            if (allowToggleKey && inputService != null && inputService.TogglePressed())
                ToggleInventory();

            // Select slot with keys
            if (playerNetworkInventory != null)
                SelectSlot();
        }

        public void FillInventoryFromDatabase(string itemId, int amount, string containerId, int slot)
        {
            ItemSO itemSO = GameManager.AssetManager.GetItemById(itemId);
            if (itemSO == null) return;

            var icon = slots[slot].Q<VisualElement>("Icon");
            var count = slots[slot].Q<Label>("Count");

            icon.style.backgroundImage = new StyleBackground(itemSO.icon);
            icon.style.opacity = 0.9f;
            count.text = amount > 1 ? amount.ToString() : string.Empty;

            items[slot] = itemSO;
        }

        public void AddItemToSlot(int index, ItemSO itemSO)
        {
            string containerId = CurrentLootContainer != null ? CurrentLootContainer.GetContainerId() : "inventory";

            bool swap = false;
            if (items[index] != null && index != currentDraggedIndex && currentDraggedIndex >= 0)
            {
                swap = true;

                // Move icon/background from target to dragged slot
                var targetSlot = slots[index];
                var draggedSlot = slots[currentDraggedIndex];

                var targetIcon = targetSlot.Q<VisualElement>("Icon");
                var targetCount = targetSlot.Q<Label>("Count");
                var draggedIcon = draggedSlot.Q<VisualElement>("Icon");
                var draggedCount = draggedSlot.Q<Label>("Count");

                // Copy target item visual to dragged
                draggedIcon.style.backgroundImage = targetIcon.style.backgroundImage;
                draggedIcon.style.opacity = targetIcon.style.opacity;
                draggedCount.text = targetCount?.text;

                // Copy item data
                items[currentDraggedIndex] = items[index];

                playerNetworkInventory.SyncAddItemRpc(containerId, currentDraggedIndex, items[currentDraggedIndex].Id, 1);
            }

            // Set new item visuals on target slot
            var newIcon = slots[index].Q<VisualElement>("Icon");
            var newCount = slots[index].Q<Label>("Count");

            if (newIcon != null)
            {
                newIcon.style.backgroundImage = new StyleBackground(itemSO.icon);
                newIcon.style.opacity = 0.9f;
            }
            if (newCount != null)
            {
                newCount.text = "1";
            }

            items[index] = itemSO;

            playerNetworkInventory.SyncAddItemRpc(containerId, index, itemSO.Id, 1);

            if (currentDraggedIndex >= 0 && currentDraggedIndex != index && swap == false)
            {
                // Clear dragged slot visuals
                var draggedIcon = slots[currentDraggedIndex].Q<VisualElement>("Icon");
                var draggedCount = slots[currentDraggedIndex].Q<Label>("Count");

                if (draggedIcon != null)
                {
                    draggedIcon.style.backgroundImage = null;
                    draggedIcon.style.opacity = 1f;
                }
                if (draggedCount != null)
                {
                    draggedCount.text = string.Empty;
                }

                items[currentDraggedIndex] = null;
                playerNetworkInventory.SyncRemoveItemRpc(containerId, currentDraggedIndex);
            }

            if (currentDraggedIndex == selectedSlot || index == selectedSlot)
                UpdateItemInHand();

            audioFeedback?.PlayItemMove();
            currentDraggedIndex = -1;
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            // Check if Body (Tab) is active to allow moving items
            if (Root.Q<VisualElement>("Body").style.display == DisplayStyle.None) return;

            var slot = evt.currentTarget as VisualElement;
            var slotIndex = slots.IndexOf(slot);

            // Check if the slot has an item
            if (items[slotIndex] != null)
            {
                currentDraggedIndex = slotIndex;

                draggedElement = new Image { sprite = items[currentDraggedIndex].icon };
                draggedElement.style.width = slotWidth;
                draggedElement.style.height = slotHeight;
                draggedElement.style.position = Position.Absolute;
                draggedElement.pickingMode = PickingMode.Ignore;

                // Convert screen coordinates to local coordinates
                Vector2 localMousePosition = Root.WorldToLocal(evt.position);
                draggedElement.style.left = localMousePosition.x;
                draggedElement.style.top = localMousePosition.y;

                Root.Add(draggedElement);

                // Hide the icon from the slot while dragging (do not Clear() the slot)
                slot.Q<VisualElement>("Icon").style.backgroundImage = null;
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
                // Restore the icon background to the original slot
                var icon = slots[currentDraggedIndex].Q<VisualElement>("Icon");
                if (icon != null && items[currentDraggedIndex] != null)
                {
                    icon.style.backgroundImage = new StyleBackground(items[currentDraggedIndex].icon);
                    icon.style.opacity = 0.9f;
                }

                draggedElement.RemoveFromHierarchy();
                draggedElement = null;
                currentDraggedIndex = -1;

                audioFeedback?.PlayItemMove();
            }
        }

        // Fill loot container
        public void FillLootContainer(ItemSO item, int slot)
        {
            AddItemToSlot(slot + baseSlots, item);
        }

        public void ClearInventory()
        {
            for (int i = 0; i < slots.Count; i++)
            {
                var icon = slots[i].Q<VisualElement>("Icon");
                var count = slots[i].Q<Label>("Count");

                if (icon != null)
                {
                    icon.style.backgroundImage = null;
                    icon.style.opacity = 1f;
                }
                if (count != null)
                {
                    count.text = string.Empty;
                }

                items[i] = null;
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

            // Reapply scene defaults to avoid inheriting the previous scene’s state
            ApplyInitialValues();
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

        private void ApplyInitialValues()
        {
            if (RootBody != null)
                RootBody.style.display = startHidden ? DisplayStyle.None : DisplayStyle.Flex;

            if (RootLootSlots != null)
                RootLootSlots.style.display = startHidden ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnSceneChanged;
        }
    }
}
