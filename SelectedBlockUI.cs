using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class SelectedBlockUI : MonoBehaviour
{
    private static readonly string[] BlockTypeNames = System.Enum.GetNames(typeof(BlockType));

    [SerializeField] private BlockSelector blockSelector;
    [SerializeField] private Text blockText;
    private bool wasShowingBlock;
    private BlockType lastShownBlock = BlockType.Air;

    void Update()
    {
        UpdateText();
    }

    void UpdateText()
    {
        if (blockText == null)
            return;

        if (blockSelector == null || !blockSelector.HasBlock)
        {
            if (wasShowingBlock)
            {
                blockText.text = string.Empty;
                wasShowingBlock = false;
            }
            return;
        }

        BlockType current = blockSelector.CurrentBlock;
        if (wasShowingBlock && current == lastShownBlock)
            return;

        blockText.text = GetBlockTypeLabel(current);
        lastShownBlock = current;
        wasShowingBlock = true;
    }

    private static string GetBlockTypeLabel(BlockType blockType)
    {
        int index = (int)blockType;
        if (index >= 0 && index < BlockTypeNames.Length)
            return BlockTypeNames[index];

        return blockType.ToString();
    }
}
