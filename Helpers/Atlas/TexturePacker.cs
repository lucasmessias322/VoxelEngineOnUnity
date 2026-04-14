using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class TexturePacker
{
    public struct Input
    {
        public string id;
        public int width;
        public int height;
    }

    public struct PackedItem
    {
        public string id;
        public RectInt rect;
    }

    private sealed class Node
    {
        public RectInt rect;
        public Node right;
        public Node down;
        public bool used;
    }

    public bool TryPack(IReadOnlyList<Input> inputs, int atlasWidth, int atlasHeight, out List<PackedItem> packedItems)
    {
        packedItems = null;

        if (inputs == null || inputs.Count == 0 || atlasWidth <= 0 || atlasHeight <= 0)
            return false;

        List<Input> sorted = new List<Input>(inputs.Count);
        for (int i = 0; i < inputs.Count; i++)
        {
            Input input = inputs[i];
            if (input.width <= 0 || input.height <= 0)
                return false;

            sorted.Add(input);
        }

        sorted.Sort(CompareInputs);

        Node root = new Node
        {
            rect = new RectInt(0, 0, atlasWidth, atlasHeight),
            used = false,
            right = null,
            down = null
        };

        packedItems = new List<PackedItem>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            Input input = sorted[i];
            Node node = FindNode(root, input.width, input.height);
            if (node == null)
            {
                packedItems = null;
                return false;
            }

            Node placed = SplitNode(node, input.width, input.height);
            packedItems.Add(new PackedItem
            {
                id = input.id,
                rect = placed.rect
            });
        }

        return true;
    }

    private static int CompareInputs(Input a, Input b)
    {
        int maxA = Mathf.Max(a.width, a.height);
        int maxB = Mathf.Max(b.width, b.height);
        int maxCompare = maxB.CompareTo(maxA);
        if (maxCompare != 0)
            return maxCompare;

        int areaA = a.width * a.height;
        int areaB = b.width * b.height;
        int areaCompare = areaB.CompareTo(areaA);
        if (areaCompare != 0)
            return areaCompare;

        int widthCompare = b.width.CompareTo(a.width);
        if (widthCompare != 0)
            return widthCompare;

        return string.CompareOrdinal(a.id, b.id);
    }

    private static Node FindNode(Node root, int width, int height)
    {
        if (root == null)
            return null;

        if (root.used)
        {
            Node rightNode = FindNode(root.right, width, height);
            if (rightNode != null)
                return rightNode;

            return FindNode(root.down, width, height);
        }

        if (width <= root.rect.width && height <= root.rect.height)
            return root;

        return null;
    }

    private static Node SplitNode(Node node, int width, int height)
    {
        node.used = true;

        RectInt original = node.rect;
        node.rect = new RectInt(original.x, original.y, width, height);

        int remainingWidth = original.width - width;
        int remainingHeight = original.height - height;

        node.right = remainingWidth > 0
            ? new Node
            {
                rect = new RectInt(original.x + width, original.y, remainingWidth, height),
                used = false
            }
            : null;

        node.down = remainingHeight > 0
            ? new Node
            {
                rect = new RectInt(original.x, original.y + height, original.width, remainingHeight),
                used = false
            }
            : null;

        return node;
    }
}

