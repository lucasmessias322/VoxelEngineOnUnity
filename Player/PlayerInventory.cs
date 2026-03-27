using System;
using System.Collections.Generic;
using UnityEngine;

public class PlayerInventory : MonoBehaviour
{
    public event Action ContentsChanged;
    private const string DefaultBlockItemMappingResourcePath = "BlockItemMappingSO";

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

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip addItemClip;
    [Range(0f, 1f)] [SerializeField] private float addItemVolume = 1f;
    [Min(0f)] [SerializeField] private float addItemSoundMinInterval = 0.06f;

    [Header("Block Drop Mapping")]
    [SerializeField] private BlockItemMappingSO blockItemMappingSO;

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

        InitializeBlockItemMappingReference();
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
    }

    private void OnValidate()
    {
        InitializeBlockItemMappingReference();
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
            if (slot.IsEmpty) capacity += stackLimit;
            else if (slot.item == item) capacity += Mathf.Max(0, stackLimit - slot.amount);
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

        if (!isOpen)
            Slot.OnInventoryClosed(this);
    }

    private void ApplyCursorState(bool inventoryOpen)
    {
        if (!lockCursorWhenClosed) return;

        Cursor.visible = inventoryOpen;
        Cursor.lockState = inventoryOpen ? CursorLockMode.None : CursorLockMode.Locked;
    }

    private void CollectSlotsRecursive(Transform current, List<Slot> output)
    {
        if (current == null) return;

        if (!includeInactiveSlots && !current.gameObject.activeInHierarchy)
            return;

        Slot slot = current.GetComponent<Slot>();
        if (slot != null)
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
        InitializeBlockItemMappingReference();
        item = null;
        return blockItemMappingSO != null && blockItemMappingSO.TryGetItemForBlock(blockType, out item);
    }

    public bool TryGetBlockForItem(Item item, out BlockType blockType)
    {
        InitializeBlockItemMappingReference();
        blockType = BlockType.Air;
        return blockItemMappingSO != null && blockItemMappingSO.TryGetBlockForItem(item, out blockType);
    }

    public bool TryGetItemAtlasData(out ItemAtlasDataSO atlasData)
    {
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

    private void InitializeBlockItemMappingReference()
    {
        if (blockItemMappingSO != null)
            return;

        blockItemMappingSO = Resources.Load<BlockItemMappingSO>(DefaultBlockItemMappingResourcePath);
    }

    private void InitializeItemAtlasLookup()
    {
        if (itemAtlasData != null)
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
