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
        if (blockSelector == null || !blockSelector.HasBlock)
        {
            blockText.text = "";
            return;
        }

        blockText.text = blockSelector.CurrentBlock.ToString();
    }
}
