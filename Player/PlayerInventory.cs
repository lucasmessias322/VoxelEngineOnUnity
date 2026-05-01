using System;
using System.Collections.Generic;
using UnityEngine;

public class PlayerInventory : MonoBehaviour
{
    public event Action ContentsChanged;
    private const string DefaultItemAtlasDataResourcePath = "ItemAtlasDataSO";

    public static PlayerInventory Instance { get; private set; }

    [Header("Slots Mapping")]
    [SerializeField] private Transform slotsContainer;
    [SerializeField] private bool includeInactiveSlots = true;
    [SerializeField] private bool autoMapOnAwake = true;

    [Header("Inventory UI")]
    [SerializeField] private GameObject inventoryUI;
    [SerializeField] private KeyCode toggleInventoryKey = KeyCode.E;
    [SerializeField] private bool lockCursorWhenClosed = true;

    [Header("Quick Move")]
    [Min(0)] [SerializeField] private int hotbarSlotCount = 7;

    [Header("Drop Controls")]
    [SerializeField] private KeyCode dropItemKey = KeyCode.Q;
    [SerializeField] private bool allowHotbarDropWhenInventoryClosed = true;
    [SerializeField] private bool allowInventoryDropWhenInventoryOpen = true;
    [SerializeField] private Transform dropOrigin;
    [Min(0f)] [SerializeField] private float dropForwardOffset = 0.8f;
    [SerializeField] private float dropVerticalOffset = -0.15f;
    [Min(1f)] [SerializeField] private float dropLaunchStrength = 2.15f;
    [SerializeField] private float dropLaunchUpwardBias = 0.35f;
    [SerializeField] private HotbarMirror hotbarMirror;

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip addItemClip;
    [Range(0f, 1f)] [SerializeField] private float addItemVolume = 1f;
    [Min(0f)] [SerializeField] private float addItemSoundMinInterval = 0.06f;

    [Header("Item Atlas")]
    [SerializeField] private ItemAtlasDataSO itemAtlasData;

    [Header("Runtime (Read Only)")]
    [SerializeField] private Slot[] slots;

    public Slot[] Slots => slots;
    public Transform SlotsContainer => slotsContainer;
    public bool IsInventoryOpen => inventoryUI != null && inventoryUI.activeSelf;
    public ItemAtlasDataSO ItemAtlasData => itemAtlasData;

    private float lastAddItemSoundTime = -999f;
    private int contentChangeBatchDepth;
    private bool hasPendingContentChangeNotification;

    private void Awake()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        InitializeItemAtlasLookup();

        if (autoMapOnAwake)
            MapSlotsFromContainer();

        if (inventoryUI != null)
        {
            inventoryUI.SetActive(false);
            ApplyCursorState(false);
        }

        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("Multiple PlayerInventory instances detected. Destroying duplicate.");
            Destroy(this);
            return;
        }

        Instance = this;
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleInventoryKey))
            ToggleInventoryUI();

        HandleDropInput();
    }

    private void OnValidate()
    {
        InitializeItemAtlasLookup();

        if (!Application.isPlaying && autoMapOnAwake)
            MapSlotsFromContainer();
    }

    [ContextMenu("Map Slots From Container")]
    public void MapSlotsFromContainer()
    {
        if (slotsContainer == null)
        {
            slots = System.Array.Empty<Slot>();
            NotifyContentsChanged();
            return;
        }

        List<Slot> found = new List<Slot>();
        CollectSlotsRecursive(slotsContainer, found);

        slots = found.ToArray();
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] != null)
                slots[i].SetIndex(i);
        }

        NotifyContentsChanged();
    }

    public bool AddItem(Item item, int amount = 1)
    {
        return InsertItem(item, amount) == 0;
    }

    public int InsertItem(Item item, int amount = 1, Slot excludedSlot = null)
    {
        if (item == null || amount <= 0)
            return amount;

        EnsureSlotsMappedIfNeeded();
        if (slots == null || slots.Length == 0)
            return amount;

        int requested = amount;
        int remaining = amount;

        BeginContentChangeBatch();
        try
        {
            remaining = InsertIntoMatchingSlots(item, remaining, 0, slots.Length, excludedSlot);
            remaining = InsertIntoEmptySlots(item, remaining, 0, slots.Length, excludedSlot);
        }
        finally
        {
            EndContentChangeBatch();
        }

        if (requested != remaining)
            TryPlayAddItemSound();

        return remaining;
    }

    public bool TryAddItemFully(Item item, int amount = 1)
    {
        if (item == null || amount <= 0)
            return false;

        EnsureSlotsMappedIfNeeded();
        if (!HasSpaceFor(item, amount))
            return false;

        return InsertItem(item, amount) == 0;
    }

    public bool TryMoveSlotToInventory(Slot sourceSlot)
    {
        if (sourceSlot == null || sourceSlot.IsEmpty)
            return false;

        Item sourceItem = sourceSlot.item;
        int originalAmount = sourceSlot.amount;
        int remaining = InsertItem(sourceItem, originalAmount, sourceSlot);
        int movedAmount = originalAmount - remaining;
        if (movedAmount <= 0)
            return false;

        if (remaining <= 0)
            sourceSlot.Clear();
        else
            sourceSlot.SetContents(sourceItem, remaining);

        return true;
    }

    public bool TryQuickMoveSlot(Slot sourceSlot)
    {
        if (sourceSlot == null || sourceSlot.IsEmpty)
            return false;

        EnsureSlotsMappedIfNeeded();
        if (slots == null || slots.Length == 0)
            return false;

        int sourceIndex = GetSlotIndex(sourceSlot);
        if (sourceIndex < 0)
            return false;

        int resolvedHotbarCount = Mathf.Clamp(hotbarSlotCount, 0, slots.Length);
        if (resolvedHotbarCount <= 0 || resolvedHotbarCount >= slots.Length)
            return false;

        bool sourceIsInHotbar = sourceIndex < resolvedHotbarCount;
        int targetStart = sourceIsInHotbar ? resolvedHotbarCount : 0;
        int targetEnd = sourceIsInHotbar ? slots.Length : resolvedHotbarCount;
        return TryMoveInventorySlotToRange(sourceSlot, targetStart, targetEnd);
    }

    public int RemoveItem(Item item, int amount = 1)
    {
        if (item == null || amount <= 0) return 0;
        if (slots == null || slots.Length == 0) return 0;

        int remaining = amount;
        int removed = 0;

        BeginContentChangeBatch();
        try
        {
            for (int i = 0; i < slots.Length && remaining > 0; i++)
            {
                Slot slot = slots[i];
                if (slot == null || slot.IsEmpty || slot.item != item) continue;

                int now = slot.Remove(remaining);
                remaining -= now;
                removed += now;
            }
        }
        finally
        {
            EndContentChangeBatch();
        }

        return removed;
    }

    public int CountItem(Item item)
    {
        if (item == null || slots == null) return 0;

        int total = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            Slot slot = slots[i];
            if (slot == null || slot.IsEmpty || slot.item != item) continue;
            total += slot.amount;
        }

        return total;
    }

    public bool HasSpaceFor(Item item, int amount = 1)
    {
        if (item == null || amount <= 0) return false;
        EnsureSlotsMappedIfNeeded();
        if (slots == null || slots.Length == 0) return false;

        return GetAvailableCapacityFor(item) >= amount;
    }

    public int GetAvailableCapacityFor(Item item)
    {
        EnsureSlotsMappedIfNeeded();
        if (item == null || slots == null || slots.Length == 0)
            return 0;

        int capacity = 0;
        int stackLimit = Mathf.Max(1, item.maxStack);

        for (int i = 0; i < slots.Length; i++)
        {
            Slot slot = slots[i];
            if (slot == null) continue;
            if (slot.IsEmpty)
            {
                if (slot.CanAccept(item, 1))
                    capacity += stackLimit;
            }
            else if (slot.item == item && slot.CanAccept(item, 1))
            {
                capacity += Mathf.Max(0, stackLimit - slot.amount);
            }
        }

        return capacity;
    }

    public void ClearInventory()
    {
        if (slots == null) return;

        BeginContentChangeBatch();
        try
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != null)
                    slots[i].Clear();
            }
        }
        finally
        {
            EndContentChangeBatch();
        }
    }

    public bool TryAddBlockDrop(BlockType blockType, int amount = 1)
    {
        if (!TryGetItemForBlock(blockType, out Item mappedItem)) return false;
        return AddItem(mappedItem, amount);
    }

    public bool TryDropItemStackToWorld(Item itemToDrop, int amount)
    {
        if (itemToDrop == null || amount <= 0)
            return false;

        Vector3 dropDirection = ResolveDropDirection();
        Vector3 dropLaunchVector = ResolveDropLaunchVector(dropDirection);
        Vector3 dropPosition = ResolveDropPosition(dropDirection);

        if (TryGetBlockForItem(itemToDrop, out BlockType mappedBlockType))
        {
            World world = World.Instance;
            if (world == null)
                return false;

            int clampedAmount = Mathf.Clamp(amount, 1, Mathf.Max(1, itemToDrop.maxStack));
            return BlockDrop.Spawn(world, dropPosition, mappedBlockType, clampedAmount, dropLaunchVector);
        }

        return InventoryItemDrop.Spawn(itemToDrop, amount, dropPosition, dropLaunchVector);
    }

    public void ToggleInventoryUI()
    {
        if (inventoryUI == null) return;
        SetInventoryOpen(!inventoryUI.activeSelf);
    }

    public void SetInventoryOpen(bool isOpen)
    {
        if (inventoryUI == null) return;
        inventoryUI.SetActive(isOpen);
        ApplyCursorState(isOpen);

        if (CraftingStationUIController.Instance != null)
            CraftingStationUIController.Instance.HandleInventoryVisibilityChanged(isOpen);

        if (CraftingMenuUI.Instance != null)
            CraftingMenuUI.Instance.HandleInventoryVisibilityChanged(isOpen);

        if (FurnaceUIController.Instance != null)
            FurnaceUIController.Instance.HandleInventoryVisibilityChanged(isOpen);

        if (ChestUIController.Instance != null)
            ChestUIController.Instance.HandleInventoryVisibilityChanged(isOpen);

        if (!isOpen)
            Slot.OnInventoryClosed(this);
    }

    private void ApplyCursorState(bool inventoryOpen)
    {
        if (!lockCursorWhenClosed) return;

        Cursor.visible = inventoryOpen;
        Cursor.lockState = inventoryOpen ? CursorLockMode.None : CursorLockMode.Locked;
    }

    private void HandleDropInput()
    {
        if (!Input.GetKeyDown(dropItemKey))
            return;

        bool dropFullStack = IsFullStackDropModifierHeld();
        if (IsInventoryOpen)
        {
            if (!allowInventoryDropWhenInventoryOpen)
                return;

            if (TryDropHoveredInventorySlot(dropFullStack))
                return;

            Slot.TryDropCarriedStack(this, dropFullStack);
            return;
        }

        if (!allowHotbarDropWhenInventoryClosed)
            return;

        TryDropSelectedHotbarSlot(dropFullStack);
    }

    private bool TryDropHoveredInventorySlot(bool dropFullStack)
    {
        if (!Slot.TryGetHoveredSlot(out Slot hovered))
            return false;

        if (hovered == null || hovered.IsEmpty || !ContainsSlot(hovered))
            return false;

        int amountToDrop = dropFullStack ? hovered.amount : 1;
        return TryDropFromSlot(hovered, amountToDrop);
    }

    private bool TryDropSelectedHotbarSlot(bool dropFullStack)
    {
        EnsureSlotsMappedIfNeeded();
        if (slots == null || slots.Length == 0)
            return false;

        int selectedSlotIndex = ResolveSelectedHotbarSlotIndex();
        if (selectedSlotIndex < 0 || selectedSlotIndex >= slots.Length)
            return false;

        Slot selectedSlot = slots[selectedSlotIndex];
        if (selectedSlot == null || selectedSlot.IsEmpty)
            return false;

        int amountToDrop = dropFullStack ? selectedSlot.amount : 1;
        return TryDropFromSlot(selectedSlot, amountToDrop);
    }

    private bool TryDropFromSlot(Slot sourceSlot, int amountToDrop)
    {
        if (sourceSlot == null || sourceSlot.IsEmpty || amountToDrop <= 0)
            return false;

        Item sourceItem = sourceSlot.item;
        int clampedAmount = Mathf.Clamp(amountToDrop, 1, sourceSlot.amount);
        int removedAmount = sourceSlot.Remove(clampedAmount);
        if (removedAmount <= 0)
            return false;

        if (TryDropItemStackToWorld(sourceItem, removedAmount))
            return true;

        sourceSlot.Add(sourceItem, removedAmount);
        return false;
    }

    public bool TryDropEntireStackFromSlot(Slot sourceSlot)
    {
        if (sourceSlot == null)
            return false;

        return TryDropFromSlot(sourceSlot, sourceSlot.amount);
    }

    private int ResolveSelectedHotbarSlotIndex()
    {
        if (slots == null || slots.Length == 0)
            return -1;

        int maxHotbarIndex = Mathf.Min(slots.Length, Mathf.Max(0, hotbarSlotCount)) - 1;
        if (maxHotbarIndex < 0)
            return -1;

        if (hotbarMirror == null)
            hotbarMirror = FindAnyObjectByType<HotbarMirror>();

        if (hotbarMirror == null)
            return 0;

        return Mathf.Clamp(hotbarMirror.SelectedSlotIndex, 0, maxHotbarIndex);
    }

    private Vector3 ResolveDropDirection()
    {
        Transform source = dropOrigin != null ? dropOrigin : (Camera.main != null ? Camera.main.transform : transform);
        Vector3 direction = source != null ? source.forward : transform.forward;
        if (direction.sqrMagnitude < 0.0001f)
            direction = Vector3.forward;

        return direction.normalized;
    }

    private Vector3 ResolveDropPosition(Vector3 direction)
    {
        Transform source = dropOrigin != null ? dropOrigin : (Camera.main != null ? Camera.main.transform : transform);
        Vector3 basePosition = source != null ? source.position : transform.position;
        return basePosition + direction * dropForwardOffset + Vector3.up * dropVerticalOffset;
    }

    private Vector3 ResolveDropLaunchVector(Vector3 direction)
    {
        Transform source = dropOrigin != null ? dropOrigin : (Camera.main != null ? Camera.main.transform : transform);
        Vector3 upward = source != null ? source.up : Vector3.up;
        if (upward.sqrMagnitude < 0.0001f)
            upward = Vector3.up;

        Vector3 launchVector = direction.normalized * Mathf.Max(1f, dropLaunchStrength);
        launchVector += upward.normalized * dropLaunchUpwardBias;
        return launchVector;
    }

    private static bool IsFullStackDropModifierHeld()
    {
        return Input.GetKey(KeyCode.LeftControl) ||
               Input.GetKey(KeyCode.RightControl) ||
               Input.GetKey(KeyCode.LeftShift) ||
               Input.GetKey(KeyCode.RightShift);
    }

    private void CollectSlotsRecursive(Transform current, List<Slot> output)
    {
        if (current == null) return;

        if (!includeInactiveSlots && !current.gameObject.activeInHierarchy)
            return;

        Slot slot = current.GetComponent<Slot>();
        if (slot != null && slot.RequiresInventorySlotMapping)
            output.Add(slot);

        for (int i = 0; i < current.childCount; i++)
            CollectSlotsRecursive(current.GetChild(i), output);
    }

    public bool TryConsumeFromSlot(int slotIndex, int amount = 1, Item expectedItem = null)
    {
        if (amount <= 0) return false;
        EnsureSlotsMappedIfNeeded();
        if (slots == null || slots.Length == 0) return false;
        if (slotIndex < 0 || slotIndex >= slots.Length) return false;

        Slot slot = slots[slotIndex];
        if (slot == null || slot.IsEmpty) return false;
        if (expectedItem != null && slot.item != expectedItem) return false;
        if (slot.amount < amount) return false;

        int removed = slot.Remove(amount);
        return removed == amount;
    }

    public void NotifyContentsChanged()
    {
        if (contentChangeBatchDepth > 0)
        {
            hasPendingContentChangeNotification = true;
            return;
        }

        ContentsChanged?.Invoke();
    }

    public bool TryGetItemForBlock(BlockType blockType, out Item item)
    {
        return BlockItemCatalog.TryGetItemForBlock(blockType, out item);
    }

    public bool TryGetBlockForItem(Item item, out BlockType blockType)
    {
        return BlockItemCatalog.TryGetBlockForItem(item, out blockType);
    }

    public bool TryGetItemAtlasData(out ItemAtlasDataSO atlasData)
    {
        InitializeItemAtlasLookup();
        atlasData = itemAtlasData;
        return atlasData != null;
    }

    public bool ContainsSlot(Slot target)
    {
        if (target == null || slots == null) return false;

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == target)
                return true;
        }

        return false;
    }

    private void TryPlayAddItemSound()
    {
        if (addItemClip == null || audioSource == null)
            return;

        float now = Time.unscaledTime;
        if (now - lastAddItemSoundTime < addItemSoundMinInterval)
            return;

        audioSource.PlayOneShot(addItemClip, addItemVolume);
        lastAddItemSoundTime = now;
    }

    private bool TryMoveInventorySlotToRange(Slot sourceSlot, int startIndex, int endIndex)
    {
        if (sourceSlot == null || sourceSlot.IsEmpty)
            return false;

        startIndex = Mathf.Clamp(startIndex, 0, slots.Length);
        endIndex = Mathf.Clamp(endIndex, 0, slots.Length);
        if (startIndex >= endIndex)
            return false;

        Item sourceItem = sourceSlot.item;
        int originalAmount = sourceSlot.amount;
        int remaining = originalAmount;

        BeginContentChangeBatch();
        try
        {
            remaining = InsertIntoMatchingSlots(sourceItem, remaining, startIndex, endIndex, sourceSlot);
            remaining = InsertIntoEmptySlots(sourceItem, remaining, startIndex, endIndex, sourceSlot);

            int movedAmount = originalAmount - remaining;
            if (movedAmount > 0)
                sourceSlot.Remove(movedAmount);
        }
        finally
        {
            EndContentChangeBatch();
        }

        return remaining != originalAmount;
    }

    private int InsertIntoMatchingSlots(Item item, int amount, int startIndex, int endIndex, Slot excludedSlot)
    {
        int remaining = amount;

        for (int i = startIndex; i < endIndex && remaining > 0; i++)
        {
            Slot slot = slots[i];
            if (slot == null || slot == excludedSlot || slot.IsEmpty || slot.item != item)
                continue;

            remaining = slot.Add(item, remaining);
        }

        return remaining;
    }

    private int InsertIntoEmptySlots(Item item, int amount, int startIndex, int endIndex, Slot excludedSlot)
    {
        int remaining = amount;

        for (int i = startIndex; i < endIndex && remaining > 0; i++)
        {
            Slot slot = slots[i];
            if (slot == null || slot == excludedSlot || !slot.IsEmpty)
                continue;

            remaining = slot.Add(item, remaining);
        }

        return remaining;
    }

    private int GetSlotIndex(Slot targetSlot)
    {
        if (targetSlot == null || slots == null)
            return -1;

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == targetSlot)
                return i;
        }

        return -1;
    }

    private void EnsureSlotsMappedIfNeeded()
    {
        if (slots == null || slots.Length == 0)
            MapSlotsFromContainer();
    }

    private void BeginContentChangeBatch()
    {
        contentChangeBatchDepth++;
    }

    private void InitializeItemAtlasLookup()
    {
        if (itemAtlasData == null)
            itemAtlasData = Resources.Load<ItemAtlasDataSO>(DefaultItemAtlasDataResourcePath);

        if (itemAtlasData == null)
            return;

        itemAtlasData.InitializeLookup();
    }

    private void EndContentChangeBatch()
    {
        contentChangeBatchDepth = Mathf.Max(0, contentChangeBatchDepth - 1);
        if (contentChangeBatchDepth > 0 || !hasPendingContentChangeNotification)
            return;

        hasPendingContentChangeNotification = false;
        ContentsChanged?.Invoke();
    }
}
