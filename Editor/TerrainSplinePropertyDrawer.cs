using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(TerrainSpline))]
public sealed class TerrainSplinePropertyDrawer : PropertyDrawer
{
    private const float CurveHeight = 90f;
    private const float HelpBoxHeight = 36f;
    private const float TangentEpsilon = 1e-4f;

    private static readonly Dictionary<string, bool> AdvancedFoldouts = new Dictionary<string, bool>();

    private struct PointData
    {
        public float location;
        public float value;
        public float derivative;
        public bool splitTangents;
        public float derivativeIn;
        public float derivativeOut;

        public float GetDerivativeIn()
        {
            return splitTangents ? derivativeIn : derivative;
        }

        public float GetDerivativeOut()
        {
            return splitTangents ? derivativeOut : derivative;
        }
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float line = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float height = line;

        if (!property.isExpanded)
            return height;

        height += spacing + CurveHeight;
        height += spacing + line;

        int count = Mathf.Clamp(property.FindPropertyRelative("pointCount").intValue, 0, TerrainSpline.MaxPoints);
        if (count >= TerrainSpline.MaxPoints)
            height += spacing + HelpBoxHeight;

        height += spacing + line;

        if (GetAdvancedExpanded(property))
        {
            height += spacing + line;
            height += count * ((line * 2f) + (spacing * 2f));
        }

        return height;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        float line = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float y = position.y;

        SerializedProperty enabledProp = property.FindPropertyRelative("enabled");
        SerializedProperty smoothingProp = property.FindPropertyRelative("smoothing");

        Rect headerRect = new Rect(position.x, y, position.width, line);
        Rect foldoutRect = headerRect;
        foldoutRect.width -= 56f;
        property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);

        Rect enabledRect = headerRect;
        enabledRect.xMin = enabledRect.xMax - 52f;
        enabledProp.boolValue = EditorGUI.ToggleLeft(enabledRect, "On", enabledProp.boolValue);

        if (!property.isExpanded)
        {
            EditorGUI.EndProperty();
            return;
        }

        EditorGUI.indentLevel++;
        y += line + spacing;

        int count;
        float smoothing;
        PointData[] points = GetSanitizedPoints(property, out count, out smoothing);

        Rect curveRect = new Rect(position.x, y, position.width, CurveHeight);
        AnimationCurve curve = BuildCurve(points, smoothing);
        Rect curveRange = GetCurveRange(label, points);

        EditorGUI.BeginChangeCheck();
        AnimationCurve editedCurve = EditorGUI.CurveField(curveRect, GUIContent.none, curve, Color.green, curveRange);
        if (EditorGUI.EndChangeCheck())
        {
            ApplyCurveToProperty(property, smoothingProp, enabledProp, editedCurve);
            points = GetSanitizedPoints(property, out count, out smoothing);
        }

        y += CurveHeight + spacing;

        Rect smoothingRect = new Rect(position.x, y, position.width, line);
        EditorGUI.BeginChangeCheck();
        float smoothingValue = EditorGUI.Slider(smoothingRect, "Curve Blend", Mathf.Clamp01(smoothingProp.floatValue), 0f, 1f);
        if (EditorGUI.EndChangeCheck())
            smoothingProp.floatValue = smoothingValue;

        y += line + spacing;

        if (count >= TerrainSpline.MaxPoints)
        {
            Rect helpRect = new Rect(position.x, y, position.width, HelpBoxHeight);
            EditorGUI.HelpBox(helpRect, $"Limite atual: {TerrainSpline.MaxPoints} pontos por spline. Chaves extras da curva sao truncadas.", MessageType.Info);
            y += HelpBoxHeight + spacing;
        }

        Rect advancedRect = new Rect(position.x, y, position.width, line);
        bool advancedExpanded = GetAdvancedExpanded(property);
        advancedExpanded = EditorGUI.Foldout(advancedRect, advancedExpanded, "Raw Control Points", true);
        SetAdvancedExpanded(property, advancedExpanded);
        y += line + spacing;

        if (advancedExpanded)
        {
            Rect statsRect = new Rect(position.x, y, position.width, line);
            EditorGUI.LabelField(statsRect, $"Control points: {count}/{TerrainSpline.MaxPoints}");
            y += line + spacing;

            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < count; i++)
                y = DrawPointEditor(position.x, position.width, y, spacing, i, GetPointProperty(property, i));

            if (EditorGUI.EndChangeCheck())
                WriteSanitizedPoints(property, GetRawPoints(property));
        }

        EditorGUI.indentLevel--;
        EditorGUI.EndProperty();
    }

    private static float DrawPointEditor(float x, float width, float y, float spacing, int index, SerializedProperty pointProperty)
    {
        float line = EditorGUIUtility.singleLineHeight;

        SerializedProperty locationProp = pointProperty.FindPropertyRelative("location");
        SerializedProperty valueProp = pointProperty.FindPropertyRelative("value");
        SerializedProperty derivativeProp = pointProperty.FindPropertyRelative("derivative");
        SerializedProperty splitTangentsProp = pointProperty.FindPropertyRelative("splitTangents");
        SerializedProperty derivativeInProp = pointProperty.FindPropertyRelative("derivativeIn");
        SerializedProperty derivativeOutProp = pointProperty.FindPropertyRelative("derivativeOut");

        Rect firstRow = new Rect(x, y, width, line);
        Rect labelRect = new Rect(firstRow.x, firstRow.y, 28f, line);
        EditorGUI.LabelField(labelRect, $"P{index}");

        float fieldX = labelRect.xMax + 4f;
        float fieldWidth = (width - (fieldX - x) - 6f) * 0.5f;
        Rect locationRect = new Rect(fieldX, firstRow.y, fieldWidth, line);
        Rect valueRect = new Rect(locationRect.xMax + 6f, firstRow.y, fieldWidth, line);
        EditorGUI.PropertyField(locationRect, locationProp, new GUIContent("X"));
        EditorGUI.PropertyField(valueRect, valueProp, new GUIContent("Y"));

        y += line + spacing;

        Rect secondRow = new Rect(x, y, width, line);
        bool splitTangents = splitTangentsProp.boolValue;
        Rect splitRect = new Rect(secondRow.x, secondRow.y, 92f, line);
        bool nextSplitTangents = EditorGUI.ToggleLeft(splitRect, "Split", splitTangents);
        if (nextSplitTangents != splitTangents)
        {
            if (nextSplitTangents)
            {
                derivativeInProp.floatValue = derivativeProp.floatValue;
                derivativeOutProp.floatValue = derivativeProp.floatValue;
            }
            else
            {
                float unified = (derivativeInProp.floatValue + derivativeOutProp.floatValue) * 0.5f;
                derivativeProp.floatValue = unified;
                derivativeInProp.floatValue = unified;
                derivativeOutProp.floatValue = unified;
            }

            splitTangentsProp.boolValue = nextSplitTangents;
            splitTangents = nextSplitTangents;
        }

        Rect tangentRect = new Rect(splitRect.xMax + 6f, secondRow.y, width - (splitRect.width + 6f), line);
        if (splitTangents)
        {
            float tangentWidth = (tangentRect.width - 6f) * 0.5f;
            Rect inRect = new Rect(tangentRect.x, tangentRect.y, tangentWidth, line);
            Rect outRect = new Rect(inRect.xMax + 6f, tangentRect.y, tangentWidth, line);
            EditorGUI.PropertyField(inRect, derivativeInProp, new GUIContent("In"));
            EditorGUI.PropertyField(outRect, derivativeOutProp, new GUIContent("Out"));
            derivativeProp.floatValue = (derivativeInProp.floatValue + derivativeOutProp.floatValue) * 0.5f;
        }
        else
        {
            EditorGUI.PropertyField(tangentRect, derivativeProp, new GUIContent("Tangent"));
            derivativeInProp.floatValue = derivativeProp.floatValue;
            derivativeOutProp.floatValue = derivativeProp.floatValue;
        }

        return y + line + spacing;
    }

    private static bool GetAdvancedExpanded(SerializedProperty property)
    {
        return AdvancedFoldouts.TryGetValue(property.propertyPath, out bool expanded) && expanded;
    }

    private static void SetAdvancedExpanded(SerializedProperty property, bool expanded)
    {
        AdvancedFoldouts[property.propertyPath] = expanded;
    }

    private static SerializedProperty GetPointProperty(SerializedProperty property, int index)
    {
        return property.FindPropertyRelative($"point{index}");
    }

    private static PointData[] GetRawPoints(SerializedProperty property)
    {
        int count = Mathf.Clamp(property.FindPropertyRelative("pointCount").intValue, 0, TerrainSpline.MaxPoints);
        PointData[] points = new PointData[count];
        for (int i = 0; i < count; i++)
            points[i] = ReadPoint(GetPointProperty(property, i));

        return points;
    }

    private static PointData[] GetSanitizedPoints(SerializedProperty property, out int count, out float smoothing)
    {
        PointData[] points = GetRawPoints(property);
        smoothing = Mathf.Clamp01(property.FindPropertyRelative("smoothing").floatValue);
        count = Mathf.Clamp(points.Length, 0, TerrainSpline.MaxPoints);
        return SanitizePoints(points, count);
    }

    private static PointData ReadPoint(SerializedProperty pointProperty)
    {
        return new PointData
        {
            location = pointProperty.FindPropertyRelative("location").floatValue,
            value = pointProperty.FindPropertyRelative("value").floatValue,
            derivative = pointProperty.FindPropertyRelative("derivative").floatValue,
            splitTangents = pointProperty.FindPropertyRelative("splitTangents").boolValue,
            derivativeIn = pointProperty.FindPropertyRelative("derivativeIn").floatValue,
            derivativeOut = pointProperty.FindPropertyRelative("derivativeOut").floatValue
        };
    }

    private static void WritePoint(SerializedProperty pointProperty, PointData point)
    {
        pointProperty.FindPropertyRelative("location").floatValue = point.location;
        pointProperty.FindPropertyRelative("value").floatValue = point.value;
        pointProperty.FindPropertyRelative("derivative").floatValue = point.derivative;
        pointProperty.FindPropertyRelative("splitTangents").boolValue = point.splitTangents;
        pointProperty.FindPropertyRelative("derivativeIn").floatValue = point.derivativeIn;
        pointProperty.FindPropertyRelative("derivativeOut").floatValue = point.derivativeOut;
    }

    private static void WriteSanitizedPoints(SerializedProperty property, PointData[] rawPoints)
    {
        SerializedProperty pointCountProp = property.FindPropertyRelative("pointCount");
        SerializedProperty enabledProp = property.FindPropertyRelative("enabled");
        SerializedProperty smoothingProp = property.FindPropertyRelative("smoothing");

        int count = Mathf.Clamp(rawPoints?.Length ?? 0, 0, TerrainSpline.MaxPoints);
        PointData[] points = SanitizePoints(rawPoints ?? Array.Empty<PointData>(), count);

        pointCountProp.intValue = points.Length;
        smoothingProp.floatValue = Mathf.Clamp01(smoothingProp.floatValue);
        enabledProp.boolValue = enabledProp.boolValue && points.Length > 0;

        for (int i = 0; i < points.Length; i++)
            WritePoint(GetPointProperty(property, i), points[i]);

        for (int i = points.Length; i < TerrainSpline.MaxPoints; i++)
            WritePoint(GetPointProperty(property, i), default);
    }

    private static PointData[] SanitizePoints(PointData[] sourcePoints, int count)
    {
        if (count <= 0)
            return Array.Empty<PointData>();

        PointData[] points = new PointData[count];
        Array.Copy(sourcePoints, points, count);
        Array.Sort(points, CompareByLocation);

        for (int i = 0; i < points.Length; i++)
        {
            PointData point = points[i];
            float inDerivative = point.splitTangents ? point.derivativeIn : point.derivative;
            float outDerivative = point.splitTangents ? point.derivativeOut : point.derivative;
            bool splitTangents = Mathf.Abs(inDerivative - outDerivative) > TangentEpsilon;

            point.derivativeIn = inDerivative;
            point.derivativeOut = outDerivative;
            point.derivative = splitTangents ? (inDerivative + outDerivative) * 0.5f : inDerivative;
            point.splitTangents = splitTangents;

            if (i > 0 && point.location <= points[i - 1].location)
                point.location = points[i - 1].location + TangentEpsilon;

            points[i] = point;
        }

        return points;
    }

    private static int CompareByLocation(PointData left, PointData right)
    {
        return left.location.CompareTo(right.location);
    }

    private static AnimationCurve BuildCurve(PointData[] points, float smoothing)
    {
        if (points == null || points.Length == 0)
            return new AnimationCurve();

        Keyframe[] keys = new Keyframe[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            PointData point = points[i];
            Keyframe key = new Keyframe(point.location, point.value);

            if (points.Length == 1)
            {
                key.inTangent = 0f;
                key.outTangent = 0f;
            }
            else
            {
                key.inTangent = i == 0
                    ? GetEffectiveOutTangent(point, points[1], smoothing)
                    : GetEffectiveInTangent(points[i - 1], point, smoothing);
                key.outTangent = i == points.Length - 1
                    ? GetEffectiveInTangent(points[i - 1], point, smoothing)
                    : GetEffectiveOutTangent(point, points[i + 1], smoothing);
            }

            keys[i] = key;
        }

        AnimationCurve curve = new AnimationCurve(keys)
        {
            preWrapMode = WrapMode.ClampForever,
            postWrapMode = WrapMode.ClampForever
        };
        return curve;
    }

    private static Rect GetCurveRange(GUIContent label, PointData[] points)
    {
        bool jaggedCurve = label != null &&
                           !string.IsNullOrEmpty(label.text) &&
                           label.text.IndexOf("jagged", StringComparison.OrdinalIgnoreCase) >= 0;

        float minX = jaggedCurve ? 0f : -1f;
        float maxX = 1f;
        float minY = -1.25f;
        float maxY = 1.25f;

        if (points != null && points.Length > 0)
        {
            for (int i = 0; i < points.Length; i++)
            {
                minX = Mathf.Min(minX, points[i].location);
                maxX = Mathf.Max(maxX, points[i].location);
                minY = Mathf.Min(minY, points[i].value);
                maxY = Mathf.Max(maxY, points[i].value);
            }
        }

        float xRange = Mathf.Max(0.1f, maxX - minX);
        float yRange = Mathf.Max(0.5f, maxY - minY);
        minX -= xRange * 0.05f;
        maxX += xRange * 0.05f;
        minY -= yRange * 0.15f;
        maxY += yRange * 0.15f;

        return new Rect(minX, minY, Mathf.Max(0.1f, maxX - minX), Mathf.Max(0.1f, maxY - minY));
    }

    private static void ApplyCurveToProperty(
        SerializedProperty property,
        SerializedProperty smoothingProp,
        SerializedProperty enabledProp,
        AnimationCurve editedCurve)
    {
        Keyframe[] keys = editedCurve != null ? editedCurve.keys : Array.Empty<Keyframe>();
        if (keys.Length == 0)
        {
            WriteSanitizedPoints(property, Array.Empty<PointData>());
            enabledProp.boolValue = false;
            return;
        }

        Array.Sort(keys, CompareKeyframesByTime);
        if (keys.Length > TerrainSpline.MaxPoints)
            Array.Resize(ref keys, TerrainSpline.MaxPoints);

        float smoothing = Mathf.Clamp01(smoothingProp.floatValue);
        if (smoothing <= TangentEpsilon && CurveHasCustomTangents(keys))
        {
            smoothing = 1f;
            smoothingProp.floatValue = 1f;
        }

        PointData[] points = new PointData[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            Keyframe key = keys[i];
            float inDerivative;
            float outDerivative;

            if (keys.Length == 1)
            {
                inDerivative = 0f;
                outDerivative = 0f;
            }
            else if (i == 0)
            {
                float secant = GetSecant(key, keys[i + 1]);
                outDerivative = RecoverStoredTangent(key.outTangent, secant, smoothing);
                inDerivative = outDerivative;
            }
            else if (i == keys.Length - 1)
            {
                float secant = GetSecant(keys[i - 1], key);
                inDerivative = RecoverStoredTangent(key.inTangent, secant, smoothing);
                outDerivative = inDerivative;
            }
            else
            {
                float previousSecant = GetSecant(keys[i - 1], key);
                float nextSecant = GetSecant(key, keys[i + 1]);
                inDerivative = RecoverStoredTangent(key.inTangent, previousSecant, smoothing);
                outDerivative = RecoverStoredTangent(key.outTangent, nextSecant, smoothing);
            }

            bool splitTangents = Mathf.Abs(inDerivative - outDerivative) > TangentEpsilon;
            points[i] = new PointData
            {
                location = key.time,
                value = key.value,
                derivative = splitTangents ? (inDerivative + outDerivative) * 0.5f : inDerivative,
                splitTangents = splitTangents,
                derivativeIn = inDerivative,
                derivativeOut = outDerivative
            };
        }

        WriteSanitizedPoints(property, points);
        enabledProp.boolValue = true;
    }

    private static bool CurveHasCustomTangents(Keyframe[] keys)
    {
        if (keys == null || keys.Length <= 1)
            return false;

        for (int i = 0; i < keys.Length - 1; i++)
        {
            float secant = GetSecant(keys[i], keys[i + 1]);
            float outTangent = SanitizeCurveTangent(keys[i].outTangent, secant);
            float inTangent = SanitizeCurveTangent(keys[i + 1].inTangent, secant);
            if (Mathf.Abs(outTangent - secant) > TangentEpsilon || Mathf.Abs(inTangent - secant) > TangentEpsilon)
                return true;
        }

        return false;
    }

    private static int CompareKeyframesByTime(Keyframe left, Keyframe right)
    {
        return left.time.CompareTo(right.time);
    }

    private static float GetEffectiveInTangent(PointData start, PointData end, float smoothing)
    {
        float secant = GetSecant(start.location, start.value, end.location, end.value);
        return Mathf.Lerp(secant, end.GetDerivativeIn(), smoothing);
    }

    private static float GetEffectiveOutTangent(PointData start, PointData end, float smoothing)
    {
        float secant = GetSecant(start.location, start.value, end.location, end.value);
        return Mathf.Lerp(secant, start.GetDerivativeOut(), smoothing);
    }

    private static float RecoverStoredTangent(float curveTangent, float secant, float smoothing)
    {
        float sanitizedCurveTangent = SanitizeCurveTangent(curveTangent, secant);
        if (smoothing <= TangentEpsilon)
            return secant;

        return secant + ((sanitizedCurveTangent - secant) / smoothing);
    }

    private static float SanitizeCurveTangent(float tangent, float fallback)
    {
        return float.IsNaN(tangent) || float.IsInfinity(tangent) ? fallback : tangent;
    }

    private static float GetSecant(Keyframe start, Keyframe end)
    {
        return GetSecant(start.time, start.value, end.time, end.value);
    }

    private static float GetSecant(float startX, float startY, float endX, float endY)
    {
        float delta = endX - startX;
        if (Mathf.Abs(delta) <= TangentEpsilon)
            return 0f;

        return (endY - startY) / delta;
    }
}

[CustomPropertyDrawer(typeof(TerrainSplineGraphNode))]
public sealed class TerrainSplineGraphNodePropertyDrawer : PropertyDrawer
{
    private const float CurveHeight = 90f;
    private const float TangentEpsilon = 1e-4f;
    private const float EmptyHelpBoxHeight = 36f;
    private const float ChildPointInfoHeight = 44f;
    private const float ResolvedPreviewHelpBoxHeight = 44f;

    private static readonly Dictionary<string, bool> ResolvedPreviewToggles = new Dictionary<string, bool>();
    private static readonly Dictionary<string, Rect> CurveRanges = new Dictionary<string, Rect>();

    private struct GraphPointData
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
    }

    private struct PreviewShapePoint
    {
        public float continents;
        public float erosion;
        public float ridges;
        public float ridgesFolded;

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

    private struct NodePreviewData
    {
        public bool enabled;
        public TerrainSplineInput input;
        public float smoothing;
        public GraphPointData[] points;
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float line = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float height = line;

        if (!property.isExpanded)
            return height;

        height += spacing + line;
        height += spacing + line;
        height += spacing + CurveHeight;
        if (GetResolvedPreview(property))
            height += spacing + ResolvedPreviewHelpBoxHeight;

        height += spacing + line;
        height += spacing + line;

        int count = Mathf.Clamp(property.FindPropertyRelative("pointCount").intValue, 0, TerrainSplineGraphNode.MaxPoints);
        if (count == 0)
            height += spacing + EmptyHelpBoxHeight;
        else
        {
            if (HasAnyChildPoints(property, count))
                height += spacing + ChildPointInfoHeight;

            height += count * ((line * 3f) + (spacing * 4f));
        }

        return height;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        float line = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float y = position.y;

        Rect headerRect = new Rect(position.x, y, position.width, line);
        Rect foldoutRect = headerRect;
        foldoutRect.width -= 56f;
        property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);

        SerializedProperty enabledProp = property.FindPropertyRelative("enabled");
        Rect enabledRect = headerRect;
        enabledRect.xMin = enabledRect.xMax - 52f;
        enabledProp.boolValue = EditorGUI.ToggleLeft(enabledRect, "On", enabledProp.boolValue);

        if (!property.isExpanded)
        {
            EditorGUI.EndProperty();
            return;
        }

        y += line + spacing;

        SerializedProperty inputProp = property.FindPropertyRelative("input");
        Rect inputRect = new Rect(position.x, y, position.width, line);
        EditorGUI.PropertyField(inputRect, inputProp, new GUIContent("Input"));
        y += line + spacing;

        bool previousPreviewResolved = GetResolvedPreview(property);
        Rect toolsRect = new Rect(position.x, y, position.width, line);
        float fitButtonWidth = 86f;
        Rect previewToggleRect = new Rect(toolsRect.x, toolsRect.y, toolsRect.width - fitButtonWidth - 6f, line);
        Rect fitRect = new Rect(previewToggleRect.xMax + 6f, toolsRect.y, fitButtonWidth, line);
        bool previewResolved = EditorGUI.ToggleLeft(previewToggleRect, "Preview Child Values", previousPreviewResolved);
        SetResolvedPreview(property, previewResolved);
        y += line + spacing;

        SerializedProperty smoothingProp = property.FindPropertyRelative("smoothing");
        int count;
        float smoothing;
        GraphPointData[] points = GetSanitizedPoints(property, out count, out smoothing);

        TerrainSplineInput input = (TerrainSplineInput)Mathf.Clamp(inputProp.enumValueIndex, 0, 3);
        bool previewUsedChild = false;
        AnimationCurve curve = previewResolved
            ? BuildResolvedPreviewCurve(property, input, points, out previewUsedChild)
            : BuildCurve(points, smoothing);
        Rect fittedRange = GetCurveRange(input, curve != null ? curve.keys : Array.Empty<Keyframe>());
        Rect curveRange = GetStoredCurveRange(property, fittedRange);
        if (previewResolved != previousPreviewResolved)
        {
            curveRange = fittedRange;
            SetStoredCurveRange(property, curveRange);
        }

        if (GUI.Button(fitRect, "Fit Range"))
        {
            curveRange = fittedRange;
            SetStoredCurveRange(property, curveRange);
        }

        Rect curveRect = new Rect(position.x, y, position.width, CurveHeight);
        if (previewResolved)
        {
            EditorGUI.BeginDisabledGroup(true);
            EditorGUI.CurveField(curveRect, GUIContent.none, curve, Color.cyan, curveRange);
            EditorGUI.EndDisabledGroup();
        }
        else
        {
            EditorGUI.BeginChangeCheck();
            AnimationCurve editedCurve = EditorGUI.CurveField(curveRect, GUIContent.none, curve, Color.cyan, curveRange);
            if (EditorGUI.EndChangeCheck())
            {
                ApplyCurveToProperty(property, smoothingProp, enabledProp, editedCurve);
                points = GetSanitizedPoints(property, out count, out smoothing);
            }
        }

        y += CurveHeight + spacing;

        if (previewResolved)
        {
            Rect previewHelpRect = new Rect(position.x, y, position.width, ResolvedPreviewHelpBoxHeight);
            string previewMessage = previewUsedChild
                ? "Preview resolvido ativo: o grafico mostra valores com child node aplicado no estilo runtime e fica somente leitura."
                : "Preview resolvido ativo: este node ainda nao usa child em nenhum ponto, entao o grafico coincide com os valores locais.";
            EditorGUI.HelpBox(previewHelpRect, previewMessage, MessageType.None);
            y += ResolvedPreviewHelpBoxHeight + spacing;
        }

        Rect smoothingRect = new Rect(position.x, y, position.width, line);
        smoothingProp.floatValue = EditorGUI.Slider(smoothingRect, "Curve Blend", Mathf.Clamp01(smoothingProp.floatValue), 0f, 1f);
        y += line + spacing;

        SerializedProperty pointCountProp = property.FindPropertyRelative("pointCount");
        Rect countRect = new Rect(position.x, y, position.width, line);
        int previousCount = Mathf.Clamp(pointCountProp.intValue, 0, TerrainSplineGraphNode.MaxPoints);
        pointCountProp.intValue = EditorGUI.IntSlider(countRect, "Control Points", previousCount, 0, TerrainSplineGraphNode.MaxPoints);
        count = Mathf.Clamp(pointCountProp.intValue, 0, TerrainSplineGraphNode.MaxPoints);
        if (count != previousCount)
        {
            ClearUnusedPoints(property, count);
            points = GetSanitizedPoints(property, out count, out smoothing);
        }

        y += line + spacing;

        if (count == 0)
        {
            Rect helpRect = new Rect(position.x, y, position.width, EmptyHelpBoxHeight);
            EditorGUI.HelpBox(helpRect, "Adicione pontos para que este node participe do grafo.", MessageType.Info);
            EditorGUI.EndProperty();
            return;
        }

        if (HasAnyChildPoints(property, count))
        {
            Rect childInfoRect = new Rect(position.x, y, position.width, ChildPointInfoHeight);
            EditorGUI.HelpBox(
                childInfoRect,
                "Pontos com 'Use Child' usam o valor do node filho no runtime. O grafico mostra e edita apenas os valores locais armazenados.",
                MessageType.None);
            y += ChildPointInfoHeight + spacing;
        }

        EditorGUI.BeginChangeCheck();
        for (int i = 0; i < count; i++)
            y = DrawPointEditor(position.x, position.width, y, spacing, i, GetPointProperty(property, i));

        if (EditorGUI.EndChangeCheck())
            WriteSanitizedPoints(property, GetRawPoints(property));

        EditorGUI.EndProperty();
    }

    private static float DrawPointEditor(float x, float width, float y, float spacing, int index, SerializedProperty pointProperty)
    {
        float line = EditorGUIUtility.singleLineHeight;

        SerializedProperty locationProp = pointProperty.FindPropertyRelative("location");
        SerializedProperty valueProp = pointProperty.FindPropertyRelative("value");
        SerializedProperty derivativeProp = pointProperty.FindPropertyRelative("derivative");
        SerializedProperty splitTangentsProp = pointProperty.FindPropertyRelative("splitTangents");
        SerializedProperty derivativeInProp = pointProperty.FindPropertyRelative("derivativeIn");
        SerializedProperty derivativeOutProp = pointProperty.FindPropertyRelative("derivativeOut");
        SerializedProperty childNodeIndexProp = pointProperty.FindPropertyRelative("childNodeIndex");

        Rect firstRow = new Rect(x, y, width, line);
        Rect labelRect = new Rect(firstRow.x, firstRow.y, 28f, line);
        EditorGUI.LabelField(labelRect, $"P{index}");

        float fieldX = labelRect.xMax + 4f;
        float rowWidth = width - (fieldX - x);
        Rect locationRect = new Rect(fieldX, firstRow.y, rowWidth * 0.56f, line);
        EditorGUI.PropertyField(locationRect, locationProp, new GUIContent("X"));

        Rect childToggleRect = new Rect(locationRect.xMax + 6f, firstRow.y, rowWidth - locationRect.width - 6f, line);
        bool useChild = childNodeIndexProp.intValue >= 0;
        bool nextUseChild = EditorGUI.ToggleLeft(childToggleRect, "Use Child", useChild);
        if (nextUseChild != useChild)
        {
            childNodeIndexProp.intValue = nextUseChild ? 0 : -1;
            useChild = nextUseChild;
        }

        y += line + spacing;

        Rect secondRow = new Rect(x, y, width, line);
        Rect secondLabelRect = new Rect(secondRow.x, secondRow.y, 28f, line);
        EditorGUI.LabelField(secondLabelRect, string.Empty);

        Rect secondFieldRect = new Rect(fieldX, secondRow.y, width - (fieldX - x), line);
        if (useChild)
        {
            childNodeIndexProp.intValue = EditorGUI.IntField(secondFieldRect, new GUIContent("Child Node"), childNodeIndexProp.intValue);
            if (childNodeIndexProp.intValue < -1)
                childNodeIndexProp.intValue = -1;
        }
        else
        {
            EditorGUI.PropertyField(secondFieldRect, valueProp, new GUIContent("Value"));
            childNodeIndexProp.intValue = -1;
        }

        y += line + spacing;

        Rect thirdRow = new Rect(x, y, width, line);
        bool splitTangents = splitTangentsProp.boolValue;
        Rect splitRect = new Rect(thirdRow.x, thirdRow.y, 92f, line);
        bool nextSplitTangents = EditorGUI.ToggleLeft(splitRect, "Split", splitTangents);
        if (nextSplitTangents != splitTangents)
        {
            if (nextSplitTangents)
            {
                derivativeInProp.floatValue = derivativeProp.floatValue;
                derivativeOutProp.floatValue = derivativeProp.floatValue;
            }
            else
            {
                float unified = (derivativeInProp.floatValue + derivativeOutProp.floatValue) * 0.5f;
                derivativeProp.floatValue = unified;
                derivativeInProp.floatValue = unified;
                derivativeOutProp.floatValue = unified;
            }

            splitTangentsProp.boolValue = nextSplitTangents;
            splitTangents = nextSplitTangents;
        }

        Rect tangentRect = new Rect(splitRect.xMax + 6f, thirdRow.y, width - splitRect.width - 6f, line);
        if (splitTangents)
        {
            float tangentWidth = (tangentRect.width - 6f) * 0.5f;
            Rect inRect = new Rect(tangentRect.x, tangentRect.y, tangentWidth, line);
            Rect outRect = new Rect(inRect.xMax + 6f, tangentRect.y, tangentWidth, line);
            EditorGUI.PropertyField(inRect, derivativeInProp, new GUIContent("In"));
            EditorGUI.PropertyField(outRect, derivativeOutProp, new GUIContent("Out"));
            derivativeProp.floatValue = (derivativeInProp.floatValue + derivativeOutProp.floatValue) * 0.5f;
        }
        else
        {
            EditorGUI.PropertyField(tangentRect, derivativeProp, new GUIContent("Tangent"));
            derivativeInProp.floatValue = derivativeProp.floatValue;
            derivativeOutProp.floatValue = derivativeProp.floatValue;
        }

        return y + line + (spacing * 2f);
    }

    private static SerializedProperty GetPointProperty(SerializedProperty property, int index)
    {
        return property.FindPropertyRelative($"point{index}");
    }

    private static GraphPointData[] GetRawPoints(SerializedProperty property)
    {
        int count = Mathf.Clamp(property.FindPropertyRelative("pointCount").intValue, 0, TerrainSplineGraphNode.MaxPoints);
        GraphPointData[] points = new GraphPointData[count];
        for (int i = 0; i < count; i++)
            points[i] = ReadPoint(GetPointProperty(property, i));

        return points;
    }

    private static GraphPointData[] GetSanitizedPoints(SerializedProperty property, out int count, out float smoothing)
    {
        GraphPointData[] points = GetRawPoints(property);
        smoothing = Mathf.Clamp01(property.FindPropertyRelative("smoothing").floatValue);
        count = Mathf.Clamp(points.Length, 0, TerrainSplineGraphNode.MaxPoints);
        return SanitizePoints(points, count);
    }

    private static bool HasAnyChildPoints(SerializedProperty property, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (GetPointProperty(property, i).FindPropertyRelative("childNodeIndex").intValue >= 0)
                return true;
        }

        return false;
    }

    private static bool GetResolvedPreview(SerializedProperty property)
    {
        return ResolvedPreviewToggles.TryGetValue(property.propertyPath, out bool enabled) && enabled;
    }

    private static void SetResolvedPreview(SerializedProperty property, bool enabled)
    {
        ResolvedPreviewToggles[property.propertyPath] = enabled;
    }

    private static Rect GetStoredCurveRange(SerializedProperty property, Rect fallback)
    {
        if (CurveRanges.TryGetValue(property.propertyPath, out Rect stored) && stored.width > 0f && stored.height > 0f)
            return stored;

        CurveRanges[property.propertyPath] = fallback;
        return fallback;
    }

    private static void SetStoredCurveRange(SerializedProperty property, Rect range)
    {
        CurveRanges[property.propertyPath] = range;
    }

    private static GraphPointData ReadPoint(SerializedProperty pointProperty)
    {
        return new GraphPointData
        {
            location = pointProperty.FindPropertyRelative("location").floatValue,
            value = pointProperty.FindPropertyRelative("value").floatValue,
            derivative = pointProperty.FindPropertyRelative("derivative").floatValue,
            splitTangents = pointProperty.FindPropertyRelative("splitTangents").boolValue,
            derivativeIn = pointProperty.FindPropertyRelative("derivativeIn").floatValue,
            derivativeOut = pointProperty.FindPropertyRelative("derivativeOut").floatValue,
            childNodeIndex = pointProperty.FindPropertyRelative("childNodeIndex").intValue
        };
    }

    private static void WritePoint(SerializedProperty pointProperty, GraphPointData point)
    {
        pointProperty.FindPropertyRelative("location").floatValue = point.location;
        pointProperty.FindPropertyRelative("value").floatValue = point.value;
        pointProperty.FindPropertyRelative("derivative").floatValue = point.derivative;
        pointProperty.FindPropertyRelative("splitTangents").boolValue = point.splitTangents;
        pointProperty.FindPropertyRelative("derivativeIn").floatValue = point.derivativeIn;
        pointProperty.FindPropertyRelative("derivativeOut").floatValue = point.derivativeOut;
        pointProperty.FindPropertyRelative("childNodeIndex").intValue = point.childNodeIndex;
    }

    private static void WriteSanitizedPoints(SerializedProperty property, GraphPointData[] rawPoints)
    {
        SerializedProperty pointCountProp = property.FindPropertyRelative("pointCount");
        SerializedProperty enabledProp = property.FindPropertyRelative("enabled");
        SerializedProperty smoothingProp = property.FindPropertyRelative("smoothing");

        int count = Mathf.Clamp(rawPoints?.Length ?? 0, 0, TerrainSplineGraphNode.MaxPoints);
        GraphPointData[] points = SanitizePoints(rawPoints ?? Array.Empty<GraphPointData>(), count);

        pointCountProp.intValue = points.Length;
        smoothingProp.floatValue = Mathf.Clamp01(smoothingProp.floatValue);
        enabledProp.boolValue = enabledProp.boolValue && points.Length > 0;

        for (int i = 0; i < points.Length; i++)
            WritePoint(GetPointProperty(property, i), points[i]);

        for (int i = points.Length; i < TerrainSplineGraphNode.MaxPoints; i++)
            WritePoint(GetPointProperty(property, i), default);
    }

    private static GraphPointData[] SanitizePoints(GraphPointData[] sourcePoints, int count)
    {
        if (count <= 0)
            return Array.Empty<GraphPointData>();

        GraphPointData[] points = new GraphPointData[count];
        Array.Copy(sourcePoints, points, count);
        Array.Sort(points, CompareByLocation);

        for (int i = 0; i < points.Length; i++)
        {
            GraphPointData point = points[i];
            float inDerivative = point.splitTangents ? point.derivativeIn : point.derivative;
            float outDerivative = point.splitTangents ? point.derivativeOut : point.derivative;
            bool splitTangents = Mathf.Abs(inDerivative - outDerivative) > TangentEpsilon;

            point.derivativeIn = inDerivative;
            point.derivativeOut = outDerivative;
            point.derivative = splitTangents ? (inDerivative + outDerivative) * 0.5f : inDerivative;
            point.splitTangents = splitTangents;
            point.childNodeIndex = Mathf.Max(-1, point.childNodeIndex);

            if (i > 0 && point.location <= points[i - 1].location)
                point.location = points[i - 1].location + TangentEpsilon;

            points[i] = point;
        }

        return points;
    }

    private static void ClearUnusedPoints(SerializedProperty property, int startIndex)
    {
        for (int i = Mathf.Clamp(startIndex, 0, TerrainSplineGraphNode.MaxPoints); i < TerrainSplineGraphNode.MaxPoints; i++)
            WritePoint(GetPointProperty(property, i), default);
    }

    private static AnimationCurve BuildCurve(GraphPointData[] points, float smoothing)
    {
        if (points == null || points.Length == 0)
            return new AnimationCurve();

        Keyframe[] keys = new Keyframe[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            GraphPointData point = points[i];
            Keyframe key = new Keyframe(point.location, point.value);

            if (points.Length == 1)
            {
                key.inTangent = 0f;
                key.outTangent = 0f;
            }
            else
            {
                key.inTangent = i == 0
                    ? GetEffectiveOutTangent(point, points[1], smoothing)
                    : GetEffectiveInTangent(points[i - 1], point, smoothing);
                key.outTangent = i == points.Length - 1
                    ? GetEffectiveInTangent(points[i - 1], point, smoothing)
                    : GetEffectiveOutTangent(point, points[i + 1], smoothing);
            }

            keys[i] = key;
        }

        AnimationCurve curve = new AnimationCurve(keys)
        {
            preWrapMode = WrapMode.ClampForever,
            postWrapMode = WrapMode.ClampForever
        };
        return curve;
    }

    private static AnimationCurve BuildResolvedPreviewCurve(
        SerializedProperty property,
        TerrainSplineInput input,
        GraphPointData[] currentPoints,
        out bool usedChildValues)
    {
        usedChildValues = false;
        if (!TryGetGraphContext(property, out SerializedProperty shaperProperty, out int nodeIndex, out int graphNodeCount))
            return BuildCurve(currentPoints, Mathf.Clamp01(property.FindPropertyRelative("smoothing").floatValue));

        int previewNodeCount = Mathf.Clamp(nodeIndex + 1, 1, graphNodeCount);
        NodePreviewData[] nodes = new NodePreviewData[previewNodeCount];
        for (int i = 0; i < previewNodeCount; i++)
            nodes[i] = ReadNodePreviewData(GetGraphNodeProperty(shaperProperty, i));

        NodePreviewData targetNode = nodes[nodeIndex];
        if (targetNode.points == null || targetNode.points.Length == 0)
            return new AnimationCurve();

        float minX = input == TerrainSplineInput.RidgesFolded ? 0f : -1f;
        float maxX = 1f;
        for (int i = 0; i < targetNode.points.Length; i++)
        {
            minX = Mathf.Min(minX, targetNode.points[i].location);
            maxX = Mathf.Max(maxX, targetNode.points[i].location);
        }

        if (Mathf.Abs(maxX - minX) <= TangentEpsilon)
        {
            minX -= 0.5f;
            maxX += 0.5f;
        }

        int sampleCount = Mathf.Clamp(24 + (targetNode.points.Length * 6), 24, 96);
        Keyframe[] keys = new Keyframe[sampleCount];
        float[] nodeValues = new float[previewNodeCount];
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            float normalized = sampleCount > 1
                ? sampleIndex / (sampleCount - 1f)
                : 0f;
            float x = Mathf.Lerp(minX, maxX, normalized);
            PreviewShapePoint shapePoint = BuildPreviewShapePoint(input, x, normalized);

            bool sampleUsesChild = false;
            for (int nodeCursor = 0; nodeCursor < previewNodeCount; nodeCursor++)
            {
                nodeValues[nodeCursor] = EvaluatePreviewNode(
                    nodes[nodeCursor],
                    nodeValues,
                    nodeCursor,
                    shapePoint,
                    out bool nodeUsesChild);

                if (nodeCursor == nodeIndex && nodeUsesChild)
                    sampleUsesChild = true;
            }

            usedChildValues |= sampleUsesChild;
            keys[sampleIndex] = new Keyframe(x, nodeValues[nodeIndex]);
        }

        return BuildCurveFromSamples(keys);
    }

    private static bool TryGetGraphContext(
        SerializedProperty nodeProperty,
        out SerializedProperty shaperProperty,
        out int nodeIndex,
        out int graphNodeCount)
    {
        shaperProperty = null;
        nodeIndex = -1;
        graphNodeCount = 0;

        string path = nodeProperty.propertyPath;
        int nodeTokenIndex = path.LastIndexOf(".node", StringComparison.Ordinal);
        if (nodeTokenIndex < 0)
            return false;

        int indexStart = nodeTokenIndex + 5;
        int indexLength = 0;
        while (indexStart + indexLength < path.Length && char.IsDigit(path[indexStart + indexLength]))
            indexLength++;

        if (indexLength <= 0 || indexStart + indexLength != path.Length)
            return false;
        if (!int.TryParse(path.Substring(indexStart, indexLength), out nodeIndex))
            return false;

        string shaperPath = path.Substring(0, nodeTokenIndex);
        shaperProperty = nodeProperty.serializedObject.FindProperty(shaperPath);
        if (shaperProperty == null)
            return false;

        graphNodeCount = Mathf.Clamp(
            shaperProperty.FindPropertyRelative("graphNodeCount").intValue,
            0,
            TerrainSplineShaperSettings.MaxGraphNodes);
        return nodeIndex >= 0 && nodeIndex < graphNodeCount;
    }

    private static SerializedProperty GetGraphNodeProperty(SerializedProperty shaperProperty, int nodeIndex)
    {
        return shaperProperty.FindPropertyRelative($"node{nodeIndex}");
    }

    private static NodePreviewData ReadNodePreviewData(SerializedProperty nodeProperty)
    {
        GraphPointData[] points = GetSanitizedPoints(nodeProperty, out _, out float smoothing);
        return new NodePreviewData
        {
            enabled = nodeProperty.FindPropertyRelative("enabled").boolValue,
            input = (TerrainSplineInput)Mathf.Clamp(nodeProperty.FindPropertyRelative("input").enumValueIndex, 0, 3),
            smoothing = smoothing,
            points = points
        };
    }

    private static PreviewShapePoint BuildPreviewShapePoint(TerrainSplineInput targetInput, float x, float normalized)
    {
        float t = Mathf.Clamp01(normalized);
        float signed = Mathf.Lerp(-1f, 1f, t);
        PreviewShapePoint point = new PreviewShapePoint
        {
            continents = signed,
            erosion = signed,
            ridges = signed,
            ridgesFolded = t
        };

        switch (targetInput)
        {
            case TerrainSplineInput.Continents:
                point.continents = x;
                break;
            case TerrainSplineInput.Erosion:
                point.erosion = x;
                break;
            case TerrainSplineInput.Ridges:
                point.ridges = x;
                break;
            case TerrainSplineInput.RidgesFolded:
            default:
                point.ridgesFolded = x;
                break;
        }

        return point;
    }

    private static float EvaluatePreviewNode(
        in NodePreviewData node,
        float[] childValues,
        int childCount,
        in PreviewShapePoint shapePoint,
        out bool usedChild)
    {
        usedChild = false;
        if (!node.enabled || node.points == null || node.points.Length == 0)
            return shapePoint.GetInputValue(node.input);

        float input = shapePoint.GetInputValue(node.input);
        GraphPointData first = node.points[0];
        float firstValue = ResolvePreviewPointValue(first, childValues, childCount, out bool firstUsesChild);
        usedChild |= firstUsesChild;

        if (node.points.Length == 1 || input <= first.location)
            return firstValue;

        GraphPointData previous = first;
        float previousValue = firstValue;
        for (int i = 1; i < node.points.Length; i++)
        {
            GraphPointData current = node.points[i];
            float currentValue = ResolvePreviewPointValue(current, childValues, childCount, out bool currentUsesChild);
            usedChild |= currentUsesChild;
            if (input <= current.location)
            {
                float delta = current.location - previous.location;
                if (Mathf.Abs(delta) <= TangentEpsilon)
                    return currentValue;

                float t = Mathf.Clamp01((input - previous.location) / delta);
                float linear = Mathf.Lerp(previousValue, currentValue, t);
                float smooth = EvaluatePreviewHermite(previous, current, previousValue, currentValue, delta, t);
                return Mathf.Lerp(linear, smooth, Mathf.Clamp01(node.smoothing));
            }

            previous = current;
            previousValue = currentValue;
        }

        return previousValue;
    }

    private static float ResolvePreviewPointValue(
        in GraphPointData point,
        float[] childValues,
        int childCount,
        out bool usedChild)
    {
        int childIndex = point.childNodeIndex;
        if (childIndex >= 0 && childIndex < childCount)
        {
            usedChild = true;
            return childValues[childIndex];
        }

        usedChild = false;
        return point.value;
    }

    private static float EvaluatePreviewHermite(
        in GraphPointData start,
        in GraphPointData end,
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

    private static AnimationCurve BuildCurveFromSamples(Keyframe[] keys)
    {
        if (keys == null || keys.Length == 0)
            return new AnimationCurve();

        if (keys.Length == 1)
        {
            Keyframe single = keys[0];
            single.inTangent = 0f;
            single.outTangent = 0f;
            keys[0] = single;
        }
        else
        {
            for (int i = 0; i < keys.Length; i++)
            {
                Keyframe key = keys[i];
                if (i == 0)
                {
                    float tangent = GetSecant(keys[i], keys[i + 1]);
                    key.inTangent = tangent;
                    key.outTangent = tangent;
                }
                else if (i == keys.Length - 1)
                {
                    float tangent = GetSecant(keys[i - 1], keys[i]);
                    key.inTangent = tangent;
                    key.outTangent = tangent;
                }
                else
                {
                    key.inTangent = GetSecant(keys[i - 1], keys[i]);
                    key.outTangent = GetSecant(keys[i], keys[i + 1]);
                }

                keys[i] = key;
            }
        }

        AnimationCurve curve = new AnimationCurve(keys)
        {
            preWrapMode = WrapMode.ClampForever,
            postWrapMode = WrapMode.ClampForever
        };
        return curve;
    }

    private static Rect GetCurveRange(TerrainSplineInput input, GraphPointData[] points)
    {
        if (points == null || points.Length == 0)
            return GetCurveRange(input, Array.Empty<Keyframe>());

        Keyframe[] keys = new Keyframe[points.Length];
        for (int i = 0; i < points.Length; i++)
            keys[i] = new Keyframe(points[i].location, points[i].value);

        return GetCurveRange(input, keys);
    }

    private static Rect GetCurveRange(TerrainSplineInput input, Keyframe[] keys)
    {
        float minX = input == TerrainSplineInput.RidgesFolded ? 0f : -1f;
        float maxX = 1f;
        float minY = -1.25f;
        float maxY = 1.25f;

        if (keys != null && keys.Length > 0)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                minX = Mathf.Min(minX, keys[i].time);
                maxX = Mathf.Max(maxX, keys[i].time);
                minY = Mathf.Min(minY, keys[i].value);
                maxY = Mathf.Max(maxY, keys[i].value);
            }
        }

        float xRange = Mathf.Max(0.1f, maxX - minX);
        float yRange = Mathf.Max(0.5f, maxY - minY);
        minX -= xRange * 0.05f;
        maxX += xRange * 0.05f;
        minY -= yRange * 0.15f;
        maxY += yRange * 0.15f;

        return new Rect(minX, minY, Mathf.Max(0.1f, maxX - minX), Mathf.Max(0.1f, maxY - minY));
    }

    private static void ApplyCurveToProperty(
        SerializedProperty property,
        SerializedProperty smoothingProp,
        SerializedProperty enabledProp,
        AnimationCurve editedCurve)
    {
        GraphPointData[] previousPoints = GetSanitizedPoints(property, out _, out _);
        Keyframe[] keys = editedCurve != null ? editedCurve.keys : Array.Empty<Keyframe>();
        if (keys.Length == 0)
        {
            WriteSanitizedPoints(property, Array.Empty<GraphPointData>());
            enabledProp.boolValue = false;
            return;
        }

        Array.Sort(keys, CompareKeyframesByTime);
        if (keys.Length > TerrainSplineGraphNode.MaxPoints)
            Array.Resize(ref keys, TerrainSplineGraphNode.MaxPoints);

        float smoothing = Mathf.Clamp01(smoothingProp.floatValue);
        if (smoothing <= TangentEpsilon && CurveHasCustomTangents(keys))
        {
            smoothing = 1f;
            smoothingProp.floatValue = 1f;
        }

        GraphPointData[] points = new GraphPointData[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            Keyframe key = keys[i];
            float inDerivative;
            float outDerivative;

            if (keys.Length == 1)
            {
                inDerivative = 0f;
                outDerivative = 0f;
            }
            else if (i == 0)
            {
                float secant = GetSecant(key, keys[i + 1]);
                outDerivative = RecoverStoredTangent(key.outTangent, secant, smoothing);
                inDerivative = outDerivative;
            }
            else if (i == keys.Length - 1)
            {
                float secant = GetSecant(keys[i - 1], key);
                inDerivative = RecoverStoredTangent(key.inTangent, secant, smoothing);
                outDerivative = inDerivative;
            }
            else
            {
                float previousSecant = GetSecant(keys[i - 1], key);
                float nextSecant = GetSecant(key, keys[i + 1]);
                inDerivative = RecoverStoredTangent(key.inTangent, previousSecant, smoothing);
                outDerivative = RecoverStoredTangent(key.outTangent, nextSecant, smoothing);
            }

            bool splitTangents = Mathf.Abs(inDerivative - outDerivative) > TangentEpsilon;
            points[i] = new GraphPointData
            {
                location = key.time,
                value = key.value,
                derivative = splitTangents ? (inDerivative + outDerivative) * 0.5f : inDerivative,
                splitTangents = splitTangents,
                derivativeIn = inDerivative,
                derivativeOut = outDerivative,
                childNodeIndex = i < previousPoints.Length ? previousPoints[i].childNodeIndex : -1
            };
        }

        WriteSanitizedPoints(property, points);
        enabledProp.boolValue = true;
    }

    private static bool CurveHasCustomTangents(Keyframe[] keys)
    {
        if (keys == null || keys.Length <= 1)
            return false;

        for (int i = 0; i < keys.Length - 1; i++)
        {
            float secant = GetSecant(keys[i], keys[i + 1]);
            float outTangent = SanitizeCurveTangent(keys[i].outTangent, secant);
            float inTangent = SanitizeCurveTangent(keys[i + 1].inTangent, secant);
            if (Mathf.Abs(outTangent - secant) > TangentEpsilon || Mathf.Abs(inTangent - secant) > TangentEpsilon)
                return true;
        }

        return false;
    }

    private static int CompareKeyframesByTime(Keyframe left, Keyframe right)
    {
        return left.time.CompareTo(right.time);
    }

    private static float GetEffectiveInTangent(GraphPointData start, GraphPointData end, float smoothing)
    {
        float secant = GetSecant(start.location, start.value, end.location, end.value);
        return Mathf.Lerp(secant, end.GetDerivativeIn(), smoothing);
    }

    private static float GetEffectiveOutTangent(GraphPointData start, GraphPointData end, float smoothing)
    {
        float secant = GetSecant(start.location, start.value, end.location, end.value);
        return Mathf.Lerp(secant, start.GetDerivativeOut(), smoothing);
    }

    private static float RecoverStoredTangent(float curveTangent, float secant, float smoothing)
    {
        float sanitizedCurveTangent = SanitizeCurveTangent(curveTangent, secant);
        if (smoothing <= TangentEpsilon)
            return secant;

        return secant + ((sanitizedCurveTangent - secant) / smoothing);
    }

    private static float SanitizeCurveTangent(float tangent, float fallback)
    {
        return float.IsNaN(tangent) || float.IsInfinity(tangent) ? fallback : tangent;
    }

    private static float GetSecant(Keyframe start, Keyframe end)
    {
        return GetSecant(start.time, start.value, end.time, end.value);
    }

    private static float GetSecant(float startX, float startY, float endX, float endY)
    {
        float delta = endX - startX;
        if (Mathf.Abs(delta) <= TangentEpsilon)
            return 0f;

        return (endY - startY) / delta;
    }

    private static int CompareByLocation(GraphPointData left, GraphPointData right)
    {
        return left.location.CompareTo(right.location);
    }
}

[CustomPropertyDrawer(typeof(TerrainSplineShaperSettings))]
public sealed class TerrainSplineShaperSettingsPropertyDrawer : PropertyDrawer
{
    private const float GraphHelpBoxHeight = 44f;

    private static readonly Dictionary<string, bool> GraphFoldouts = new Dictionary<string, bool>();
    private static readonly GUIContent SculptTuningLabel = new GUIContent(
        "Sculpt Tuning",
        "Parametros extras para esculpir montanhas (pico, recorte e controle de jaggedness).");
    private static readonly GUIContent MountainStartLabel = new GUIContent(
        "Mountain Start",
        "Valor minimo do canal MountainNoise para comecar a gerar picos. Maior valor reduz a quantidade de montanhas.");
    private static readonly GUIContent MountainRangeLabel = new GUIContent(
        "Mountain Range",
        "Faixa de remapeamento apos Mountain Start. Menor faixa deixa a subida de altura mais agressiva.");
    private static readonly GUIContent PeakExponentLabel = new GUIContent(
        "Peak Exponent",
        "Curvatura da mascara de pico. Maior valor cria picos mais agudos e ombros mais estreitos.");
    private static readonly GUIContent JaggednessFloorLabel = new GUIContent(
        "Jaggedness Floor",
        "Piso minimo de jaggedness baseado no pico. Ajuda a evitar topo oco quando a spline cai para valores baixos.");
    private static readonly GUIContent ChiselStrengthLabel = new GUIContent(
        "Chisel Strength",
        "Forca do recorte/esculpido nas cristas e vales. Maior valor deixa as montanhas mais talhadas.");
    private static readonly GUIContent ChiselExponentLabel = new GUIContent(
        "Chisel Exponent",
        "Contraste do recorte. Maior valor concentra o efeito em cristas mais fortes.");
    private static readonly GUIContent ChiselFlattenMixLabel = new GUIContent(
        "Chisel Flatten Mix",
        "Quanto o flatten reduz o recorte. 0 = flatten quase remove o chisel; 1 = flatten quase nao afeta o chisel.");

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float line = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float height = line;

        if (!property.isExpanded)
            return height;

        SerializedProperty offsetSplineProp = property.FindPropertyRelative("offsetSpline");
        SerializedProperty factorSplineProp = property.FindPropertyRelative("factorSpline");
        SerializedProperty jaggednessSplineProp = property.FindPropertyRelative("jaggednessSpline");
        SerializedProperty mountainSignalStartProp = property.FindPropertyRelative("mountainSignalStart");
        SerializedProperty mountainSignalRangeProp = property.FindPropertyRelative("mountainSignalRange");
        SerializedProperty mountainPeakExponentProp = property.FindPropertyRelative("mountainPeakExponent");
        SerializedProperty jaggednessPeakFloorProp = property.FindPropertyRelative("jaggednessPeakFloor");
        SerializedProperty ridgeChiselStrengthProp = property.FindPropertyRelative("ridgeChiselStrength");
        SerializedProperty ridgeChiselExponentProp = property.FindPropertyRelative("ridgeChiselExponent");
        SerializedProperty ridgeChiselFlattenAttenuationProp = property.FindPropertyRelative("ridgeChiselFlattenAttenuation");

        height += spacing + line;
        height += spacing + line;
        height += spacing + EditorGUI.GetPropertyHeight(offsetSplineProp, true);
        height += spacing + EditorGUI.GetPropertyHeight(factorSplineProp, true);
        height += spacing + EditorGUI.GetPropertyHeight(jaggednessSplineProp, true);
        height += spacing + line;
        height += spacing + EditorGUI.GetPropertyHeight(mountainSignalStartProp, true);
        height += spacing + EditorGUI.GetPropertyHeight(mountainSignalRangeProp, true);
        height += spacing + EditorGUI.GetPropertyHeight(mountainPeakExponentProp, true);
        height += spacing + EditorGUI.GetPropertyHeight(jaggednessPeakFloorProp, true);
        height += spacing + EditorGUI.GetPropertyHeight(ridgeChiselStrengthProp, true);
        height += spacing + EditorGUI.GetPropertyHeight(ridgeChiselExponentProp, true);
        height += spacing + EditorGUI.GetPropertyHeight(ridgeChiselFlattenAttenuationProp, true);
        height += spacing + line;

        if (GetGraphExpanded(property))
        {
            int count = Mathf.Clamp(property.FindPropertyRelative("graphNodeCount").intValue, 0, TerrainSplineShaperSettings.MaxGraphNodes);
            height += spacing + line;

            if (count == 0)
            {
                height += spacing + GraphHelpBoxHeight;
            }
            else
            {
                height += spacing + GraphHelpBoxHeight;
                height += spacing + line;
                height += spacing + line;
                height += spacing + line;

                for (int i = 0; i < count; i++)
                    height += spacing + EditorGUI.GetPropertyHeight(GetNodeProperty(property, i), true);
            }
        }

        return height;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        float line = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float y = position.y;

        SerializedProperty enabledProp = property.FindPropertyRelative("enabled");
        SerializedProperty offsetSplineProp = property.FindPropertyRelative("offsetSpline");
        SerializedProperty factorSplineProp = property.FindPropertyRelative("factorSpline");
        SerializedProperty jaggednessSplineProp = property.FindPropertyRelative("jaggednessSpline");
        SerializedProperty mountainSignalStartProp = property.FindPropertyRelative("mountainSignalStart");
        SerializedProperty mountainSignalRangeProp = property.FindPropertyRelative("mountainSignalRange");
        SerializedProperty mountainPeakExponentProp = property.FindPropertyRelative("mountainPeakExponent");
        SerializedProperty jaggednessPeakFloorProp = property.FindPropertyRelative("jaggednessPeakFloor");
        SerializedProperty ridgeChiselStrengthProp = property.FindPropertyRelative("ridgeChiselStrength");
        SerializedProperty ridgeChiselExponentProp = property.FindPropertyRelative("ridgeChiselExponent");
        SerializedProperty ridgeChiselFlattenAttenuationProp = property.FindPropertyRelative("ridgeChiselFlattenAttenuation");

        Rect headerRect = new Rect(position.x, y, position.width, line);
        Rect foldoutRect = headerRect;
        foldoutRect.width -= 56f;
        property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);

        Rect enabledRect = headerRect;
        enabledRect.xMin = enabledRect.xMax - 52f;
        enabledProp.boolValue = EditorGUI.ToggleLeft(enabledRect, "On", enabledProp.boolValue);

        if (!property.isExpanded)
        {
            EditorGUI.EndProperty();
            return;
        }

        y += line + spacing;

        Rect buttonRow = new Rect(position.x, y, position.width, line);
        Rect clearRect = new Rect(buttonRow.x, buttonRow.y, buttonRow.width, line);
        if (GUI.Button(clearRect, "Clear Graph"))
            ClearGraph(property);

        y += line + spacing;

        Rect curvesLabelRect = new Rect(position.x, y, position.width, line);
        EditorGUI.LabelField(curvesLabelRect, "Minecraft Curves (Offset / Factor / Jaggedness)", EditorStyles.boldLabel);
        y += line + spacing;

        float offsetSplineHeight = EditorGUI.GetPropertyHeight(offsetSplineProp, true);
        Rect offsetSplineRect = new Rect(position.x, y, position.width, offsetSplineHeight);
        EditorGUI.PropertyField(offsetSplineRect, offsetSplineProp, new GUIContent("Offset"), true);
        y += offsetSplineHeight + spacing;

        float factorSplineHeight = EditorGUI.GetPropertyHeight(factorSplineProp, true);
        Rect factorSplineRect = new Rect(position.x, y, position.width, factorSplineHeight);
        EditorGUI.PropertyField(factorSplineRect, factorSplineProp, new GUIContent("Factor"), true);
        y += factorSplineHeight + spacing;

        float jaggednessSplineHeight = EditorGUI.GetPropertyHeight(jaggednessSplineProp, true);
        Rect jaggednessSplineRect = new Rect(position.x, y, position.width, jaggednessSplineHeight);
        EditorGUI.PropertyField(jaggednessSplineRect, jaggednessSplineProp, new GUIContent("Jaggedness"), true);
        y += jaggednessSplineHeight + spacing;

        Rect sculptLabelRect = new Rect(position.x, y, position.width, line);
        EditorGUI.LabelField(sculptLabelRect, SculptTuningLabel, EditorStyles.boldLabel);
        y += line + spacing;

        Rect mountainSignalStartRect = new Rect(position.x, y, position.width, line);
        EditorGUI.Slider(mountainSignalStartRect, mountainSignalStartProp, 0f, 0.95f, MountainStartLabel);
        y += line + spacing;

        Rect mountainSignalRangeRect = new Rect(position.x, y, position.width, line);
        EditorGUI.Slider(mountainSignalRangeRect, mountainSignalRangeProp, 0.05f, 2f, MountainRangeLabel);
        y += line + spacing;

        Rect mountainPeakExponentRect = new Rect(position.x, y, position.width, line);
        EditorGUI.Slider(mountainPeakExponentRect, mountainPeakExponentProp, 0.2f, 6f, PeakExponentLabel);
        y += line + spacing;

        Rect jaggednessPeakFloorRect = new Rect(position.x, y, position.width, line);
        EditorGUI.Slider(jaggednessPeakFloorRect, jaggednessPeakFloorProp, 0f, 2f, JaggednessFloorLabel);
        y += line + spacing;

        Rect ridgeChiselStrengthRect = new Rect(position.x, y, position.width, line);
        EditorGUI.Slider(ridgeChiselStrengthRect, ridgeChiselStrengthProp, 0f, 2f, ChiselStrengthLabel);
        y += line + spacing;

        Rect ridgeChiselExponentRect = new Rect(position.x, y, position.width, line);
        EditorGUI.Slider(ridgeChiselExponentRect, ridgeChiselExponentProp, 0.3f, 6f, ChiselExponentLabel);
        y += line + spacing;

        Rect ridgeChiselFlattenAttenuationRect = new Rect(position.x, y, position.width, line);
        EditorGUI.Slider(ridgeChiselFlattenAttenuationRect, ridgeChiselFlattenAttenuationProp, 0f, 1f, ChiselFlattenMixLabel);
        y += line + spacing;

        Rect graphFoldoutRect = new Rect(position.x, y, position.width, line);
        bool graphExpanded = EditorGUI.Foldout(graphFoldoutRect, GetGraphExpanded(property), "Graph Authoring", true);
        SetGraphExpanded(property, graphExpanded);
        y += line + spacing;

        if (graphExpanded)
        {
            SerializedProperty countProp = property.FindPropertyRelative("graphNodeCount");
            int previousCount = Mathf.Clamp(countProp.intValue, 0, TerrainSplineShaperSettings.MaxGraphNodes);
            Rect countRect = new Rect(position.x, y, position.width, line);
            countProp.intValue = EditorGUI.IntSlider(countRect, "Node Count", previousCount, 0, TerrainSplineShaperSettings.MaxGraphNodes);
            NormalizeGraphState(property);
            int count = Mathf.Clamp(countProp.intValue, 0, TerrainSplineShaperSettings.MaxGraphNodes);
            y += line + spacing;

            if (count == 0)
            {
                Rect helpRect = new Rect(position.x, y, position.width, GraphHelpBoxHeight);
                EditorGUI.HelpBox(helpRect, "Sem nodes autorados. O shaping spline fica inativo ate definir roots.", MessageType.Info);
                y += GraphHelpBoxHeight + spacing;
            }
            else
            {
                Rect helpRect = new Rect(position.x, y, position.width, GraphHelpBoxHeight);
                EditorGUI.HelpBox(helpRect, "Cada ponto pode usar um valor constante ou referenciar um node anterior. Referencias para o proprio node ou para nodes futuros sao ignoradas no runtime.", MessageType.None);
                y += GraphHelpBoxHeight + spacing;

                y = DrawRootField(position.x, position.width, y, line, property, "offsetRootNodeIndex", "Offset Root", count);
                y = DrawRootField(position.x, position.width, y, line, property, "factorRootNodeIndex", "Factor Root", count);
                y = DrawRootField(position.x, position.width, y, line, property, "jaggednessRootNodeIndex", "Jaggedness Root", count);

                for (int i = 0; i < count; i++)
                {
                    SerializedProperty nodeProp = GetNodeProperty(property, i);
                    Rect nodeRect = new Rect(position.x, y, position.width, EditorGUI.GetPropertyHeight(nodeProp, true));
                    EditorGUI.PropertyField(nodeRect, nodeProp, new GUIContent($"Node {i}"), true);
                    y += nodeRect.height + spacing;
                }
            }
        }

        EditorGUI.EndProperty();
    }

    private static float DrawRootField(float x, float width, float y, float line, SerializedProperty property, string relativeName, string label, int count)
    {
        SerializedProperty rootProp = property.FindPropertyRelative(relativeName);
        Rect rect = new Rect(x, y, width, line);
        int[] values = new int[count + 1];
        GUIContent[] options = new GUIContent[count + 1];
        values[0] = -1;
        options[0] = new GUIContent("None");
        for (int i = 0; i < count; i++)
        {
            values[i + 1] = i;
            options[i + 1] = new GUIContent($"Node {i}");
        }

        rootProp.intValue = EditorGUI.IntPopup(rect, new GUIContent(label), rootProp.intValue, options, values);
        return y + line + EditorGUIUtility.standardVerticalSpacing;
    }

    private static SerializedProperty GetNodeProperty(SerializedProperty property, int index)
    {
        return property.FindPropertyRelative($"node{index}");
    }

    private static bool GetGraphExpanded(SerializedProperty property)
    {
        return !GraphFoldouts.TryGetValue(property.propertyPath, out bool expanded) || expanded;
    }

    private static void SetGraphExpanded(SerializedProperty property, bool expanded)
    {
        GraphFoldouts[property.propertyPath] = expanded;
    }

    private static void NormalizeGraphState(SerializedProperty property)
    {
        SerializedProperty countProp = property.FindPropertyRelative("graphNodeCount");
        int count = Mathf.Clamp(countProp.intValue, 0, TerrainSplineShaperSettings.MaxGraphNodes);
        countProp.intValue = count;

        for (int i = count; i < TerrainSplineShaperSettings.MaxGraphNodes; i++)
            ClearNode(GetNodeProperty(property, i));

        ClampRoot(property.FindPropertyRelative("offsetRootNodeIndex"), count);
        ClampRoot(property.FindPropertyRelative("factorRootNodeIndex"), count);
        ClampRoot(property.FindPropertyRelative("jaggednessRootNodeIndex"), count);
    }

    private static void ClampRoot(SerializedProperty rootProp, int count)
    {
        if (rootProp.intValue >= count)
            rootProp.intValue = -1;
        if (rootProp.intValue < -1)
            rootProp.intValue = -1;
    }

    private static void ClearGraph(SerializedProperty property)
    {
        property.FindPropertyRelative("graphNodeCount").intValue = 0;
        property.FindPropertyRelative("offsetRootNodeIndex").intValue = -1;
        property.FindPropertyRelative("factorRootNodeIndex").intValue = -1;
        property.FindPropertyRelative("jaggednessRootNodeIndex").intValue = -1;

        for (int i = 0; i < TerrainSplineShaperSettings.MaxGraphNodes; i++)
            ClearNode(GetNodeProperty(property, i));
    }

    private static void ClearNode(SerializedProperty nodeProp)
    {
        nodeProp.FindPropertyRelative("enabled").boolValue = false;
        nodeProp.FindPropertyRelative("input").enumValueIndex = 0;
        nodeProp.FindPropertyRelative("smoothing").floatValue = 1f;
        nodeProp.FindPropertyRelative("pointCount").intValue = 0;
        for (int i = 0; i < TerrainSplineGraphNode.MaxPoints; i++)
            ClearGraphPoint(nodeProp.FindPropertyRelative($"point{i}"));
    }

    private static void ClearGraphPoint(SerializedProperty pointProp)
    {
        pointProp.FindPropertyRelative("location").floatValue = 0f;
        pointProp.FindPropertyRelative("value").floatValue = 0f;
        pointProp.FindPropertyRelative("derivative").floatValue = 0f;
        pointProp.FindPropertyRelative("splitTangents").boolValue = false;
        pointProp.FindPropertyRelative("derivativeIn").floatValue = 0f;
        pointProp.FindPropertyRelative("derivativeOut").floatValue = 0f;
        pointProp.FindPropertyRelative("childNodeIndex").intValue = -1;
    }
}
