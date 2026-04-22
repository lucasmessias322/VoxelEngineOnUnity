using UnityEngine;
using UnityEngine.UI;

public class SelectedBlockUI : MonoBehaviour
{
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
        return System.Enum.GetName(typeof(BlockType), blockType) ?? blockType.ToString();
    }
}
