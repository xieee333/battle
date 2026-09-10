using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Vision.Geometry;
using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Recognition;

/// <summary>
/// Locates occupied board cards from the blue card aura.  Board cards are
/// centered as a group when their count changes, so the calibrated seven-slot
/// grid is not sufficient on its own.
/// </summary>
public static class CardZoneSlotDetector
{
    public static IReadOnlyList<NormalizedRect> DetectBoard(
        Mat frame,
        NormalizedRect boardRegion,
        IReadOnlyList<NormalizedRect> calibratedSlots)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(calibratedSlots);
        if (frame.Empty() || boardRegion.Width <= 0 || boardRegion.Height <= 0 || calibratedSlots.Count == 0)
            return [];

        // The seven positions are only candidate probes.  The returned list
        // contains occupied cards, so empty board cells never become cards in
        // the snapshot.  Their original x-coordinate is retained and mapped
        // back to the calibrated slot index by TemplateLayoutRecognizer.
        var occupancyDetector = new CardOccupancyDetector();
        var occupied = new List<NormalizedRect>();
        foreach (var slot in calibratedSlots)
        {
            var pixels = slot.ToPixels(frame.Width, frame.Height);
            if (!pixels.IsWithin(frame.Width, frame.Height))
                continue;
            using var crop = new Mat(frame, new Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height));
            if (occupancyDetector.Detect(crop, CardZone.Board).IsOccupied)
                occupied.Add(slot);
        }

        return occupied;
    }

    public static IReadOnlyList<NormalizedRect> DetectHand(
        Mat frame,
        NormalizedRect handRegion,
        IReadOnlyList<NormalizedRect> calibratedSlots)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(calibratedSlots);
        if (frame.Empty() || handRegion.Width <= 0 || handRegion.Height <= 0 || calibratedSlots.Count == 0)
            return calibratedSlots;

        var auraSlots = DetectHandByAuraSpan(frame, handRegion, calibratedSlots);
        if (auraSlots.Count > 0)
            return auraSlots;

        var circleSlots = DetectHandByCostCircles(frame, handRegion, calibratedSlots);
        if (circleSlots.Count > 0)
            return circleSlots;

        var pixels = handRegion.ToPixels(frame.Width, frame.Height);
        if (!pixels.IsWithin(frame.Width, frame.Height))
            return calibratedSlots;

        using var source = new Mat(frame, new Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height));
        using var bgr = new Mat();
        using var hsv = new Mat();
        using var mask = new Mat();
        if (source.Channels() == 1)
            Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
        else
            source.CopyTo(bgr);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        // Hand cards have a green/cyan hover rim and the blue card edge.  The
        // dark game board is intentionally below the saturation threshold.
        Cv2.InRange(hsv, new Scalar(35, 90, 70), new Scalar(125, 255, 255), mask);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var components = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        var centers = new List<double>();
        for (var component = 1; component < components; component++)
        {
            var area = stats.At<int>(component, (int)ConnectedComponentsTypes.Area);
            var width = stats.At<int>(component, (int)ConnectedComponentsTypes.Width);
            var height = stats.At<int>(component, (int)ConnectedComponentsTypes.Height);
            var centerX = centroids.At<double>(component, 0);
            var centerY = centroids.At<double>(component, 1);
            if (area < 180 || width < 12 || width > pixels.Width * 0.60
                || height < 18 || height > pixels.Height * 0.98
                || centerY < pixels.Height * 0.10)
                continue;
            centers.Add(centerX + pixels.X);
        }

        var minimumDistance = Math.Max(35, frame.Width * 0.035);
        var distinctCenters = centers
            .OrderBy(center => center)
            .Aggregate(new List<double>(), (result, center) =>
            {
                if (result.Count == 0 || center - result[^1] >= minimumDistance)
                    result.Add(center);
                else
                    result[^1] = (result[^1] + center) / 2;
                return result;
            });

        if (distinctCenters.Count == 0 || distinctCenters.Count > calibratedSlots.Count)
            return calibratedSlots;

        var slotWidth = (int)Math.Round(calibratedSlots.Average(slot => slot.Width * frame.Width) * 2.55);
        slotWidth = Math.Clamp(slotWidth, frame.Width / 16, frame.Width / 7);
        var slotHeight = Math.Clamp((int)Math.Round(frame.Height * 0.17), 40, frame.Height - pixels.Y);
        var y = Math.Clamp(pixels.Y + (int)Math.Round(frame.Height * 0.025), 0, frame.Height - slotHeight);
        var maxX = frame.Width - slotWidth;
        return distinctCenters.Select(center =>
        {
            var x = Math.Clamp((int)Math.Round(center - slotWidth / 2d), 0, maxX);
            return new NormalizedRect(
                (double)x / frame.Width,
                (double)y / frame.Height,
                (double)slotWidth / frame.Width,
                (double)slotHeight / frame.Height);
        }).ToArray();
    }

    private static IReadOnlyList<NormalizedRect> DetectHandByAuraSpan(
        Mat frame,
        NormalizedRect handRegion,
        IReadOnlyList<NormalizedRect> calibratedSlots)
    {
        var frameWidth = frame.Width;
        var frameHeight = frame.Height;
        var pixels = handRegion.ToPixels(frameWidth, frameHeight);
        if (!pixels.IsWithin(frameWidth, frameHeight))
            return [];

        using var source = new Mat(frame, new Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height));
        using var bgr = new Mat();
        using var hsv = new Mat();
        using var mask = new Mat();
        if (source.Channels() == 1)
            Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
        else
            source.CopyTo(bgr);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(35, 90, 70), new Scalar(125, 255, 255), mask);

        var top = Math.Clamp((int)Math.Round(pixels.Height * 0.10), 0, pixels.Height - 1);
        var height = Math.Max(1, Math.Min((int)Math.Round(pixels.Height * 0.38), pixels.Height - top));
        var runs = new List<(int Start, int End)>();
        var runStart = -1;
        var maskWidth = mask.Width;
        for (var x = 0; x < maskWidth; x++)
        {
            using var column = new Mat(mask, new Rect(x, top, 1, height));
            var hits = Cv2.CountNonZero(column);
            if (hits >= 4 && runStart < 0)
                runStart = x;
            if ((hits < 4 || x == maskWidth - 1) && runStart >= 0)
            {
                var end = hits < 4 ? x - 1 : x;
                if (end - runStart >= frameWidth * 0.02)
                    runs.Add((runStart + pixels.X, end + pixels.X));
                runStart = -1;
            }
        }

        if (runs.Count == 0)
            return [];
        var merged = new List<(int Start, int End)>();
        foreach (var run in runs)
        {
            if (merged.Count == 0 || run.Start - merged[^1].End > frameWidth * 0.02)
                merged.Add(run);
            else
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, run.End));
        }

        var span = merged.OrderByDescending(run => run.End - run.Start).First();
        if (span.End - span.Start < frameWidth * 0.18)
            return [];
        var cardWidth = Math.Clamp((int)Math.Round(frameWidth * 0.075), frameWidth / 18, frameWidth / 7);
        var defaultSpacing = frameWidth * 0.075;
        var count = Math.Clamp((int)Math.Round((span.End - span.Start - cardWidth) / defaultSpacing) + 1,
            1, calibratedSlots.Count);
        if (count <= 0)
            return [];
        var spacing = count == 1 ? 0 : (span.End - span.Start - cardWidth) / (double)(count - 1);
        if (spacing < frameWidth * 0.045 || spacing > frameWidth * 0.12)
            return [];

        var heightPixels = Math.Clamp((int)Math.Round(frameHeight * 0.15), 50, frameHeight - pixels.Y);
        var y = Math.Clamp((int)Math.Round(frameHeight * 0.86), pixels.Y, frameHeight - heightPixels);
        return Enumerable.Range(0, count).Select(index =>
        {
            var x = (int)Math.Round(span.Start + index * spacing);
            x = Math.Clamp(x, 0, frameWidth - cardWidth);
            return new NormalizedRect(
                (double)x / frameWidth,
                (double)y / frameHeight,
                (double)cardWidth / frameWidth,
                (double)heightPixels / frameHeight);
        }).ToArray();
    }

    private static IReadOnlyList<NormalizedRect> DetectHandByCostCircles(
        Mat frame,
        NormalizedRect handRegion,
        IReadOnlyList<NormalizedRect> calibratedSlots)
    {
        var frameWidth = frame.Width;
        var frameHeight = frame.Height;
        var pixels = handRegion.ToPixels(frame.Width, frame.Height);
        if (!pixels.IsWithin(frame.Width, frame.Height))
            return [];

        using var source = new Mat(frame, new Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height));
        using var gray = new Mat();
        if (source.Channels() == 1)
            source.CopyTo(gray);
        else
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.MedianBlur(gray, blurred, 5);
        var circles = Cv2.HoughCircles(
            blurred,
            HoughModes.Gradient,
            dp: 1,
            minDist: Math.Max(35, frameWidth * 0.045),
            param1: 80,
            param2: 20,
            minRadius: 10,
            maxRadius: 30);

        var centers = circles
            .Where(circle => circle.Radius is >= 12 and <= 30
                && circle.Center.Y >= pixels.Height * 0.16
                && circle.Center.Y <= pixels.Height * 0.62)
            .Select(circle => circle.Center.X + pixels.X)
            .OrderBy(center => center)
            .Aggregate(new List<double>(), (result, center) =>
            {
                if (result.Count == 0 || center - result[^1] >= frameWidth * 0.025)
                    result.Add(center);
                else
                    result[^1] = (result[^1] + center) / 2;
                return result;
            });
        if (centers.Count < 2)
            return [];

        var spacings = centers.Zip(centers.Skip(1), (left, right) => right - left)
            .Where(spacing => spacing >= frameWidth * 0.065 && spacing <= frameWidth * 0.12)
            .OrderBy(value => value)
            .ToArray();
        if (spacings.Length == 0)
            return [];
        var spacingMedian = spacings[spacings.Length / 2];
        var bestSequence = new List<double>();
        foreach (var start in centers)
        {
            var sequence = new List<double> { start };
            var previous = start;
            foreach (var center in centers.Where(center => center > start))
            {
                if (Math.Abs(center - previous - spacingMedian) <= spacingMedian * 0.22)
                {
                    sequence.Add(center);
                    previous = center;
                }
            }

            if (sequence.Count > bestSequence.Count)
                bestSequence = sequence;
        }

        if (bestSequence.Count < 2)
            return [];

        var first = bestSequence[0];
        var inferred = new List<double>(bestSequence);
        while (bestSequence.Count == 2 && inferred.Count < calibratedSlots.Count
            && first - spacingMedian >= pixels.X - frameWidth * 0.01)
        {
            first -= spacingMedian;
            inferred.Insert(0, first);
        }

        // Cost circles are at the upper-left of each card.  Shift to the
        // card center before producing an overlapped full-card crop.
        var width = Math.Clamp((int)Math.Round(frameWidth * 0.075), frameWidth / 18, frameWidth / 7);
        var height = Math.Clamp((int)Math.Round(frameHeight * 0.15), 50, frameHeight - pixels.Y);
        var y = Math.Clamp((int)Math.Round(frameHeight * 0.86), pixels.Y, frameHeight - height);
        var centerOffset = width * 0.25;
        return inferred.Select(center =>
        {
            var x = Math.Clamp((int)Math.Round(center + centerOffset - width / 2d), 0, frameWidth - width);
            return new NormalizedRect(
                (double)x / frameWidth,
                (double)y / frameHeight,
                (double)width / frameWidth,
                (double)height / frameHeight);
        }).ToArray();
    }

    private static IReadOnlyList<NormalizedRect> DetectAuraSlots(
        Mat frame,
        NormalizedRect region,
        IReadOnlyList<NormalizedRect> calibratedSlots,
        AuraKind kind)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(calibratedSlots);
        if (frame.Empty() || region.Width <= 0 || region.Height <= 0 || calibratedSlots.Count == 0)
            return calibratedSlots;

        var pixels = region.ToPixels(frame.Width, frame.Height);
        if (!pixels.IsWithin(frame.Width, frame.Height))
            return calibratedSlots;

        using var source = new Mat(frame, new Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height));
        using var bgr = new Mat();
        using var hsv = new Mat();
        using var mask = new Mat();
        if (source.Channels() == 1)
            Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
        else
            source.CopyTo(bgr);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        if (kind == AuraKind.Board)
        {
            // The blue/cyan rim is present on normal, golden and buffed
            // board minions.  Keep the mask narrow enough to ignore the brown
            // board while allowing the cyan animation glow.
            Cv2.InRange(hsv, new Scalar(75, 75, 65), new Scalar(125, 255, 255), mask);
        }
        else
        {
            Cv2.InRange(hsv, new Scalar(35, 75, 65), new Scalar(125, 255, 255), mask);
        }

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        Cv2.Dilate(mask, mask, kernel);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var components = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        var centers = new List<double>();
        var calibratedPixels = calibratedSlots
            .Select(slot => slot.ToPixels(frame.Width, frame.Height))
            .ToArray();
        var slotWidth = (int)Math.Round(calibratedPixels.Average(slot => slot.Width));
        var slotHeight = (int)Math.Round(calibratedPixels.Average(slot => slot.Height));
        var minimumWidth = Math.Max(25, slotWidth / 3);
        var maximumWidth = Math.Max(minimumWidth + 1, (int)(slotWidth * 1.45));
        var minimumHeight = Math.Max(45, slotHeight / 3);
        var maximumHeight = Math.Max(minimumHeight + 1, (int)(slotHeight * 1.45));

        for (var component = 1; component < components; component++)
        {
            var area = stats.At<int>(component, (int)ConnectedComponentsTypes.Area);
            var width = stats.At<int>(component, (int)ConnectedComponentsTypes.Width);
            var height = stats.At<int>(component, (int)ConnectedComponentsTypes.Height);
            var centerX = centroids.At<double>(component, 0);
            var centerY = centroids.At<double>(component, 1);
            if (area < Math.Max(350, slotWidth * slotHeight / 30)
                || width < minimumWidth || width > maximumWidth
                || height < minimumHeight || height > maximumHeight
                || centerY < pixels.Height * 0.22 || centerY > pixels.Height * 0.92)
                continue;

            centers.Add(centerX + pixels.X);
        }

        var minimumDistance = calibratedPixels.Length > 1
            ? calibratedPixels.Zip(calibratedPixels.Skip(1), (left, right) => (double)(right.X - left.X))
                .DefaultIfEmpty(slotWidth * 1.1).Min() * 0.45
            : slotWidth * 0.7;
        var distinctCenters = centers
            .OrderBy(center => center)
            .Aggregate(new List<double>(), (result, center) =>
            {
                if (result.Count == 0 || center - result[^1] >= minimumDistance)
                    result.Add(center);
                else
                    result[^1] = (result[^1] + center) / 2;
                return result;
            });

        if (distinctCenters.Count == 0 || distinctCenters.Count > calibratedSlots.Count)
            return calibratedSlots;

        var y = calibratedPixels.Min(slot => slot.Y);
        var maxX = pixels.X + pixels.Width - slotWidth;
        return distinctCenters.Select(center =>
        {
            var x = Math.Clamp((int)Math.Round(center - slotWidth / 2d), pixels.X, maxX);
            return new NormalizedRect(
                (double)x / frame.Width,
                (double)y / frame.Height,
                (double)slotWidth / frame.Width,
                (double)slotHeight / frame.Height);
        }).ToArray();
    }

    private enum AuraKind
    {
        Board,
        Hand
    }

}
