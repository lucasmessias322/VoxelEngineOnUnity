using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

[Serializable]
public struct TerrainSplinePoint
{
    public float location;
    public float value;
    public float derivative;
    public bool splitTangents;
    public float derivativeIn;
    public float derivativeOut;

    public TerrainSplinePoint(float location, float value, float derivative = 0f)
    {
        this.location = location;
        this.value = value;
        this.derivative = derivative;
        splitTangents = false;
        derivativeIn = derivative;
        derivativeOut = derivative;
    }

    public TerrainSplinePoint(float location, float value, float derivativeIn, float derivativeOut)
    {
        this.location = location;
        this.value = value;
        derivative = (derivativeIn + derivativeOut) * 0.5f;
        splitTangents = math.abs(derivativeIn - derivativeOut) > 1e-4f;
        this.derivativeIn = derivativeIn;
        this.derivativeOut = derivativeOut;
    }

    public float GetDerivativeIn()
    {
        return splitTangents ? derivativeIn : derivative;
    }

    public float GetDerivativeOut()
    {
        return splitTangents ? derivativeOut : derivative;
    }

    public TerrainSplinePoint Sanitized()
    {
        TerrainSplinePoint sanitized = this;
        float inDerivative = splitTangents ? derivativeIn : derivative;
        float outDerivative = splitTangents ? derivativeOut : derivative;

        sanitized.derivativeIn = inDerivative;
        sanitized.derivativeOut = outDerivative;
        sanitized.derivative = math.abs(inDerivative - outDerivative) <= 1e-4f
            ? inDerivative
            : (inDerivative + outDerivative) * 0.5f;
        sanitized.splitTangents = math.abs(inDerivative - outDerivative) > 1e-4f;
        return sanitized;
    }
}

[Serializable]
public struct TerrainSpline
{
    public const int MaxPoints = 16;

    public bool enabled;
    public float smoothing;
    public int pointCount;
    public TerrainSplinePoint point0;
    public TerrainSplinePoint point1;
    public TerrainSplinePoint point2;
    public TerrainSplinePoint point3;
    public TerrainSplinePoint point4;
    public TerrainSplinePoint point5;
    public TerrainSplinePoint point6;
    public TerrainSplinePoint point7;
    public TerrainSplinePoint point8;
    public TerrainSplinePoint point9;
    public TerrainSplinePoint point10;
    public TerrainSplinePoint point11;
    public TerrainSplinePoint point12;
    public TerrainSplinePoint point13;
    public TerrainSplinePoint point14;
    public TerrainSplinePoint point15;

    public static TerrainSpline Create(params TerrainSplinePoint[] points)
    {
        TerrainSpline spline = new TerrainSpline
        {
            enabled = points != null && points.Length > 0,
            smoothing = 1f,
            pointCount = math.clamp(points?.Length ?? 0, 0, MaxPoints)
        };

        for (int i = 0; i < spline.pointCount; i++)
            spline.SetPoint(i, points[i]);

        return spline;
    }

    public TerrainSplinePoint GetPoint(int index)
    {
        switch (index)
        {
            case 0: return point0;
            case 1: return point1;
            case 2: return point2;
            case 3: return point3;
            case 4: return point4;
            case 5: return point5;
            case 6: return point6;
            case 7: return point7;
            case 8: return point8;
            case 9: return point9;
            case 10: return point10;
            case 11: return point11;
            case 12: return point12;
            case 13: return point13;
            case 14: return point14;
            case 15: return point15;
            default: return point15;
        }
    }

    public void SetPoint(int index, TerrainSplinePoint point)
    {
        switch (index)
        {
            case 0: point0 = point; break;
            case 1: point1 = point; break;
            case 2: point2 = point; break;
            case 3: point3 = point; break;
            case 4: point4 = point; break;
            case 5: point5 = point; break;
            case 6: point6 = point; break;
            case 7: point7 = point; break;
            case 8: point8 = point; break;
            case 9: point9 = point; break;
            case 10: point10 = point; break;
            case 11: point11 = point; break;
            case 12: point12 = point; break;
            case 13: point13 = point; break;
            case 14: point14 = point; break;
            case 15: point15 = point; break;
        }
    }

    public TerrainSpline Sanitized()
    {
        TerrainSpline sanitized = this;
        sanitized.smoothing = math.saturate(smoothing);
        sanitized.pointCount = math.clamp(pointCount, 0, MaxPoints);
        if (sanitized.pointCount <= 0)
        {
            sanitized.enabled = false;
            return sanitized;
        }

        TerrainSplinePoint[] points = new TerrainSplinePoint[sanitized.pointCount];
        for (int i = 0; i < sanitized.pointCount; i++)
            points[i] = sanitized.GetPoint(i).Sanitized();

        Array.Sort(points, CompareByLocation);

        for (int i = 0; i < points.Length; i++)
        {
            TerrainSplinePoint point = points[i];
            if (i > 0 && point.location <= points[i - 1].location)
                point.location = points[i - 1].location + 1e-4f;

            sanitized.SetPoint(i, point);
        }

        return sanitized;
    }

    private static int CompareByLocation(TerrainSplinePoint a, TerrainSplinePoint b)
    {
        return a.location.CompareTo(b.location);
    }
}

[Serializable]
public enum TerrainSplineInput : byte
{
    Continents = 0,
    Erosion = 1,
    Ridges = 2,
    RidgesFolded = 3
}

[Serializable]
public enum TerrainSplineGraphTarget : byte
{
    Offset = 0,
    Factor = 1,
    Jaggedness = 2
}

public struct TerrainShapePoint
{
    public float continents;
    public float erosion;
    public float ridges;
    public float ridgesFolded;

    [BurstCompile]
    public float GetInputValue(TerrainSplineInput input)
    {
        switch (input)
        {
            case TerrainSplineInput.Continents:
                return continents;
            case TerrainSplineInput.Erosion:
                return erosion;
            case TerrainSplineInput.Ridges:
                return ridges;
            case TerrainSplineInput.RidgesFolded:
            default:
                return ridgesFolded;
        }
    }
}

[Serializable]
public struct TerrainSplineGraphPoint
{
    public float location;
    public float value;
    public float derivative;
    public bool splitTangents;
    public float derivativeIn;
    public float derivativeOut;
    public int childNodeIndex;

    public float GetDerivativeIn()
    {
        return splitTangents ? derivativeIn : derivative;
    }

    public float GetDerivativeOut()
    {
        return splitTangents ? derivativeOut : derivative;
    }

    public TerrainSplineGraphPoint Sanitized()
    {
        TerrainSplineGraphPoint sanitized = this;
        float inDerivative = splitTangents ? derivativeIn : derivative;
        float outDerivative = splitTangents ? derivativeOut : derivative;

        sanitized.derivativeIn = inDerivative;
        sanitized.derivativeOut = outDerivative;
        sanitized.derivative = math.abs(inDerivative - outDerivative) <= 1e-4f
            ? inDerivative
            : (inDerivative + outDerivative) * 0.5f;
        sanitized.splitTangents = math.abs(inDerivative - outDerivative) > 1e-4f;
        sanitized.childNodeIndex = math.max(-1, childNodeIndex);
        return sanitized;
    }

    public static TerrainSplineGraphPoint FromLegacy(in TerrainSplinePoint point)
    {
        TerrainSplinePoint sanitizedPoint = point.Sanitized();
        return new TerrainSplineGraphPoint
        {
            location = sanitizedPoint.location,
            value = sanitizedPoint.value,
            derivative = sanitizedPoint.derivative,
            splitTangents = sanitizedPoint.splitTangents,
            derivativeIn = sanitizedPoint.derivativeIn,
            derivativeOut = sanitizedPoint.derivativeOut,
            childNodeIndex = -1
        };
    }
}

[Serializable]
public struct TerrainSplineGraphNode
{
    public const int MaxPoints = TerrainSpline.MaxPoints;

    public bool enabled;
    public TerrainSplineInput input;
    public float smoothing;
    public int pointCount;
    public TerrainSplineGraphPoint point0;
    public TerrainSplineGraphPoint point1;
    public TerrainSplineGraphPoint point2;
    public TerrainSplineGraphPoint point3;
    public TerrainSplineGraphPoint point4;
    public TerrainSplineGraphPoint point5;
    public TerrainSplineGraphPoint point6;
    public TerrainSplineGraphPoint point7;
    public TerrainSplineGraphPoint point8;
    public TerrainSplineGraphPoint point9;
    public TerrainSplineGraphPoint point10;
    public TerrainSplineGraphPoint point11;
    public TerrainSplineGraphPoint point12;
    public TerrainSplineGraphPoint point13;
    public TerrainSplineGraphPoint point14;
    public TerrainSplineGraphPoint point15;

    public TerrainSplineGraphPoint GetPoint(int index)
    {
        switch (index)
        {
            case 0: return point0;
            case 1: return point1;
            case 2: return point2;
            case 3: return point3;
            case 4: return point4;
            case 5: return point5;
            case 6: return point6;
            case 7: return point7;
            case 8: return point8;
            case 9: return point9;
            case 10: return point10;
            case 11: return point11;
            case 12: return point12;
            case 13: return point13;
            case 14: return point14;
            case 15: return point15;
            default: return point15;
        }
    }

    public void SetPoint(int index, TerrainSplineGraphPoint point)
    {
        switch (index)
        {
            case 0: point0 = point; break;
            case 1: point1 = point; break;
            case 2: point2 = point; break;
            case 3: point3 = point; break;
            case 4: point4 = point; break;
            case 5: point5 = point; break;
            case 6: point6 = point; break;
            case 7: point7 = point; break;
            case 8: point8 = point; break;
            case 9: point9 = point; break;
            case 10: point10 = point; break;
            case 11: point11 = point; break;
            case 12: point12 = point; break;
            case 13: point13 = point; break;
            case 14: point14 = point; break;
            case 15: point15 = point; break;
        }
    }

    public TerrainSplineGraphNode Sanitized()
    {
        TerrainSplineGraphNode sanitized = this;
        sanitized.smoothing = math.saturate(smoothing);
        sanitized.pointCount = math.clamp(pointCount, 0, MaxPoints);
        if (sanitized.pointCount <= 0)
        {
            sanitized.enabled = false;
            return sanitized;
        }

        TerrainSplineGraphPoint[] points = new TerrainSplineGraphPoint[sanitized.pointCount];
        for (int i = 0; i < sanitized.pointCount; i++)
            points[i] = sanitized.GetPoint(i).Sanitized();

        Array.Sort(points, CompareByLocation);

        for (int i = 0; i < points.Length; i++)
        {
            TerrainSplineGraphPoint point = points[i];
            if (i > 0 && point.location <= points[i - 1].location)
                point.location = points[i - 1].location + 1e-4f;

            sanitized.SetPoint(i, point);
        }

        return sanitized;
    }

    public static TerrainSplineGraphNode FromLegacy(in TerrainSpline spline, TerrainSplineInput input)
    {
        TerrainSpline sanitizedSpline = spline.Sanitized();
        TerrainSplineGraphNode node = new TerrainSplineGraphNode
        {
            enabled = sanitizedSpline.enabled,
            input = input,
            smoothing = sanitizedSpline.smoothing,
            pointCount = sanitizedSpline.pointCount
        };

        for (int i = 0; i < node.pointCount; i++)
            node.SetPoint(i, TerrainSplineGraphPoint.FromLegacy(sanitizedSpline.GetPoint(i)));

        return node;
    }

    private static int CompareByLocation(TerrainSplineGraphPoint a, TerrainSplineGraphPoint b)
    {
        return a.location.CompareTo(b.location);
    }
}

[Serializable]
public struct TerrainSplineShaperSettings
{
    public const int MaxGraphNodes = 16;

    public bool enabled;
    [HideInInspector] public int graphNodeCount;
    [HideInInspector] public int offsetRootNodeIndex;
    [HideInInspector] public int factorRootNodeIndex;
    [HideInInspector] public int jaggednessRootNodeIndex;
    [HideInInspector] public TerrainSplineGraphNode node0;
    [HideInInspector] public TerrainSplineGraphNode node1;
    [HideInInspector] public TerrainSplineGraphNode node2;
    [HideInInspector] public TerrainSplineGraphNode node3;
    [HideInInspector] public TerrainSplineGraphNode node4;
    [HideInInspector] public TerrainSplineGraphNode node5;
    [HideInInspector] public TerrainSplineGraphNode node6;
    [HideInInspector] public TerrainSplineGraphNode node7;
    [HideInInspector] public TerrainSplineGraphNode node8;
    [HideInInspector] public TerrainSplineGraphNode node9;
    [HideInInspector] public TerrainSplineGraphNode node10;
    [HideInInspector] public TerrainSplineGraphNode node11;
    [HideInInspector] public TerrainSplineGraphNode node12;
    [HideInInspector] public TerrainSplineGraphNode node13;
    [HideInInspector] public TerrainSplineGraphNode node14;
    [HideInInspector] public TerrainSplineGraphNode node15;
    public TerrainSpline offsetSpline;
    public TerrainSpline factorSpline;
    public TerrainSpline jaggednessSpline;

    public bool HasAnyControlPoints =>
        math.clamp(graphNodeCount, 0, MaxGraphNodes) > 0 ||
        math.clamp(offsetSpline.pointCount, 0, TerrainSpline.MaxPoints) > 0 ||
        math.clamp(factorSpline.pointCount, 0, TerrainSpline.MaxPoints) > 0 ||
        math.clamp(jaggednessSpline.pointCount, 0, TerrainSpline.MaxPoints) > 0;

    public static TerrainSplineShaperSettings Disabled => new TerrainSplineShaperSettings
    {
        enabled = false,
        graphNodeCount = 0,
        offsetRootNodeIndex = -1,
        factorRootNodeIndex = -1,
        jaggednessRootNodeIndex = -1,
        offsetSpline = default,
        factorSpline = default,
        jaggednessSpline = default
    };

    public static TerrainSplineShaperSettings MinecraftModernDefault => new TerrainSplineShaperSettings
    {
        enabled = true,
        graphNodeCount = 0,
        offsetRootNodeIndex = -1,
        factorRootNodeIndex = -1,
        jaggednessRootNodeIndex = -1,
        offsetSpline = CreateWithSmoothing(
            0.34f,
            new TerrainSplinePoint(-1.00f, -0.86f, 0.00f),
            new TerrainSplinePoint(-0.82f, -0.72f, 0.12f),
            new TerrainSplinePoint(-0.50f, -0.30f, 0.38f),
            new TerrainSplinePoint(-0.12f, -0.02f, 0.18f),
            new TerrainSplinePoint(0.18f, 0.16f, 0.26f),
            new TerrainSplinePoint(0.44f, 0.44f, 0.54f),
            new TerrainSplinePoint(0.72f, 0.90f, 0.48f),
            new TerrainSplinePoint(1.00f, 1.12f, 0.00f)),
        factorSpline = CreateWithSmoothing(
            0.26f,
            new TerrainSplinePoint(-1.00f, 0.05f, 0.00f),
            new TerrainSplinePoint(-0.56f, 0.12f, 0.08f),
            new TerrainSplinePoint(-0.18f, 0.30f, 0.22f),
            new TerrainSplinePoint(0.16f, 0.58f, 0.18f),
            new TerrainSplinePoint(0.42f, 0.84f, 0.10f),
            new TerrainSplinePoint(1.00f, 1.00f, 0.00f)),
        jaggednessSpline = CreateWithSmoothing(
            0.18f,
            new TerrainSplinePoint(0.00f, 0.00f, 0.00f),
            new TerrainSplinePoint(0.24f, 0.02f, 0.00f),
            new TerrainSplinePoint(0.46f, 0.18f, 0.10f),
            new TerrainSplinePoint(0.64f, 0.52f, 0.24f),
            new TerrainSplinePoint(0.80f, 0.98f, 0.16f),
            new TerrainSplinePoint(1.00f, 1.36f, 0.00f))
    };

    public TerrainSplineGraphNode GetNode(int index)
    {
        switch (index)
        {
            case 0: return node0;
            case 1: return node1;
            case 2: return node2;
            case 3: return node3;
            case 4: return node4;
            case 5: return node5;
            case 6: return node6;
            case 7: return node7;
            case 8: return node8;
            case 9: return node9;
            case 10: return node10;
            case 11: return node11;
            case 12: return node12;
            case 13: return node13;
            case 14: return node14;
            case 15: return node15;
            default: return node15;
        }
    }

    public void SetNode(int index, TerrainSplineGraphNode node)
    {
        switch (index)
        {
            case 0: node0 = node; break;
            case 1: node1 = node; break;
            case 2: node2 = node; break;
            case 3: node3 = node; break;
            case 4: node4 = node; break;
            case 5: node5 = node; break;
            case 6: node6 = node; break;
            case 7: node7 = node; break;
            case 8: node8 = node; break;
            case 9: node9 = node; break;
            case 10: node10 = node; break;
            case 11: node11 = node; break;
            case 12: node12 = node; break;
            case 13: node13 = node; break;
            case 14: node14 = node; break;
            case 15: node15 = node; break;
        }
    }

    public TerrainSplineShaperSettings Sanitized()
    {
        TerrainSplineShaperSettings sanitized = this;
        sanitized.graphNodeCount = math.clamp(graphNodeCount, 0, MaxGraphNodes);
        sanitized.offsetSpline = offsetSpline.Sanitized();
        sanitized.factorSpline = factorSpline.Sanitized();
        sanitized.jaggednessSpline = jaggednessSpline.Sanitized();

        TerrainSplineGraphNode[] nodes = new TerrainSplineGraphNode[MaxGraphNodes];
        for (int i = 0; i < sanitized.graphNodeCount; i++)
            nodes[i] = sanitized.GetNode(i).Sanitized();

        int nodeCursor = sanitized.graphNodeCount;
        int offsetRoot = SanitizeRootIndex(offsetRootNodeIndex, sanitized.graphNodeCount);
        int factorRoot = SanitizeRootIndex(factorRootNodeIndex, sanitized.graphNodeCount);
        int jaggednessRoot = SanitizeRootIndex(jaggednessRootNodeIndex, sanitized.graphNodeCount);

        EnsureLegacyFallbackNode(
            ref nodes,
            ref nodeCursor,
            sanitized.offsetSpline,
            TerrainSplineInput.Continents,
            ref offsetRoot);
        EnsureLegacyFallbackNode(
            ref nodes,
            ref nodeCursor,
            sanitized.factorSpline,
            TerrainSplineInput.Erosion,
            ref factorRoot);
        EnsureLegacyFallbackNode(
            ref nodes,
            ref nodeCursor,
            sanitized.jaggednessSpline,
            TerrainSplineInput.RidgesFolded,
            ref jaggednessRoot);

        for (int i = 0; i < nodeCursor; i++)
        {
            TerrainSplineGraphNode node = nodes[i];
            for (int pointIndex = 0; pointIndex < node.pointCount; pointIndex++)
            {
                TerrainSplineGraphPoint point = node.GetPoint(pointIndex);
                if (point.childNodeIndex >= i || point.childNodeIndex >= nodeCursor)
                    point.childNodeIndex = -1;

                node.SetPoint(pointIndex, point);
            }

            nodes[i] = node;
            sanitized.SetNode(i, node);
        }

        for (int i = nodeCursor; i < MaxGraphNodes; i++)
            sanitized.SetNode(i, default);

        sanitized.graphNodeCount = nodeCursor;
        sanitized.offsetRootNodeIndex = offsetRoot;
        sanitized.factorRootNodeIndex = factorRoot;
        sanitized.jaggednessRootNodeIndex = jaggednessRoot;

        bool hasAnyRoot = offsetRoot >= 0 || factorRoot >= 0 || jaggednessRoot >= 0;
        sanitized.enabled &= hasAnyRoot;
        return sanitized;
    }

    private static int SanitizeRootIndex(int rootIndex, int graphNodeCount)
    {
        return rootIndex >= 0 && rootIndex < graphNodeCount ? rootIndex : -1;
    }

    private static void EnsureLegacyFallbackNode(
        ref TerrainSplineGraphNode[] nodes,
        ref int nodeCursor,
        in TerrainSpline legacySpline,
        TerrainSplineInput input,
        ref int rootIndex)
    {
        if (rootIndex >= 0)
            return;

        TerrainSpline sanitizedLegacy = legacySpline.Sanitized();
        if (!sanitizedLegacy.enabled || sanitizedLegacy.pointCount <= 0 || nodeCursor >= MaxGraphNodes)
            return;

        nodes[nodeCursor] = TerrainSplineGraphNode.FromLegacy(sanitizedLegacy, input);
        rootIndex = nodeCursor;
        nodeCursor++;
    }

    private static TerrainSpline CreateWithSmoothing(float smoothing, params TerrainSplinePoint[] points)
    {
        TerrainSpline spline = TerrainSpline.Create(points);
        spline.smoothing = math.saturate(smoothing);
        return spline;
    }
}

public static class TerrainSplineEvaluator
{
    [BurstCompile]
    public static float Evaluate(in TerrainSpline spline, float input, float fallback)
    {
        if (!spline.enabled)
            return fallback;

        int count = math.clamp(spline.pointCount, 0, TerrainSpline.MaxPoints);
        if (count <= 0)
            return fallback;

        TerrainSplinePoint first = spline.GetPoint(0);
        if (count == 1)
            return first.value;

        if (input <= first.location)
            return first.value;

        TerrainSplinePoint previous = first;
        for (int i = 1; i < count; i++)
        {
            TerrainSplinePoint current = spline.GetPoint(i);
            if (input <= current.location)
            {
                float delta = current.location - previous.location;
                if (math.abs(delta) <= 1e-5f)
                    return current.value;

                float t = math.saturate((input - previous.location) / delta);
                float linear = math.lerp(previous.value, current.value, t);
                float smooth = EvaluateHermite(previous, current, previous.value, current.value, delta, t);
                return math.lerp(linear, smooth, math.saturate(spline.smoothing));
            }

            previous = current;
        }

        return previous.value;
    }

    [BurstCompile]
    private static float EvaluateHermite(
        in TerrainSplinePoint start,
        in TerrainSplinePoint end,
        float startValue,
        float endValue,
        float delta,
        float t)
    {
        float tt = t * t;
        float ttt = tt * t;
        float h00 = 2f * ttt - 3f * tt + 1f;
        float h10 = ttt - 2f * tt + t;
        float h01 = -2f * ttt + 3f * tt;
        float h11 = ttt - tt;

        return h00 * startValue +
               h10 * delta * start.GetDerivativeOut() +
               h01 * endValue +
               h11 * delta * end.GetDerivativeIn();
    }
}

public static class TerrainSplineGraphEvaluator
{
    [BurstCompile]
    public static float Evaluate(
        in TerrainSplineShaperSettings settings,
        TerrainSplineGraphTarget target,
        in TerrainShapePoint shapePoint,
        float fallback)
    {
        if (!settings.enabled)
            return EvaluateLegacy(settings, target, shapePoint, fallback);

        int rootIndex = GetRootIndex(settings, target);
        int graphNodeCount = math.clamp(settings.graphNodeCount, 0, TerrainSplineShaperSettings.MaxGraphNodes);
        if (rootIndex < 0 || rootIndex >= graphNodeCount)
            return EvaluateLegacy(settings, target, shapePoint, fallback);

        FixedList128Bytes<float> nodeValues = default;
        for (int nodeIndex = 0; nodeIndex < graphNodeCount; nodeIndex++)
        {
            TerrainSplineGraphNode node = settings.GetNode(nodeIndex);
            float nodeFallback = GetLegacyNodeFallback(settings, target, shapePoint, fallback, node.input);
            nodeValues.Add(EvaluateNode(node, nodeValues, shapePoint, nodeFallback));
        }

        return rootIndex < nodeValues.Length ? nodeValues[rootIndex] : fallback;
    }

    [BurstCompile]
    private static float EvaluateNode(
        in TerrainSplineGraphNode node,
        in FixedList128Bytes<float> childValues,
        in TerrainShapePoint shapePoint,
        float fallback)
    {
        if (!node.enabled)
            return fallback;

        int count = math.clamp(node.pointCount, 0, TerrainSplineGraphNode.MaxPoints);
        if (count <= 0)
            return fallback;

        float input = shapePoint.GetInputValue(node.input);
        TerrainSplineGraphPoint first = node.GetPoint(0);
        float firstValue = ResolvePointValue(first, childValues);
        if (count == 1)
            return firstValue;

        if (input <= first.location)
            return firstValue;

        TerrainSplineGraphPoint previous = first;
        float previousValue = firstValue;
        for (int i = 1; i < count; i++)
        {
            TerrainSplineGraphPoint current = node.GetPoint(i);
            float currentValue = ResolvePointValue(current, childValues);
            if (input <= current.location)
            {
                float delta = current.location - previous.location;
                if (math.abs(delta) <= 1e-5f)
                    return currentValue;

                float t = math.saturate((input - previous.location) / delta);
                float linear = math.lerp(previousValue, currentValue, t);
                float smooth = EvaluateHermite(previous, current, previousValue, currentValue, delta, t);
                return math.lerp(linear, smooth, math.saturate(node.smoothing));
            }

            previous = current;
            previousValue = currentValue;
        }

        return previousValue;
    }

    [BurstCompile]
    private static float ResolvePointValue(in TerrainSplineGraphPoint point, in FixedList128Bytes<float> childValues)
    {
        int childIndex = point.childNodeIndex;
        if (childIndex >= 0 && childIndex < childValues.Length)
            return childValues[childIndex];

        return point.value;
    }

    [BurstCompile]
    private static float EvaluateHermite(
        in TerrainSplineGraphPoint start,
        in TerrainSplineGraphPoint end,
        float startValue,
        float endValue,
        float delta,
        float t)
    {
        float tt = t * t;
        float ttt = tt * t;
        float h00 = 2f * ttt - 3f * tt + 1f;
        float h10 = ttt - 2f * tt + t;
        float h01 = -2f * ttt + 3f * tt;
        float h11 = ttt - tt;

        return h00 * startValue +
               h10 * delta * start.GetDerivativeOut() +
               h01 * endValue +
               h11 * delta * end.GetDerivativeIn();
    }

    [BurstCompile]
    private static float EvaluateLegacy(
        in TerrainSplineShaperSettings settings,
        TerrainSplineGraphTarget target,
        in TerrainShapePoint shapePoint,
        float fallback)
    {
        switch (target)
        {
            case TerrainSplineGraphTarget.Offset:
                return TerrainSplineEvaluator.Evaluate(settings.offsetSpline, shapePoint.continents, fallback);
            case TerrainSplineGraphTarget.Factor:
                return TerrainSplineEvaluator.Evaluate(settings.factorSpline, shapePoint.erosion, fallback);
            case TerrainSplineGraphTarget.Jaggedness:
            default:
                return TerrainSplineEvaluator.Evaluate(settings.jaggednessSpline, shapePoint.ridgesFolded, fallback);
        }
    }

    [BurstCompile]
    private static int GetRootIndex(in TerrainSplineShaperSettings settings, TerrainSplineGraphTarget target)
    {
        switch (target)
        {
            case TerrainSplineGraphTarget.Offset:
                return settings.offsetRootNodeIndex;
            case TerrainSplineGraphTarget.Factor:
                return settings.factorRootNodeIndex;
            case TerrainSplineGraphTarget.Jaggedness:
            default:
                return settings.jaggednessRootNodeIndex;
        }
    }

    [BurstCompile]
    private static float GetLegacyNodeFallback(
        in TerrainSplineShaperSettings settings,
        TerrainSplineGraphTarget target,
        in TerrainShapePoint shapePoint,
        float fallback,
        TerrainSplineInput nodeInput)
    {
        float targetFallback = EvaluateLegacy(settings, target, shapePoint, fallback);
        switch (nodeInput)
        {
            case TerrainSplineInput.Continents:
                return target == TerrainSplineGraphTarget.Offset ? targetFallback : shapePoint.continents;
            case TerrainSplineInput.Erosion:
                return target == TerrainSplineGraphTarget.Factor ? targetFallback : shapePoint.erosion;
            case TerrainSplineInput.Ridges:
                return shapePoint.ridges;
            case TerrainSplineInput.RidgesFolded:
            default:
                return target == TerrainSplineGraphTarget.Jaggedness ? targetFallback : shapePoint.ridgesFolded;
        }
    }
}
