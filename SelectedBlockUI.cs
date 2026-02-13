using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class SelectedBlockUI : MonoBehaviour
{
    [SerializeField] private BlockSelector blockSelector;
    [SerializeField] private Text blockText;

    void Update()
    {
        UpdateText();
    }

    void UpdateText()
    {
        BlockType block = blockSelector.CurrentBlock;

        // if (block == BlockType.Air )
        // {
        //     blockText.text = "";
        //     return;
        // }

        blockText.text = block.ToString();
    }
}
