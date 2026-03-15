using System;
using System.Collections.Generic;
using UnityEngine;

public class PlayerInventory : MonoBehaviour
{
    public event Action ContentsChanged;

    public static PlayerInventory Instance { get; private set; }

    [System.Serializable]
    public struct BlockItemMapping
    {
        public BlockType blockType;
        public Item item;
    }

    [Header("Slots Mapping")]
    [SerializeField] private Transform slotsContainer;
    [SerializeField] private bool includeInactiveSlots = true;
    [SerializeField] private bool autoMapOnAwake = true;

    [Header("Inventory UI")]
    [SerializeField] private GameObject inventoryUI;
    [SerializeField] private KeyCode toggleInventoryKey = KeyCode.E;
    [SerializeField] private bool lockCursorWhenClosed = true;

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip addItemClip;
    [Range(0f, 1f)] [SerializeField] private float addItemVolume = 1f;
    [Min(0f)] [SerializeField] private float addItemSoundMinInterval = 0.06f;

    [Header("Block Drop Mapping")]
    [SerializeField] private BlockItemMapping[] blockItemMappings;

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
        if (item == null || amount <= 0) return false;
        if (slots == null || slots.Length == 0) MapSlotsFromContainer();
        if (slots == null || slots.Length == 0) return false;

        int requested = amount;
        int remaining = amount;

        BeginContentChangeBatch();
        try
        {
            for (int i = 0; i < slots.Length && remaining > 0; i++)
            {
                Slot slot = slots[i];
                if (slot == null || slot.IsEmpty) continue;
                remaining = slot.Add(item, remaining);
            }

            for (int i = 0; i < slots.Length && remaining > 0; i++)
            {
                Slot slot = slots[i];
                if (slot == null || !slot.IsEmpty) continue;
                remaining = slot.Add(item, remaining);
            }
        }
        finally
        {
            EndContentChangeBatch();
        }

        int addedAmount = requested - remaining;
        if (addedAmount > 0)
            TryPlayAddItemSound();

        return remaining == 0;
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
        if (slots == null || slots.Length == 0) return false;

        return GetAvailableCapacityFor(item) >= amount;
    }

    public int GetAvailableCapacityFor(Item item)
    {
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
        item = null;
        if (blockItemMappings == null) return false;

        for (int i = 0; i < blockItemMappings.Length; i++)
        {
            if (blockItemMappings[i].blockType == blockType)
            {
                item = blockItemMappings[i].item;
                return item != null;
            }
        }

        return false;
    }

    public bool TryGetBlockForItem(Item item, out BlockType blockType)
    {
        blockType = BlockType.Air;
        if (item == null || blockItemMappings == null) return false;

        for (int i = 0; i < blockItemMappings.Length; i++)
        {
            if (blockItemMappings[i].item == item)
            {
                blockType = blockItemMappings[i].blockType;
                return true;
            }
        }

        return false;
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

    private void BeginContentChangeBatch()
    {
        contentChangeBatchDepth++;
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
