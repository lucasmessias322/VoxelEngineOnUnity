using System;
using Unity.Burst;
using Unity.Mathematics;

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

        for (int i = 1; i < points.Length; i++)
        {
            TerrainSplinePoint candidate = points[i];
            int j = i - 1;
            while (j >= 0 && points[j].location > candidate.location)
            {
                points[j + 1] = points[j];
                j--;
            }

            points[j + 1] = candidate;
        }

        for (int i = 0; i < points.Length; i++)
        {
            TerrainSplinePoint point = points[i];
            if (i > 0 && point.location <= points[i - 1].location)
                point.location = points[i - 1].location + 1e-4f;

            sanitized.SetPoint(i, point);
        }

        return sanitized;
    }
}

[Serializable]
public struct TerrainSplineShaperSettings
{
    public bool enabled;
    public TerrainSpline offsetSpline;
    public TerrainSpline factorSpline;
    public TerrainSpline jaggednessSpline;

    public bool HasAnyControlPoints =>
        math.clamp(offsetSpline.pointCount, 0, TerrainSpline.MaxPoints) > 0 ||
        math.clamp(factorSpline.pointCount, 0, TerrainSpline.MaxPoints) > 0 ||
        math.clamp(jaggednessSpline.pointCount, 0, TerrainSpline.MaxPoints) > 0;

    public static TerrainSplineShaperSettings Disabled => new TerrainSplineShaperSettings
    {
        enabled = false,
        offsetSpline = default,
        factorSpline = default,
        jaggednessSpline = default
    };

    public static TerrainSplineShaperSettings MinecraftModernDefault => new TerrainSplineShaperSettings
    {
        enabled = true,
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

    public TerrainSplineShaperSettings Sanitized()
    {
        TerrainSplineShaperSettings sanitized = this;
        sanitized.offsetSpline = offsetSpline.Sanitized();
        sanitized.factorSpline = factorSpline.Sanitized();
        sanitized.jaggednessSpline = jaggednessSpline.Sanitized();

        bool hasAnySpline =
            sanitized.offsetSpline.enabled ||
            sanitized.factorSpline.enabled ||
            sanitized.jaggednessSpline.enabled;

        sanitized.enabled &= hasAnySpline;
        return sanitized;
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
                float smooth = EvaluateHermite(previous, current, delta, t);
                return math.lerp(linear, smooth, math.saturate(spline.smoothing));
            }

            previous = current;
        }

        return previous.value;
    }

    [BurstCompile]
    private static float EvaluateHermite(in TerrainSplinePoint start, in TerrainSplinePoint end, float delta, float t)
    {
        float tt = t * t;
        float ttt = tt * t;
        float h00 = 2f * ttt - 3f * tt + 1f;
        float h10 = ttt - 2f * tt + t;
        float h01 = -2f * ttt + 3f * tt;
        float h11 = ttt - tt;

        return h00 * start.value +
               h10 * delta * start.GetDerivativeOut() +
               h01 * end.value +
               h11 * delta * end.GetDerivativeIn();
    }

}
