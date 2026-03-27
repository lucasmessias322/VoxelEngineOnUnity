using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class Slot : MonoBehaviour, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler, IPointerEnterHandler, IPointerExitHandler
{
    public delegate bool BeforeTakeFromSlotHandler(Slot slot, Item currentItem, int currentAmount, int amountToTake);
    public delegate bool QuickTransferRequestedHandler(Slot slot);
    public delegate bool BeforePlaceIntoSlotHandler(Slot slot, Item incomingItem, int incomingAmount);

    private const string IconChildName = "IconImage";
    private const string AmountChildName = "amount";
    private static readonly Vector2 DragVisualOffset = new Vector2(22f, -22f);

    [Header("UI")]
    [SerializeField] private Image iconImage;
    [SerializeField] private bool hideIconWhenEmpty = true;
    [SerializeField] private TMP_Text amountText;
    [SerializeField] private Text amountTextLegacy;
    [SerializeField] private bool hideAmountWhenOneOrEmpty = true;
    [SerializeField] private bool requireSlotToBeMappedInInventory = true;
    [SerializeField] private bool allowManualPickup = true;
    [SerializeField] private bool allowManualInsert = true;
    [SerializeField] private bool allowRightClickInteraction = true;

    [Header("Runtime")]
    public int id;
    public Item item;
    [Min(0)] public int amount;

    public event System.Action<Slot> SlotChanged;
    public event BeforeTakeFromSlotHandler BeforeTakeFromSlot;
    public event System.Action<Slot, Item, int> AfterTakeFromSlot;
    public event QuickTransferRequestedHandler QuickTransferRequested;
    public event BeforePlaceIntoSlotHandler BeforePlaceIntoSlot;

    private static Item carriedItem;
    private static int carriedAmount;

    private static Canvas dragVisualCanvas;
    private static RectTransform dragVisualRoot;
    private static Image dragVisualIcon;
    private static TMP_Text dragVisualAmountText;
    private static Slot hoveredSlot;
    private static Slot pendingDropTarget;
    private static bool pendingDropResolution;

    private bool draggingFromThisSlot;

    public bool IsEmpty => item == null || amount <= 0;
    public bool RequiresInventorySlotMapping => requireSlotToBeMappedInInventory;
    private static bool HasCarriedStack => carriedItem != null && carriedAmount > 0;

    private void Awake()
    {
        TryAutoBindIconImage();
        TryAutoBindAmountText();
        RefreshUI();
    }

    private void OnValidate()
    {
        TryAutoBindIconImage();
        TryAutoBindAmountText();
        RefreshUI();
    }

    private void Update()
    {
        PlayerInventory inventory = PlayerInventory.Instance;

        if (HasCarriedStack)
        {
            UpdateDragVisual();

            if (inventory != null && inventory.IsInventoryOpen && CanUseSlotWithCurrentInventory(inventory))
            {
                EnsureDragVisual(this);
                UpdateDragVisualPosition(Input.mousePosition);
            }
        }
        else
        {
            HideDragVisual();
        }

        if (inventory != null && !inventory.IsInventoryOpen && HasCarriedStack)
            OnInventoryClosed(inventory);

        TryResolvePendingDragDrop();
    }

    public void SetIndex(int newIndex)
    {
        id = newIndex;
    }

    public void SetRequireInventorySlotMapping(bool required)
    {
        requireSlotToBeMappedInInventory = required;
    }

    public void SetManualInteraction(bool canPickup, bool canInsert, bool canUseRightClick = true)
    {
        allowManualPickup = canPickup;
        allowManualInsert = canInsert;
        allowRightClickInteraction = canUseRightClick;
    }

    public void SetContents(Item newItem, int newAmount)
    {
        if (newItem == null || newAmount <= 0)
        {
            item = null;
            amount = 0;
        }
        else
        {
            item = newItem;
            amount = Mathf.Max(0, newAmount);
        }

        RefreshUI();
        NotifyOwningInventoryChanged();
    }

    public bool CanStack(Item target)
    {
        if (target == null) return false;
        if (IsEmpty) return true;
        if (item != target) return false;
        return amount < Mathf.Max(1, target.maxStack);
    }

    public int Add(Item target, int value)
    {
        if (target == null || value <= 0) return value;
        int stackLimit = Mathf.Max(1, target.maxStack);

        if (IsEmpty)
        {
            item = target;
            int toStore = Mathf.Min(value, stackLimit);
            amount = toStore;
            RefreshUI();
            NotifyOwningInventoryChanged();
            return value - toStore;
        }

        if (item != target) return value;

        int freeSpace = Mathf.Max(0, stackLimit - amount);
        int addNow = Mathf.Min(value, freeSpace);
        if (addNow <= 0)
            return value;

        amount += addNow;
        RefreshUI();
        NotifyOwningInventoryChanged();
        return value - addNow;
    }

    public int Remove(int value)
    {
        if (value <= 0 || IsEmpty) return 0;

        int removed = Mathf.Min(value, amount);
        amount -= removed;
        if (amount <= 0)
        {
            item = null;
            amount = 0;
        }

        RefreshUI();
        NotifyOwningInventoryChanged();
        return removed;
    }

    public void Clear()
    {
        if (IsEmpty)
            return;

        item = null;
        amount = 0;
        RefreshUI();
        NotifyOwningInventoryChanged();
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (!CanInteractWithInventory())
            return;

        if (eventData.button == PointerEventData.InputButton.Right && !allowRightClickInteraction)
            return;

        if (eventData.button == PointerEventData.InputButton.Left)
        {
            if (IsQuickTransferModifierHeld())
            {
                HandleQuickTransferClick();
                return;
            }

            HandleLeftClick();
            return;
        }

        if (eventData.button == PointerEventData.InputButton.Right)
            HandleRightClick();
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left)
            return;

        if (!CanInteractWithInventory())
            return;

        draggingFromThisSlot = false;

        if (!allowManualPickup && !HasCarriedStack)
            return;

        if (!HasCarriedStack && !IsEmpty)
            PickupFromSlot(amount);

        if (!HasCarriedStack)
            return;

        pendingDropResolution = false;
        pendingDropTarget = null;
        draggingFromThisSlot = true;
        EnsureDragVisual(this);
        UpdateDragVisual();
        UpdateDragVisualPosition(eventData.position);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!draggingFromThisSlot || !HasCarriedStack)
            return;

        UpdateDragVisualPosition(eventData.position);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (!draggingFromThisSlot)
            return;

        draggingFromThisSlot = false;
        UpdateDragVisualPosition(eventData.position);
        pendingDropTarget = ResolveSlotFromGameObject(eventData.pointerEnter);
        if (pendingDropTarget == null)
            pendingDropTarget = hoveredSlot;

        pendingDropResolution = HasCarriedStack;
    }

    public void OnDrop(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left)
            return;

        if (!CanInteractWithInventory() || !HasCarriedStack || !allowManualInsert)
            return;

        PlaceCarriedStackWithLeftClick();
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        hoveredSlot = this;
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (hoveredSlot == this)
            hoveredSlot = null;
    }

    private bool CanInteractWithInventory()
    {
        PlayerInventory inventory = PlayerInventory.Instance;
        if (inventory == null || !inventory.IsInventoryOpen)
            return false;

        return CanUseSlotWithCurrentInventory(inventory);
    }

    private bool CanUseSlotWithCurrentInventory(PlayerInventory inventory)
    {
        if (inventory == null)
            return false;

        return !requireSlotToBeMappedInInventory || inventory.ContainsSlot(this);
    }

    private void HandleLeftClick()
    {
        if (!HasCarriedStack)
        {
            if (!allowManualPickup || IsEmpty)
                return;

            if (!IsEmpty)
                PickupFromSlot(amount);

            return;
        }

        if (!allowManualInsert)
            return;

        PlaceCarriedStackWithLeftClick();
    }

    private void HandleQuickTransferClick()
    {
        if (HasCarriedStack || IsEmpty)
            return;

        if (TryHandleQuickTransfer())
            return;

        if (!allowManualPickup)
            return;

        PlayerInventory inventory = PlayerInventory.Instance;
        if (inventory != null && inventory.TryQuickMoveSlot(this))
            return;
    }

    private void HandleRightClick()
    {
        if (!allowRightClickInteraction)
            return;

        if (!HasCarriedStack)
        {
            if (!allowManualPickup || IsEmpty)
                return;

            int toTake = Mathf.CeilToInt(amount * 0.5f);
            PickupFromSlot(toTake);
            return;
        }

        if (!allowManualInsert)
            return;

        PlaceSingleFromCarriedStack();
    }

    private void PickupFromSlot(int amountToTake)
    {
        if (IsEmpty || amountToTake <= 0)
            return;

        Item itemToTake = item;
        int currentAmount = amount;
        int take = Mathf.Clamp(amountToTake, 1, amount);
        if (!CanTakeFromSlot(itemToTake, currentAmount, take))
            return;

        carriedItem = itemToTake;
        carriedAmount = take;
        Remove(take);
        AfterTakeFromSlot?.Invoke(this, itemToTake, take);
        EnsureDragVisual(this);
        UpdateDragVisual();
    }

    private void PlaceCarriedStackWithLeftClick()
    {
        if (!HasCarriedStack)
            return;

        if (!CanPlaceIntoSlot(carriedItem, carriedAmount))
            return;

        if (IsEmpty)
        {
            item = carriedItem;
            amount = carriedAmount;
            ClearCarriedStack();
            RefreshUI();
            NotifyOwningInventoryChanged();
            return;
        }

        if (item == carriedItem)
        {
            int stackLimit = Mathf.Max(1, item.maxStack);
            int free = Mathf.Max(0, stackLimit - amount);
            if (free <= 0)
                return;

            int toMove = Mathf.Min(free, carriedAmount);
            amount += toMove;
            carriedAmount -= toMove;
            if (carriedAmount <= 0)
                ClearCarriedStack();
            else
                UpdateDragVisual();

            RefreshUI();
            NotifyOwningInventoryChanged();
            return;
        }

        Item targetItem = item;
        int targetAmount = amount;
        item = carriedItem;
        amount = carriedAmount;
        carriedItem = targetItem;
        carriedAmount = targetAmount;
        RefreshUI();
        UpdateDragVisual();
        NotifyOwningInventoryChanged();
    }

    private void PlaceSingleFromCarriedStack()
    {
        if (!HasCarriedStack)
            return;

        if (!CanPlaceIntoSlot(carriedItem, 1))
            return;

        if (IsEmpty)
        {
            item = carriedItem;
            amount = 1;
            carriedAmount -= 1;
            if (carriedAmount <= 0)
                ClearCarriedStack();
            else
                UpdateDragVisual();

            RefreshUI();
            NotifyOwningInventoryChanged();
            return;
        }

        if (item != carriedItem)
            return;

        int stackLimit = Mathf.Max(1, item.maxStack);
        if (amount >= stackLimit)
            return;

        amount += 1;
        carriedAmount -= 1;
        if (carriedAmount <= 0)
            ClearCarriedStack();
        else
            UpdateDragVisual();

        RefreshUI();
        NotifyOwningInventoryChanged();
    }

    public static void OnInventoryClosed(PlayerInventory inventory)
    {
        if (!HasCarriedStack || inventory == null)
        {
            ClearCarriedStack();
            return;
        }

        Slot[] invSlots = inventory.Slots;
        if (invSlots == null || invSlots.Length == 0)
        {
            ClearCarriedStack();
            return;
        }

        int remaining = inventory.InsertItem(carriedItem, carriedAmount);

        carriedAmount = remaining;
        if (carriedAmount <= 0)
            ClearCarriedStack();
        else
            UpdateDragVisual();
    }

    private static void ClearCarriedStack()
    {
        carriedItem = null;
        carriedAmount = 0;
        pendingDropResolution = false;
        pendingDropTarget = null;
        HideDragVisual();
    }

    private static void EnsureDragVisual(Slot contextSlot)
    {
        if (contextSlot == null)
            return;

        Canvas canvas = contextSlot.GetComponentInParent<Canvas>();
        if (canvas == null)
            return;

        if (dragVisualRoot != null && dragVisualCanvas == canvas)
            return;

        dragVisualCanvas = canvas;

        if (dragVisualRoot == null)
        {
            GameObject rootObject = new GameObject("CarriedItemVisual", typeof(RectTransform), typeof(CanvasGroup));
            dragVisualRoot = rootObject.GetComponent<RectTransform>();
            dragVisualRoot.sizeDelta = new Vector2(52f, 52f);
            dragVisualRoot.pivot = new Vector2(0f, 1f);
            dragVisualRoot.anchorMin = new Vector2(0.5f, 0.5f);
            dragVisualRoot.anchorMax = new Vector2(0.5f, 0.5f);

            CanvasGroup group = rootObject.GetComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            GameObject iconObject = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            RectTransform iconRect = iconObject.GetComponent<RectTransform>();
            iconRect.SetParent(dragVisualRoot, false);
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = Vector2.zero;
            iconRect.offsetMax = Vector2.zero;
            dragVisualIcon = iconObject.GetComponent<Image>();
            dragVisualIcon.raycastTarget = false;

            GameObject amountObject = new GameObject("Amount", typeof(RectTransform), typeof(TextMeshProUGUI));
            RectTransform amountRect = amountObject.GetComponent<RectTransform>();
            amountRect.SetParent(dragVisualRoot, false);
            amountRect.anchorMin = Vector2.zero;
            amountRect.anchorMax = Vector2.one;
            amountRect.offsetMin = new Vector2(2f, 1f);
            amountRect.offsetMax = new Vector2(-2f, -2f);
            dragVisualAmountText = amountObject.GetComponent<TextMeshProUGUI>();
            dragVisualAmountText.alignment = TextAlignmentOptions.BottomRight;
            dragVisualAmountText.raycastTarget = false;
            dragVisualAmountText.fontSize = 24f;
            dragVisualAmountText.color = Color.white;
        }

        dragVisualRoot.SetParent(canvas.transform, false);
        dragVisualRoot.SetAsLastSibling();
    }

    private static void UpdateDragVisual()
    {
        if (dragVisualRoot == null || dragVisualIcon == null || dragVisualAmountText == null)
            return;

        PlayerInventory inventory = PlayerInventory.Instance;
        bool canShow = HasCarriedStack && inventory != null && inventory.IsInventoryOpen;
        dragVisualRoot.gameObject.SetActive(canShow);
        if (!canShow)
            return;

        dragVisualIcon.sprite = ResolveItemIcon(carriedItem);
        dragVisualIcon.enabled = dragVisualIcon.sprite != null;

        bool showAmount = carriedAmount > 1;
        dragVisualAmountText.text = showAmount ? carriedAmount.ToString() : string.Empty;
        dragVisualAmountText.enabled = showAmount;
    }

    private static void HideDragVisual()
    {
        if (dragVisualRoot != null && dragVisualRoot.gameObject.activeSelf)
            dragVisualRoot.gameObject.SetActive(false);
    }

    private void TryResolvePendingDragDrop()
    {
        if (!pendingDropResolution || Input.GetMouseButton(0))
            return;

        pendingDropResolution = false;

        if (!HasCarriedStack)
        {
            pendingDropTarget = null;
            return;
        }

        Slot target = pendingDropTarget != null ? pendingDropTarget : hoveredSlot;
        pendingDropTarget = null;

        if (target == null || !target.CanInteractWithInventory() || !target.allowManualInsert)
            return;

        target.PlaceCarriedStackWithLeftClick();
    }

    private static void UpdateDragVisualPosition(Vector2 screenPosition)
    {
        if (dragVisualRoot == null || dragVisualCanvas == null)
            return;

        RectTransform canvasRect = dragVisualCanvas.transform as RectTransform;
        if (canvasRect == null)
            return;

        Camera cameraRef = dragVisualCanvas.renderMode == RenderMode.ScreenSpaceOverlay
            ? null
            : dragVisualCanvas.worldCamera;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screenPosition, cameraRef, out Vector2 localPos))
            return;

        dragVisualRoot.anchoredPosition = localPos + DragVisualOffset;
    }

    private static Sprite ResolveItemIcon(Item currentItem)
    {
        return ItemIconResolver.ResolveForUI(currentItem);
    }

    private static Slot ResolveSlotFromGameObject(GameObject currentObject)
    {
        if (currentObject == null)
            return null;

        return currentObject.GetComponentInParent<Slot>();
    }

    private void TryAutoBindIconImage()
    {
        if (iconImage != null) return;

        Transform iconTransform = FindChildByName(transform, IconChildName);
        if (iconTransform != null)
            iconImage = iconTransform.GetComponent<Image>();
    }

    private void TryAutoBindAmountText()
    {
        if (amountText != null || amountTextLegacy != null) return;

        Transform amountTransform = FindChildByName(transform, AmountChildName);
        if (amountTransform == null) return;

        amountText = amountTransform.GetComponent<TMP_Text>();
        if (amountText == null)
            amountTextLegacy = amountTransform.GetComponent<Text>();
    }

    private static Transform FindChildByName(Transform parent, string childName)
    {
        if (parent == null) return null;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == childName)
                return child;

            Transform nested = FindChildByName(child, childName);
            if (nested != null)
                return nested;
        }

        return null;
    }

    public void RefreshUI()
    {
        RefreshIcon();
        RefreshAmount();
    }

    public void RefreshIcon()
    {
        Sprite iconToShow = !IsEmpty ? ItemIconResolver.ResolveForUI(item) : null;
        bool hasIcon = iconToShow != null;

        if (iconImage != null)
        {
            iconImage.sprite = hasIcon ? iconToShow : null;

            if (hideIconWhenEmpty)
                iconImage.enabled = hasIcon;
            else
                iconImage.enabled = true;
        }
    }

    public void RefreshAmount()
    {
        bool hasAmount = !IsEmpty && amount > 0;
        bool shouldShow = hasAmount && (!hideAmountWhenOneOrEmpty || amount > 1);
        string value = shouldShow ? amount.ToString() : string.Empty;

        if (amountText != null)
        {
            amountText.text = value;
            if (hideAmountWhenOneOrEmpty)
                amountText.enabled = shouldShow;
            else
                amountText.enabled = true;
        }

        if (amountTextLegacy != null)
        {
            amountTextLegacy.text = value;
            if (hideAmountWhenOneOrEmpty)
                amountTextLegacy.enabled = shouldShow;
            else
                amountTextLegacy.enabled = true;
        }
    }

    private void NotifyOwningInventoryChanged()
    {
        SlotChanged?.Invoke(this);

        PlayerInventory inventory = PlayerInventory.Instance;
        if (inventory != null && inventory.ContainsSlot(this))
            inventory.NotifyContentsChanged();
    }

    private bool CanTakeFromSlot(Item currentItem, int currentAmount, int amountToTake)
    {
        if (BeforeTakeFromSlot == null)
            return true;

        System.Delegate[] listeners = BeforeTakeFromSlot.GetInvocationList();
        for (int i = 0; i < listeners.Length; i++)
        {
            BeforeTakeFromSlotHandler handler = (BeforeTakeFromSlotHandler)listeners[i];
            if (!handler.Invoke(this, currentItem, currentAmount, amountToTake))
                return false;
        }

        return true;
    }

    private bool TryHandleQuickTransfer()
    {
        if (QuickTransferRequested == null)
            return false;

        System.Delegate[] listeners = QuickTransferRequested.GetInvocationList();
        for (int i = 0; i < listeners.Length; i++)
        {
            QuickTransferRequestedHandler handler = (QuickTransferRequestedHandler)listeners[i];
            if (handler.Invoke(this))
                return true;
        }

        return false;
    }

    private bool CanPlaceIntoSlot(Item incomingItem, int incomingAmount)
    {
        if (BeforePlaceIntoSlot == null)
            return true;

        System.Delegate[] listeners = BeforePlaceIntoSlot.GetInvocationList();
        for (int i = 0; i < listeners.Length; i++)
        {
            BeforePlaceIntoSlotHandler handler = (BeforePlaceIntoSlotHandler)listeners[i];
            if (!handler.Invoke(this, incomingItem, incomingAmount))
                return false;
        }

        return true;
    }

    private static bool IsQuickTransferModifierHeld()
    {
        return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    }
}
